using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataProxy.Data;
using DbDataProxy.Tests.Support;
using Grpc.Core;

namespace DbDataProxy.Tests.Flight;

public class ParameterTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    private async Task<ProxyClient> LoginAsync()
    {
        var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        return client;
    }

    private static async Task<List<RecordBatch>> ReadAsync(DbDataProxy.Client.QueryResultStream stream)
    {
        var batches = new List<RecordBatch>();
        await foreach (var b in stream) batches.Add(b);
        return batches;
    }

    [Fact]
    public async Task Queries_bind_positional_parameters()
    {
        using var client = await LoginAsync();
        var batches = await ReadAsync(await client.Raw.QueryAsync(
            "SELECT id, name FROM numbers WHERE id >= ? AND name = ? ORDER BY id", [10L, "name-12"]));

        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);
        Assert.Equal(12, ((Int32Array)batch.Column("id")).GetValue(0));
    }

    [Fact]
    public async Task Parameter_values_are_never_interpreted_as_sql()
    {
        using var client = await LoginAsync();
        var batches = await ReadAsync(await client.Raw.QueryAsync(
            "SELECT count(*) AS n FROM numbers WHERE name = ?", ["x'; DROP TABLE numbers; --"]));
        Assert.Equal(0L, ((Int64Array)Assert.Single(batches).Column("n")).GetValue(0));

        var still = await client.QueryAsync("SELECT count(*) AS n FROM numbers");
        Assert.Equal(10000L, ((Int64Array)Assert.Single(still.Batches).Column("n")).GetValue(0));
    }

    [Fact]
    public async Task Carries_common_value_types_and_nulls()
    {
        using var client = await LoginAsync();
        var batches = await ReadAsync(await client.Raw.QueryAsync(
            "SELECT ? AS i, ? AS d, ? AS dec, ? AS s, ? AS b, ? AS day, ? AS ts, ? AS n",
            [42, 2.5, 123.456m, "héllo", true, new DateOnly(2024, 3, 5), new DateTime(2024, 3, 5, 6, 7, 8, DateTimeKind.Utc), null]));
        var batch = Assert.Single(batches);

        Assert.Equal(42L, Convert.ToInt64(ArrowText.Get(batch.Column("i"), 0)));
        Assert.Equal("2.5", ArrowText.Get(batch.Column("d"), 0));
        Assert.Equal("héllo", ArrowText.Get(batch.Column("s"), 0));
        Assert.Equal("True", ArrowText.Get(batch.Column("b"), 0));
        Assert.Equal("2024-03-05", ArrowText.Get(batch.Column("day"), 0));
        Assert.StartsWith("2024-03-05 06:07:08", ArrowText.Get(batch.Column("ts"), 0));
        Assert.Null(ArrowText.Get(batch.Column("n"), 0));
        Assert.StartsWith("123.456", ArrowText.Get(batch.Column("dec"), 0));
    }

    [Fact]
    public async Task Updates_run_once_per_parameter_row()
    {
        using var client = await LoginAsync();
        await client.UpdateAsync("CREATE TABLE IF NOT EXISTS param_scratch(id INTEGER, label VARCHAR)");

        long affected = await client.Raw.ExecuteUpdateAsync("INSERT INTO param_scratch VALUES (?, ?)",
            [[1, "one"], [2, "two"], [3, null]]);
        Assert.Equal(3, affected);

        var read = await ReadAsync(await client.Raw.QueryAsync("SELECT label FROM param_scratch WHERE id = ?", [2]));
        Assert.Equal("two", ArrowText.Get(Assert.Single(read).Column("label"), 0));

        long updated = await client.Raw.ExecuteUpdateAsync("DELETE FROM param_scratch WHERE id > ?", [1]);
        Assert.Equal(2, updated);
    }

    [Fact]
    public async Task A_marker_count_mismatch_is_an_invalid_argument()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(async () => await ReadAsync(await client.Raw.QueryAsync("SELECT ? + ?", [1])));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }

    [Fact]
    public async Task Binding_to_someone_elses_statement_is_rejected()
    {
        // A handle is only usable by the user who prepared it.
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(async () => await ReadAsync(await client.Raw.QueryAsync("SELECT nope FROM missing_table WHERE x = ?", [1])));
        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
    }
}

public class ParameterUnitTests
{
    [Theory]
    [InlineData("SELECT ?", 1)]
    [InlineData("SELECT '?' , \"?\" , ?", 1)]
    [InlineData("SELECT 'it''s ?', ?, ?", 2)]
    [InlineData("SELECT ? -- is this ? a marker\n, ?", 2)]
    [InlineData("SELECT /* ? */ ?", 1)]
    [InlineData("SELECT 1", 0)]
    public void Counts_only_real_markers(string sql, int expected)
    {
        Assert.Equal(expected, ParameterMarkers.Count(sql, ProviderDialects.Odbc));
        Assert.Equal(expected, ParameterMarkers.Count(sql, ProviderDialects.DuckDb));
        Assert.Equal(expected, ParameterMarkers.Count(sql, ProviderDialects.SqlServer));
    }

    [Fact]
    public void Rewrites_markers_for_named_providers_and_leaves_literals_alone()
    {
        Assert.Equal("SELECT '?', @p1, [a?b], @p2", ParameterMarkers.RewriteToNamed("SELECT '?', ?, [a?b], ?", ProviderDialects.SqlServer));
        Assert.Equal(1, ParameterMarkers.Count("SELECT [a?b], ?", ProviderDialects.SqlServer));
        Assert.Equal(2, ParameterMarkers.Count("SELECT [?, ?]", ProviderDialects.DuckDb)); // DuckDB list literal: brackets are not quoting
        Assert.Equal(2, ParameterMarkers.Count("SELECT [?, ?]", ProviderDialects.Odbc));
    }

    [Theory]
    [InlineData("SELECT $$ ? $$, ?", 1)]                // dollar-quoted string (DuckDB)
    [InlineData("SELECT $tag$ it's ? $tag$, ?, ?", 2)]
    [InlineData("SELECT E'it\\'s ?', ?", 1)]             // E'..' string: a backslash escapes the quote
    [InlineData("SELECT $1, $name, ?", 1)]               // other marker styles are not '?' markers
    public void DuckDb_markers_respect_dollar_quotes_and_E_strings(string sql, int expected) =>
        Assert.Equal(expected, ParameterMarkers.Count(sql, ProviderDialects.DuckDb));

    [Fact]
    public void Odbc_dialect_from_driver_quote_scans_backticks_and_brackets()
    {
        var mysql = ProviderDialects.ForOdbc("MySQL", "`", "`");
        Assert.Equal(1, ParameterMarkers.Count("SELECT `a?b`, ?", mysql));
        var access = ProviderDialects.ForOdbc("ACCESS", "[", "]");
        Assert.Equal(1, ParameterMarkers.Count("SELECT [a?b], ?", access));
        Assert.Equal("`a``b`", mysql.Quote("a`b"));
        Assert.Equal("[a]]b]", access.Quote("a]b"));
    }

    [Fact]
    public void Hash_is_not_a_comment_for_the_proxy_dialects()
    {
        // '#' starts a comment only in MySQL-style dialects; for SQL Server it starts a temp-table name, so the marker after it counts.
        Assert.Equal(1, ParameterMarkers.Count("SELECT * FROM #t WHERE a = ?", ProviderDialects.SqlServer));
        Assert.Equal(2, ParameterMarkers.Count("SELECT 1 # c ?\n, ?", ProviderDialects.Odbc));
        Assert.Equal(1, ParameterMarkers.Count("SELECT 1 # c ?\n, ?", DbDataKit.Dialect.DialectCatalog.Builtin.Get("mysql")));
    }

    [Fact]
    public void Identifier_quoting_rejects_hostile_names_and_doubles_closers()
    {
        foreach (var dialect in new[] { ProviderDialects.SqlServer, ProviderDialects.DuckDb, ProviderDialects.Odbc })
        {
            Assert.Throws<ArgumentException>(() => dialect.Quote("a\nb"));
            Assert.Throws<ArgumentException>(() => dialect.Quote("a\0b"));
            Assert.Throws<ArgumentException>(() => dialect.Quote(""));
        }
        Assert.Equal("\"a\"\"b\"", ProviderDialects.DuckDb.Quote("a\"b"));
        Assert.Equal("[a]]b]", ProviderDialects.SqlServer.Quote("a]b"));
        Assert.Equal("\"main\".\"t\"", ProviderDialects.DuckDb.QuoteQualified(["main", "t"]));
        // A driver that reports no quote character: plain names pass unquoted, anything else is refused rather than passed through raw.
        var none = ProviderDialects.ForOdbc("Text", null, null);
        Assert.Equal("people", none.Quote("people"));
        Assert.Throws<NotSupportedException>(() => none.Quote("x; DROP TABLE y"));
    }

    [Fact]
    public void Arrow_values_round_trip_through_query_parameters()
    {
        var batch = ArrowParameters.FromValues([7, "x", 1.5, true, null, new DateOnly(2024, 1, 2), new byte[] { 1, 2 }, 12.50m]);
        var row = ArrowParameters.ReadRow(batch, 0);

        Assert.Equal(["int64", "string", "double", "bool", "null", "date", "binary", "decimal"], row.Select(p => p.Type));
        Assert.Equal(7L, row[0].ToClr().Value);
        Assert.Equal("x", row[1].ToClr().Value);
        Assert.Equal(1.5, row[2].ToClr().Value);
        Assert.Equal(DBNull.Value, row[4].ToClr().Value);
        Assert.Equal(new DateTime(2024, 1, 2), row[5].ToClr().Value);
        Assert.Equal(new byte[] { 1, 2 }, row[6].ToClr().Value);
        Assert.Equal(12.50m, row[7].ToClr().Value);
    }
}

public class ParameterSchemaTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    [Theory]
    [InlineData("SELECT 1", 0)]
    [InlineData("SELECT * FROM numbers WHERE id > ? AND id <= ?", 2)]
    [InlineData("SELECT '?' AS literal, ? AS value -- ? in a comment", 1)]
    public async Task Prepared_statements_report_one_parameter_field_per_marker(string sql, int expected)
    {
        using var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);

        var schema = await client.Raw.DescribeParametersAsync(sql);
        Assert.Equal(expected, schema.FieldsList.Count);
        Assert.Equal(Enumerable.Range(0, expected).Select(i => "parameter_" + i), schema.FieldsList.Select(f => f.Name));
    }

    [Fact]
    public void A_prepared_statement_handle_survives_being_read_as_a_string()
    {
        // The Apache .NET Flight SQL client keeps the handle as a string; raw random bytes came back changed and the statement was "unknown".
        var store = new DbDataProxy.Server.Flight.PreparedStatementStore(TimeProvider.System);
        for (int i = 0; i < 2000; i++)
        {
            byte[] handle = store.Add("alice", "numbers", "SELECT 1");
            byte[] roundTripped = System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(handle));
            Assert.Equal(handle, roundTripped);
            Assert.NotNull(store.Get(roundTripped, "alice"));
        }
    }
}
