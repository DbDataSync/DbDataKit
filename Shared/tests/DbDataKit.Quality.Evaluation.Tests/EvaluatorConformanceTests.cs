using System.Globalization;
using DbDataKit.Dialect;

namespace DbDataKit.Quality.Evaluation.Tests;

/// <summary>
/// The conformance suite of the evaluator (plan 23a FQ07): the same checks over the same rows give the same result on SQL Server, PostgreSQL and DuckDB. The columns are named to be hostile (a space, a double quote, a reserved word, mixed case), the checks run over
/// the provider's read-only connection, and a connection that is down, a missing table or a missing column is an <c>error</c>.
/// </summary>
public sealed class EvaluatorConformanceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 11, 0, 0, TimeSpan.Zero);

    private const string Id = "Id", Group = "Group", Customer = "MixedCase", Amount = "a \"quote\"", Loaded = "Loaded At";

    public static TheoryData<string> Engines => EvaluationEngine.All;

    private static IReadOnlyList<(string, CanonicalType, bool)> OrderColumns =>
    [
        (Id, CanonicalType.Of(CanonicalKind.Int32), false),
        (Group, CanonicalType.String(50), true),
        (Customer, CanonicalType.Of(CanonicalKind.Int32), true),
        (Amount, CanonicalType.Decimal(10, 2), true),
        (Loaded, CanonicalType.Timestamp(), false),
    ];

    private static DateTime Oct(int day) => new(2026, 10, day, 8, 0, 0, DateTimeKind.Unspecified);

    // ten rows with one thing wrong in each of several: row 4 has a group that is not accepted, row 5 a null and an amount below zero, row 6 a customer that does not exist and an amount above 500, MixedCase repeats
    private static IEnumerable<object?[]> Orders()
    {
        yield return [1, "open", 1, 10.00m, Oct(1)];
        yield return [2, "closed", 2, 20.00m, Oct(2)];
        yield return [3, "open", 3, 5.00m, new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Unspecified)];
        yield return [4, "weird", 1, 30.50m, Oct(4)];
        yield return [5, "open", null, -5.00m, Oct(5)];
        yield return [6, "open", 99, 1000.00m, Oct(6)];
        yield return [7, "closed", 2, 15.00m, Oct(7)];
        yield return [8, "open", 1, 12.00m, Oct(8)];
        yield return [9, null, 1, 7.00m, Oct(9)];
        yield return [10, "open", 2, null, Oct(10)];
    }

    private static async Task<(string Orders, string Customers)> Arrange(EvaluationEngine engine)
    {
        var orders = engine.Unique("Order Lines");
        var customers = engine.Unique("Customers");
        await engine.Table(orders, OrderColumns, Orders());
        await engine.Table(customers, [(Id, CanonicalType.Of(CanonicalKind.Int32), false), ("Name", CanonicalType.String(50), false)], [[1, "Ada"], [2, "Grace"], [3, "Edsger"]]);
        return (orders, customers);
    }

    private static CheckDefinition Check(string id, CheckKind kind, string[] columns, CheckParameters? parameters = null, string dataset = "orders") =>
        new(id, kind, new CheckAttachment(dataset, columns), parameters ?? NoParameters.Instance);

    private static RangeBound Bound(string text) => RangeBound.TryParse(text, out var bound) ? bound : throw new ArgumentException(text);

    private static List<CheckDefinition> AllChecks() =>
    [
        Check("nn_customer", CheckKind.NotNull, [Customer]),
        Check("nn_customer_keyed", CheckKind.NotNull, [Customer]),
        Check("nn_id", CheckKind.NotNull, [Id]),
        Check("uq_customer", CheckKind.Unique, [Customer]),
        Check("uq_id", CheckKind.Unique, [Id]),
        Check("uq_pair", CheckKind.Unique, [Customer, Group]),
        Check("av_fail", CheckKind.AcceptedValues, [Group], new AcceptedValuesParameters(["open", "closed"])),
        Check("av_fail_keyed", CheckKind.AcceptedValues, [Group], new AcceptedValuesParameters(["open", "closed"])),
        Check("av_pass", CheckKind.AcceptedValues, [Group], new AcceptedValuesParameters(["open", "closed", "weird"])),
        Check("av_hostile_value", CheckKind.AcceptedValues, [Group], new AcceptedValuesParameters(["open", "closed", "weird", "x'; DROP TABLE t; --"])),
        Check("range_fail", CheckKind.Range, [Amount], new RangeParameters(Bound("0"), Bound("500"))),
        Check("range_pass", CheckKind.Range, [Amount], new RangeParameters(Bound("-10"), Bound("2000.5"))),
        Check("range_min_only", CheckKind.Range, [Amount], new RangeParameters(Bound("6"), null)),
        Check("range_date_fail_keyed", CheckKind.Range, [Loaded], new RangeParameters(null, Bound("2026-10-05"))),
        Check("range_date_pass", CheckKind.Range, [Loaded], new RangeParameters(Bound("2026-01-01"), Bound("2026-12-31T00:00:00"))),
        Check("ref_fail", CheckKind.Referential, [Customer], new ReferentialParameters("customers", [Id])),
        Check("ref_fail_keyed", CheckKind.Referential, [Customer], new ReferentialParameters("customers", [Id])),
        Check("ref_pass", CheckKind.Referential, [Id], new ReferentialParameters("orders", [Id])),
        Check("fresh_pass", CheckKind.Freshness, [Loaded], new FreshnessParameters(TimeSpan.FromDays(1))),
        Check("fresh_fail", CheckKind.Freshness, [Loaded], new FreshnessParameters(TimeSpan.FromHours(6))),
        Check("fresh_load_fail", CheckKind.Freshness, [], new FreshnessParameters(TimeSpan.FromHours(1))),
        Check("fresh_load_unknown", CheckKind.Freshness, [], new FreshnessParameters(TimeSpan.FromHours(1))),
        Check("rc_first", CheckKind.RowCountChange, [], new RowCountChangeParameters(50, 25)),
        Check("rc_same", CheckKind.RowCountChange, [], new RowCountChangeParameters(50, 25)),
        Check("rc_up", CheckKind.RowCountChange, [], new RowCountChangeParameters(50, 25)),
        Check("rc_down", CheckKind.RowCountChange, [], new RowCountChangeParameters(50, 25)),
        Check("rc_from_empty", CheckKind.RowCountChange, [], new RowCountChangeParameters(50, null)),
    ];

    private static EvaluationContext ContextFor(CheckDefinition check, string orders, string customers) => new()
    {
        ResolveTable = name => name switch { "orders" => [orders], "customers" => [customers], _ => null },
        KeyColumns = check.Id.EndsWith("_keyed", StringComparison.Ordinal) ? [Id] : [],
        LastLoadedAt = check.Id == "fresh_load_fail" ? Now - TimeSpan.FromHours(2) : null,
        PreviousRowCount = check.Id switch { "rc_same" => 10, "rc_up" => 5, "rc_down" => 20, "rc_from_empty" => 0, _ => null },
    };

    private static string Signature(CheckEvaluation e)
    {
        static string N(decimal? v) => v is null ? "" : v.Value.ToString("0.####", CultureInfo.InvariantCulture);
        return $"{e.CheckId}|{e.Status}|{N(e.Measured)}|{N(e.Threshold)}|{string.Join(';', e.SampleKeys)}|{N(e.Observed)}";
    }

    /// <summary>What every engine must answer, for these rows. One list for all three engines is the claim.</summary>
    private static readonly string[] Expected =
    [
        "nn_customer|Fail|1|0||",
        "nn_customer_keyed|Fail|1|0|Id=5|",
        "nn_id|Pass|0|0||",
        "uq_customer|Fail|7|0|MixedCase=1;MixedCase=2|",
        "uq_id|Pass|0|0||",
        "uq_pair|Fail|4|0|MixedCase=1, Group=open;MixedCase=2, Group=closed|",
        "av_fail|Fail|1|0|Group=weird|",
        "av_fail_keyed|Fail|1|0|Id=4|",
        "av_pass|Pass|0|0||",
        "av_hostile_value|Pass|0|0||",
        "range_fail|Fail|2|0|a \"quote\"=-5.00;a \"quote\"=1000.00|",
        "range_pass|Pass|0|0||",
        "range_min_only|Fail|2|0|a \"quote\"=-5.00;a \"quote\"=5.00|",
        "range_date_fail_keyed|Fail|7|0|Id=3;Id=5;Id=6;Id=7;Id=8;Id=9;Id=10|",
        "range_date_pass|Pass|0|0||",
        "ref_fail|Fail|1|0|MixedCase=99|",
        "ref_fail_keyed|Fail|1|0|Id=6|",
        "ref_pass|Pass|0|0||",
        "fresh_pass|Pass|82800|86400||",
        "fresh_fail|Fail|82800|21600||",
        "fresh_load_fail|Fail|7200|3600||",
        "fresh_load_unknown|Error||||",
        "rc_first|Pass||||10",
        "rc_same|Pass|0|50||10",
        "rc_up|Fail|100|50||10",
        "rc_down|Fail|-50|25||10",
        "rc_from_empty|Fail||50||10",
    ];

    [Theory, MemberData(nameof(Engines))]
    public async Task The_same_checks_over_the_same_rows_give_the_same_result_on_every_engine(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var (orders, customers) = await Arrange(engine);
        var evaluator = new CheckEvaluator(engine.Dialect, time: new FixedTime(Now));

        var results = await evaluator.EvaluateAsync(AllChecks(), engine.ReadOnlyOpener(), c => ContextFor(c, orders, customers));

        var errors = string.Join('\n', results.Where(r => r.Status == EvaluationStatus.Error && r.CheckId != "fresh_load_unknown").Select(r => $"{r.CheckId}: {r.Message} -- {r.Statement}"));
        Assert.True(errors.Length == 0, errors);
        Assert.Equal(Expected, results.Select(Signature).ToArray());
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task One_statement_is_sent_per_check_and_none_for_a_freshness_check_on_the_last_load(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var (orders, customers) = await Arrange(engine);
        var evaluator = new CheckEvaluator(engine.Dialect, time: new FixedTime(Now));

        var results = await evaluator.EvaluateAsync(AllChecks(), engine.ReadOnlyOpener(), c => ContextFor(c, orders, customers));

        foreach (var result in results)
        {
            if (result.CheckId == "fresh_load_fail" || result.CheckId == "fresh_load_unknown") Assert.Null(result.Statement);
            else
            {
                Assert.NotNull(result.Statement);
                Assert.StartsWith("SELECT", result.Statement, StringComparison.Ordinal);
                // a check with a hostile value has its semicolon inside a literal only
                Assert.True(result.CheckId == "av_hostile_value" || !result.Statement!.Contains(';'), result.Statement);
            }
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_table_given_with_its_schema_works(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var (orders, _) = await Arrange(engine);
        var evaluator = new CheckEvaluator(engine.Dialect);
        var check = Check("nn", CheckKind.NotNull, [Customer]);

        var results = await evaluator.EvaluateAsync([check], engine.ReadOnlyOpener(), _ => new EvaluationContext { ResolveTable = _ => [engine.DefaultSchema, orders] });

        Assert.Equal(EvaluationStatus.Fail, results[0].Status);
        Assert.Equal(1, results[0].Measured);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_connection_that_is_down_is_an_error_and_not_a_fail(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var (orders, customers) = await Arrange(engine);
        var evaluator = new CheckEvaluator(engine.Dialect, time: new FixedTime(Now));
        var checks = new List<CheckDefinition>
        {
            Check("nn_customer", CheckKind.NotNull, [Customer]),
            Check("rc_first", CheckKind.RowCountChange, [], new RowCountChangeParameters(50, 25)),
            Check("fresh_load_fail", CheckKind.Freshness, [], new FreshnessParameters(TimeSpan.FromHours(1))),
        };

        var results = await evaluator.EvaluateAsync(checks, engine.ReadOnlyOpener(down: true), c => ContextFor(c, orders, customers));

        Assert.Equal(EvaluationStatus.Error, results[0].Status);
        Assert.StartsWith("The connection could not be opened", results[0].Message);
        Assert.Equal(results[0].Message, results[1].Message);
        Assert.Null(results[0].Measured);
        // what needs no connection is still answered
        Assert.Equal(EvaluationStatus.Fail, results[2].Status);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_missing_table_or_column_is_an_error_that_names_it(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var (orders, customers) = await Arrange(engine);
        var evaluator = new CheckEvaluator(engine.Dialect);
        var missingTable = Check("t", CheckKind.NotNull, [Customer], dataset: "gone");
        var missingColumn = Check("c", CheckKind.NotNull, ["No Such Column"]);
        var missingTarget = Check("r", CheckKind.Referential, [Customer], new ReferentialParameters("targetless", [Id]));
        var unknownDataset = Check("u", CheckKind.NotNull, [Customer], dataset: "nobody");

        var results = await evaluator.EvaluateAsync([missingTable, missingColumn, missingTarget, unknownDataset], engine.ReadOnlyOpener(), _ => new EvaluationContext
        {
            ResolveTable = name => name switch { "orders" => [orders], "customers" => [customers], "gone" => ["no_such_table_" + Guid.NewGuid().ToString("N")], "targetless" => ["no_such_target"], _ => null },
        });

        Assert.All(results, r => Assert.Equal(EvaluationStatus.Error, r.Status));
        Assert.Contains("The table or column is not there", results[0].Message);
        Assert.Contains("no_such_table_", results[0].Message);
        Assert.Contains("No Such Column", results[1].Message);
        Assert.Contains("no_such_target", results[2].Message);
        Assert.Contains("'nobody' is not known", results[3].Message);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_column_that_cannot_be_compared_is_an_error_that_leaks_no_value(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var (orders, customers) = await Arrange(engine);
        var evaluator = new CheckEvaluator(engine.Dialect);
        var check = Check("range_on_text", CheckKind.Range, [Group], new RangeParameters(Bound("0"), Bound("5")));

        var results = await evaluator.EvaluateAsync([check], engine.ReadOnlyOpener(), c => ContextFor(c, orders, customers));

        Assert.Equal(EvaluationStatus.Error, results[0].Status);
        foreach (var value in new[] { "open", "closed", "weird" }) Assert.DoesNotContain(value, results[0].Message);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_sample_never_holds_more_than_ten_keys_and_the_measure_counts_every_failing_row(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var table = engine.Unique("Many");
        await engine.Table(table, [(Id, CanonicalType.Of(CanonicalKind.Int32), false), ("Secret", CanonicalType.String(50), true), (Customer, CanonicalType.Of(CanonicalKind.Int32), true)],
            Enumerable.Range(1, 25).Select(i => new object?[] { i, "secret-" + i, null }));
        var evaluator = new CheckEvaluator(engine.Dialect);

        var results = await evaluator.EvaluateAsync([Check("nn", CheckKind.NotNull, [Customer], dataset: "many")], engine.ReadOnlyOpener(),
            _ => new EvaluationContext { ResolveTable = _ => [table], KeyColumns = [Id] });

        Assert.Equal(25, results[0].Measured);
        Assert.Equal(10, results[0].SampleKeys.Count);
        Assert.All(results[0].SampleKeys, k => Assert.StartsWith("Id=", k));
        // the statement selects the key and the measure and no other column of the failing rows
        Assert.DoesNotContain("Secret", results[0].Statement);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_sample_is_the_key_columns_and_nothing_else(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var (orders, customers) = await Arrange(engine);
        var evaluator = new CheckEvaluator(engine.Dialect);
        var check = Check("av", CheckKind.AcceptedValues, [Group], new AcceptedValuesParameters(["open", "closed"]));

        var results = await evaluator.EvaluateAsync([check], engine.ReadOnlyOpener(), _ => new EvaluationContext { ResolveTable = _ => [orders], KeyColumns = [Id] });

        Assert.Equal(["Id=4"], results[0].SampleKeys);
        Assert.DoesNotContain("weird", string.Join(' ', results[0].SampleKeys));
        Assert.DoesNotContain("1000", results[0].Statement);
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task A_not_null_check_without_key_columns_keeps_no_sample_and_says_why(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var (orders, customers) = await Arrange(engine);
        var evaluator = new CheckEvaluator(engine.Dialect);

        var results = await evaluator.EvaluateAsync([Check("nn", CheckKind.NotNull, [Customer])], engine.ReadOnlyOpener(), c => ContextFor(c, orders, customers));

        Assert.Empty(results[0].SampleKeys);
        Assert.Contains("no key columns are known", results[0].Message);
    }

    [Theory]
    [InlineData(EvaluationEngine.Postgres)]
    [InlineData(EvaluationEngine.DuckDb)]
    public async Task The_connection_the_evaluator_gets_refuses_a_write(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var (orders, _) = await Arrange(engine);

        await using var connection = await engine.ReadOnlyOpener()(CancellationToken.None);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DELETE FROM {engine.Dialect.QuoteQualified([orders])}";

        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => command.ExecuteNonQueryAsync());
    }

    [Theory, MemberData(nameof(Engines))]
    public async Task Evaluating_leaves_the_data_as_it_was(string engineName)
    {
        await using var engine = EvaluationEngine.Open(engineName);
        var (orders, customers) = await Arrange(engine);
        var evaluator = new CheckEvaluator(engine.Dialect, time: new FixedTime(Now));
        await evaluator.EvaluateAsync(AllChecks(), engine.ReadOnlyOpener(), c => ContextFor(c, orders, customers));

        var again = await evaluator.EvaluateAsync([Check("rc", CheckKind.RowCountChange, [], new RowCountChangeParameters(1, 1))], engine.ReadOnlyOpener(), _ => new EvaluationContext { ResolveTable = _ => [orders], PreviousRowCount = 10 });

        Assert.Equal(EvaluationStatus.Pass, again[0].Status);
        Assert.Equal(10, again[0].Observed);
    }
}
