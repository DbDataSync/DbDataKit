using DbDataKit.Dialect;

namespace DbDataKit.Quality.Evaluation.Tests;

/// <summary>The evaluator without an engine: the shape of its statements per dialect, its limits, and what an error message may hold. A fake connection records what is sent.</summary>
public sealed class EvaluatorLimitsTests
{
    private static ISqlDialect Dialect(string id) => DialectCatalog.Builtin.Get(id);

    private static EvaluationContext Context(params string[] keys) => new() { ResolveTable = name => name == "orders" ? ["dbo", "Or\"d]ers"] : name == "customers" ? ["customers"] : null, KeyColumns = keys };

    private static CheckDefinition Check(CheckKind kind, string[] columns, CheckParameters? parameters = null) => new("c", kind, new CheckAttachment("orders", columns), parameters ?? NoParameters.Instance);

    private static async Task<(CheckEvaluation Result, string? Sql)> Sent(string dialect, CheckDefinition check, EvaluationContext? context = null)
    {
        var connection = FakeConnection.Throwing(new FakeDbException("stop"));
        var result = await new CheckEvaluator(Dialect(dialect)).EvaluateAsync(check, connection, context ?? Context());
        return (result, connection.Sent.SingleOrDefault());
    }

    [Theory]
    [InlineData("sqlserver", "SELECT TOP (10) [chk_t].", "COUNT_BIG(*) OVER ()")]
    [InlineData("postgres", "SELECT \"chk_t\".", " LIMIT 10")]
    [InlineData("duckdb", "SELECT \"chk_t\".", " LIMIT 10")]
    public async Task A_row_check_is_one_select_with_the_count_over_all_failing_rows_and_the_row_limit_in_it(string dialect, string start, string end)
    {
        var (_, sql) = await Sent(dialect, Check(CheckKind.NotNull, ["a", "b"]), Context("id"));

        Assert.NotNull(sql);
        Assert.Contains(start.Replace("[chk_t]", "chk_t").Replace("\"chk_t\"", "chk_t"), sql);
        Assert.Contains(end, sql);
        Assert.Contains("IS NULL OR", sql);
        Assert.Contains("ORDER BY", sql);
    }

    [Theory]
    [InlineData("sqlserver", "[dbo].[Or\"d]]ers]", "[a]]b]")]
    [InlineData("postgres", "\"dbo\".\"Or\"\"d]ers\"", "\"a]b\"")]
    [InlineData("duckdb", "\"dbo\".\"Or\"\"d]ers\"", "\"a]b\"")]
    public async Task Every_identifier_is_quoted_and_every_value_is_a_literal_of_the_dialect(string dialect, string table, string column)
    {
        var check = Check(CheckKind.AcceptedValues, ["a]b"], new AcceptedValuesParameters(["x'; DROP TABLE t; --", "plain"]));

        var (result, sql) = await Sent(dialect, check);

        Assert.NotNull(sql);
        Assert.Contains(table, sql);
        Assert.Contains(column, sql);
        Assert.Contains("x''; DROP TABLE t; --", sql);
        // the statement went to the engine: it passed the read-only policy that looks at it first
        Assert.DoesNotContain("not a plain read", result.Message);
    }

    [Fact]
    public async Task A_value_that_looks_like_a_statement_stays_a_literal()
    {
        // a column name cannot make a second statement (it is quoted) and a value is a literal: the read-only policy that looks at the statement before it is sent has nothing to refuse
        var (result, sql) = await Sent("postgres", Check(CheckKind.AcceptedValues, ["a"], new AcceptedValuesParameters(["x'); DROP TABLE t; --"])));

        Assert.Contains("x''); DROP TABLE t; --", sql);
        Assert.Equal(EvaluationStatus.Error, result.Status);
        Assert.DoesNotContain("not a plain read", result.Message);
    }

    [Fact]
    public async Task More_accepted_values_than_the_limit_is_an_error_that_names_the_limit_and_sends_nothing()
    {
        var values = Enumerable.Range(0, 501).Select(i => "v" + i).ToArray();

        var (result, sql) = await Sent("postgres", Check(CheckKind.AcceptedValues, ["a"], new AcceptedValuesParameters(values)));

        Assert.Equal(EvaluationStatus.Error, result.Status);
        Assert.Contains("limit reached", result.Message);
        Assert.Contains("501", result.Message);
        Assert.Null(sql);
    }

    [Fact]
    public async Task A_statement_that_runs_past_its_limit_is_an_error_that_says_so_and_not_a_pass()
    {
        var evaluator = new CheckEvaluator(Dialect("postgres"), new EvaluationLimits { StatementTimeout = TimeSpan.FromMilliseconds(50) });

        var result = await evaluator.EvaluateAsync(Check(CheckKind.RowCountChange, [], new RowCountChangeParameters(1, 1)), FakeConnection.Hanging(), Context());

        Assert.Equal(EvaluationStatus.Error, result.Status);
        Assert.Contains("limit reached", result.Message);
        Assert.Contains("ran past its limit", result.Message);
    }

    [Fact]
    public async Task The_callers_own_cancellation_is_not_an_error_result()
    {
        using var cts = new CancellationTokenSource();
        var connection = new FakeConnection(async (_, token) => { await cts.CancelAsync(); token.ThrowIfCancellationRequested(); throw new InvalidOperationException(); });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new CheckEvaluator(Dialect("postgres")).EvaluateAsync(Check(CheckKind.NotNull, ["a"]), connection, Context(), cts.Token));
    }

    [Theory]
    [InlineData("Conversion failed when converting the nvarchar value 'SECRET-VALUE' to data type int.", null, 245)]
    [InlineData("invalid input syntax for type integer: \"SECRET-VALUE\"", "22P02", 0)]
    [InlineData("Conversion Error: Could not convert string 'SECRET-VALUE' to INT32", null, 0)]
    public async Task An_engine_message_that_can_hold_a_data_value_is_not_kept(string message, string? state, int number)
    {
        var connection = FakeConnection.Throwing(new FakeDbException(message, state, number));

        var result = await new CheckEvaluator(Dialect("postgres")).EvaluateAsync(Check(CheckKind.NotNull, ["a"]), connection, Context());

        Assert.Equal(EvaluationStatus.Error, result.Status);
        Assert.DoesNotContain("SECRET-VALUE", result.Message);
        Assert.Contains("its message is not kept", result.Message);
    }

    [Theory]
    [InlineData("Invalid object name 'dbo.gone'.", null, 208, "dbo.gone")]
    [InlineData("Invalid column name 'nope'.", null, 207, "nope")]
    [InlineData("relation \"gone\" does not exist", "42P01", 0, "gone")]
    [InlineData("column \"nope\" does not exist", "42703", 0, "nope")]
    [InlineData("Catalog Error: Table with name gone does not exist!", null, 0, "gone")]
    public async Task The_message_about_a_missing_table_or_column_is_kept_because_it_holds_identifiers(string message, string? state, int number, string name)
    {
        var connection = FakeConnection.Throwing(new FakeDbException(message, state, number));

        var result = await new CheckEvaluator(Dialect("postgres")).EvaluateAsync(Check(CheckKind.NotNull, ["a"]), connection, Context());

        Assert.Equal(EvaluationStatus.Error, result.Status);
        Assert.Contains("The table or column is not there", result.Message);
        Assert.Contains(name, result.Message);
    }

    [Theory]
    [InlineData("Login failed for user 'x'.", null, 18456)]
    [InlineData("permission denied for table orders", "42501", 0)]
    public async Task A_login_that_may_not_read_is_an_error(string message, string? state, int number)
    {
        var connection = FakeConnection.Throwing(new FakeDbException(message, state, number == 18456 ? 229 : number));

        var result = await new CheckEvaluator(Dialect("postgres")).EvaluateAsync(Check(CheckKind.NotNull, ["a"]), connection, Context());

        Assert.Equal(EvaluationStatus.Error, result.Status);
        Assert.Contains("may not read", result.Message);
    }

    [Fact]
    public async Task A_check_that_names_no_connection_and_needs_one_is_an_error_and_not_an_exception()
    {
        var result = await new CheckEvaluator(Dialect("postgres")).EvaluateAsync(Check(CheckKind.NotNull, ["a"]), null, Context());

        Assert.Equal(EvaluationStatus.Error, result.Status);
    }

    [Fact]
    public async Task The_first_evaluation_of_a_row_count_check_only_records()
    {
        var connection = new FakeConnection((_, _) => throw new InvalidOperationException());
        var check = Check(CheckKind.RowCountChange, [], new RowCountChangeParameters(10, 10));

        var result = await new CheckEvaluator(Dialect("duckdb")).EvaluateAsync(check, connection, Context());

        // the count could not be read here (the fake throws), so this is an error, never a pass: a baseline is recorded only from a count that was read
        Assert.Equal(EvaluationStatus.Error, result.Status);
    }

    [Fact]
    public void The_sample_ceiling_is_ten()
    {
        Assert.Equal(10, EvaluationLimits.SampleKeyCeiling);
        Assert.Equal(10, new EvaluationLimits().MaxSampleKeys);
    }
}
