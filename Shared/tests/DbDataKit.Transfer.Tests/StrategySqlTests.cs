using DbDataKit.Dialect;
using Xunit;

namespace DbDataKit.Transfer.Tests;

public class StrategySqlTests
{
    private static readonly TransferSchema Schema = new(
    [
        new TransferColumn("id", CanonicalType.Of(CanonicalKind.Int32), IsKey: true),
        new TransferColumn("na]me x", CanonicalType.String(10)),
        new TransferColumn("day", CanonicalType.Of(CanonicalKind.Date)),
    ]);

    private const string Sentinel = "SENTINEL_VALUE_42";

    private static ApplyRequest Request(EngineVersion? v = null, ApplyOptions? o = null) => new(
        new TableName("sch", "tg]t"), new TableName("trk", "stg_sch__tgt"), Schema, v, o,
        RangeColumn: "day", RangeStart: new DateOnly(2024, 1, 1), RangeEnd: new DateOnly(2024, 2, 1),
        WatermarkColumn: "day", Watermark: new DateOnly(2024, 1, 15), WatermarkInclusive: true, ScopeColumn: "day");

    public static TheoryData<string, string> Combos()
    {
        var data = new TheoryData<string, string>();
        foreach (var engine in new[] { "sqlserver", "postgres", "mysql", "oracle", "duckdb", "fabric" })
            foreach (var s in StrategyCatalog.Strategies)
                data.Add(engine, s.Name);
        return data;
    }

    [Theory]
    [MemberData(nameof(Combos))]
    public void Every_strategy_builds_on_every_engine_without_values_in_the_text(string engine, string strategy)
    {
        var d = DialectCatalog.For(engine);
        var req = Request(new EngineVersion(16)) with { RangeStart = Sentinel, RangeEnd = Sentinel + "2", Watermark = Sentinel + "3" };
        // string-typed bounds to prove no value ever reaches the text, whatever its type
        var set = StrategyCatalog.Get(strategy).Build(req, d);
        Assert.NotEmpty(set.Statements);
        foreach (var s in set.Statements)
        {
            Assert.DoesNotContain(Sentinel, s.Text);
            Assert.DoesNotContain("2024", s.Text);
            // every placeholder in the text is backed by a parameter
            foreach (var p in s.Parameters) Assert.Contains(p.Marker, s.Text);
        }
    }

    [Theory]
    [MemberData(nameof(Combos))]
    public void Identifiers_are_quoted_by_the_dialect(string engine, string strategy)
    {
        var d = DialectCatalog.For(engine);
        var set = StrategyCatalog.Get(strategy).Build(Request(new EngineVersion(16)), d);
        var quotedTarget = d.QuoteQualified(["sch", "tg]t"]);
        var quotedStage = d.QuoteQualified(["trk", "stg_sch__tgt"]);
        Assert.Contains(set.Statements, s => s.Text.Contains(quotedTarget));
        Assert.Contains(set.Statements, s => s.Text.Contains(quotedStage));
        var quotedColumn = d.Quote("na]me x");
        if (strategy is not StrategyNames.KeyReconcileDelete and not StrategyNames.DeleteInsertByKey)
            Assert.Contains(set.Statements, s => s.Text.Contains(quotedColumn));
    }

    [Fact]
    public void Parameter_markers_follow_the_dialect_style()
    {
        var sqlServer = StrategyCatalog.Get(StrategyNames.DeleteInsertByRange).Build(Request(), DialectCatalog.For(EngineId.SqlServer));
        Assert.Equal(["@range_start", "@range_end"], sqlServer.Statements[0].Parameters.Select(p => p.Marker));
        // named parameters are reused within one statement
        var pg = StrategyCatalog.Get(StrategyNames.DeleteInsertByRange).Build(Request(), DialectCatalog.For(EngineId.Postgres));
        Assert.Equal(["$1", "$2"], pg.Statements[0].Parameters.Select(p => p.Marker));
        var my = StrategyCatalog.Get(StrategyNames.DeleteInsertByRange).Build(Request(), DialectCatalog.For(EngineId.MySql));
        Assert.All(my.Statements[0].Parameters, p => Assert.Equal("?", p.Marker));
        var oracle = StrategyCatalog.Get(StrategyNames.DeleteInsertByRange).Build(Request(), DialectCatalog.For(EngineId.Oracle));
        Assert.Equal([":range_start", ":range_end"], oracle.Statements[0].Parameters.Select(p => p.Marker));
    }

    [Fact]
    public void Parameters_carry_values_and_canonical_types()
    {
        var set = StrategyCatalog.Get(StrategyNames.DeleteInsertByRange).Build(Request(), DialectCatalog.For(EngineId.SqlServer));
        var p = set.Statements[0].Parameters[0];
        Assert.Equal(new DateOnly(2024, 1, 1), p.Value);
        Assert.Equal(CanonicalKind.Date, p.Type.Kind);
    }

    // ---------------------------------------------------------------- merge by capability

    [Fact]
    public void Merge_style_follows_capability_and_version()
    {
        string Text(string engine, EngineVersion? v) => StrategyCatalog.Get(StrategyNames.MergeByKey).Build(Request(v), DialectCatalog.For(engine)).Statements.Single().Text;
        Assert.StartsWith("MERGE INTO", Text("sqlserver", null));
        Assert.EndsWith(";", Text("sqlserver", null));
        Assert.StartsWith("MERGE INTO", Text("oracle", null));
        Assert.StartsWith("MERGE INTO", Text("postgres", new EngineVersion(15)));
        Assert.Contains("ON CONFLICT", Text("postgres", new EngineVersion(14)));
        Assert.Contains("ON CONFLICT", Text("postgres", null));
        Assert.Contains("ON CONFLICT", Text("duckdb", null));
        Assert.Contains("ON DUPLICATE KEY UPDATE", Text("mysql", null));
    }

    [Fact]
    public void Merge_style_can_be_forced_and_rejects_nothing_silently()
    {
        var d = DialectCatalog.For(EngineId.Postgres);
        var forced = StrategyCatalog.Get(StrategyNames.MergeByKey).Build(Request(new EngineVersion(16), new ApplyOptions { MergeStyle = MergeStyle.OnConflict }), d);
        Assert.Contains("ON CONFLICT", forced.Statements.Single().Text);
        Assert.Contains(forced.Notes!, n => n.Contains("PRIMARY KEY or UNIQUE"));
    }

    [Fact]
    public void Merge_updates_only_non_key_columns_and_do_nothing_when_all_columns_are_keys()
    {
        var onlyKeys = new TransferSchema([new TransferColumn("a", CanonicalType.Of(CanonicalKind.Int32), IsKey: true)]);
        var r = new ApplyRequest(new TableName(null, "t"), new TableName(null, "s"), onlyKeys);
        var duck = StrategyCatalog.Get(StrategyNames.MergeByKey).Build(r, DialectCatalog.For(EngineId.DuckDb)).Statements.Single().Text;
        Assert.EndsWith("DO NOTHING", duck);
        var sql = StrategyCatalog.Get(StrategyNames.MergeByKey).Build(r, DialectCatalog.For(EngineId.SqlServer)).Statements.Single().Text;
        Assert.DoesNotContain("WHEN MATCHED", sql);
        var normal = StrategyCatalog.Get(StrategyNames.MergeByKey).Build(Request(), DialectCatalog.For(EngineId.SqlServer)).Statements.Single().Text;
        Assert.DoesNotContain("SET [id]", normal);                 // key not in SET
        Assert.Contains("SET [na]]me x] = src.[na]]me x]", normal);
    }

    // ---------------------------------------------------------------- full replace

    [Fact]
    public void Full_replace_uses_delete_by_default_and_truncate_on_request()
    {
        var d = DialectCatalog.For(EngineId.SqlServer);
        var del = StrategyCatalog.Get(StrategyNames.FullReplace).Build(Request(), d);
        Assert.StartsWith("DELETE FROM", del.Statements[0].Text);
        Assert.True(del.Transactional);
        var trunc = StrategyCatalog.Get(StrategyNames.FullReplace).Build(Request(null, new ApplyOptions { UseTruncate = true }), d);
        Assert.StartsWith("TRUNCATE TABLE", trunc.Statements[0].Text);
        Assert.Equal(WriteKind.Truncate, trunc.Statements[0].Intent.Kind);
    }

    [Fact]
    public void Truncate_on_an_engine_that_commits_it_implicitly_is_marked_non_transactional()
    {
        var oracle = StrategyCatalog.Get(StrategyNames.FullReplace).Build(Request(null, new ApplyOptions { UseTruncate = true }), DialectCatalog.For(EngineId.Oracle));
        Assert.False(oracle.Transactional);
        Assert.Contains(oracle.Notes!, n => n.Contains("not atomic"));
        var pg = StrategyCatalog.Get(StrategyNames.FullReplace).Build(Request(null, new ApplyOptions { UseTruncate = true }), DialectCatalog.For(EngineId.Postgres));
        Assert.True(pg.Transactional);
    }

    // ---------------------------------------------------------------- argument validation

    [Fact]
    public void Missing_key_range_or_watermark_is_an_argument_error_naming_the_column()
    {
        var noKeys = new TransferSchema([new TransferColumn("a", CanonicalType.Of(CanonicalKind.Int32))]);
        var r = new ApplyRequest(new TableName(null, "t"), new TableName(null, "s"), noKeys);
        var d = DialectCatalog.For(EngineId.Postgres);
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Get(StrategyNames.MergeByKey).Build(r, d));
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Get(StrategyNames.DeleteInsertByKey).Build(r, d));
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Get(StrategyNames.KeyReconcileDelete).Build(r, d));
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Get(StrategyNames.DeleteInsertByRange).Build(r with { RangeColumn = "a" }, d));
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Get(StrategyNames.DeleteInsertByRange).Build(r with { RangeColumn = "nope", RangeStart = 1, RangeEnd = 2 }, d));
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Get(StrategyNames.WatermarkAppend).Build(r, d));
    }

    [Fact]
    public void Identifiers_the_dialect_cannot_represent_are_rejected()
    {
        var bad = new TransferSchema([new TransferColumn("a\u0001b", CanonicalType.Of(CanonicalKind.Int32), IsKey: true)]);
        var r = new ApplyRequest(new TableName(null, "t"), new TableName(null, "s"), bad);
        Assert.Throws<ArgumentException>(() => StrategyCatalog.Get(StrategyNames.FullReplace).Build(r, DialectCatalog.For(EngineId.SqlServer)));
    }

    // ---------------------------------------------------------------- watermark / reconcile

    [Fact]
    public void Watermark_without_lookback_is_a_strict_greater_than_append_with_no_delete()
    {
        var set = StrategyCatalog.Get(StrategyNames.WatermarkAppend).Build(Request() with { WatermarkInclusive = false }, DialectCatalog.For(EngineId.Postgres));
        var only = Assert.Single(set.Statements);
        Assert.Contains(" > $1", only.Text);
        Assert.Contains(set.Notes!, n => n.Contains("append only"));
    }

    [Fact]
    public void Watermark_with_lookback_deletes_the_window_and_reinserts_it()
    {
        var set = StrategyCatalog.Get(StrategyNames.WatermarkAppend).Build(Request(), DialectCatalog.For(EngineId.Postgres));
        Assert.Equal(2, set.Statements.Count);
        Assert.StartsWith("DELETE FROM", set.Statements[0].Text);
        Assert.Contains(">=", set.Statements[1].Text);
    }

    [Fact]
    public void Watermark_resolver_is_a_scalar_max_query()
    {
        var s = WatermarkAppendStrategy.ResolverStatement(Request(), DialectCatalog.For(EngineId.SqlServer));
        Assert.Equal(StatementRole.Scalar, s.Role);
        Assert.Equal(WriteKind.None, s.Intent.Kind);
        Assert.Equal("SELECT MAX([day]) FROM [sch].[tg]]t]", s.Text);
    }

    [Fact]
    public void Key_reconcile_pairs_a_count_with_a_guarded_delete()
    {
        var set = StrategyCatalog.Get(StrategyNames.KeyReconcileDelete).Build(Request(null, new ApplyOptions { Guard = new DeleteGuard(0.25) }) with { ScopeColumn = null }, DialectCatalog.For(EngineId.Postgres));
        Assert.Equal(StatementRole.CountScope, set.Statements[0].Role);
        Assert.Contains("NOT EXISTS", set.Statements[1].Text);
        Assert.Equal(0.25, set.Guard!.MaxRatio);
        Assert.Equal(WriteRisk.Destructive, set.Statements[1].Intent.Risk);
    }

    [Fact]
    public void Key_reconcile_scope_uses_bound_parameters_in_both_statements()
    {
        var set = StrategyCatalog.Get(StrategyNames.KeyReconcileDelete).Build(Request(), DialectCatalog.For(EngineId.SqlServer));
        Assert.All(set.Statements, s => Assert.Equal(2, s.Parameters.Count));
    }

    [Theory]
    [InlineData(100, 50, true)]
    [InlineData(100, 51, false)]
    [InlineData(0, 10, true)]
    [InlineData(10, 0, true)]
    public void Delete_guard_ratio(long scope, long deleted, bool allowed) => Assert.Equal(allowed, new DeleteGuard(0.5).Allows(scope, deleted));

    [Fact]
    public void Disabled_guard_allows_everything() => Assert.True(DeleteGuard.None.Allows(10, 10));

    // ---------------------------------------------------------------- batch insert

    [Theory]
    [InlineData("sqlserver", 3, 700)]        // 2100 / 3
    [InlineData("postgres", 3, 1000)]        // capped by the requested batch
    [InlineData("oracle", 3, 1)]
    [InlineData("duckdb", 10, 1000)]
    public void Insert_limits_bound_rows_per_statement(string engine, int columns, int expected) =>
        Assert.Equal(expected, InsertLimits.For(new EngineId(engine)).RowsFor(columns, 1000));

    [Fact]
    public void Batch_insert_statement_is_parameterised_with_one_marker_per_value()
    {
        var d = DialectCatalog.For(EngineId.SqlServer);
        var schema = new TransferSchema([new("a", CanonicalType.Of(CanonicalKind.Int32)), new("b", CanonicalType.String())]);
        var st = BatchInsertBulkWriter.Build(d, new WriteTarget(new TableName("s", "t"), schema), [[1, Sentinel], [2, null]], "x");
        Assert.Equal("INSERT INTO [s].[t] ([a], [b]) VALUES (@p0, @p1), (@p2, @p3)", st.Text);
        Assert.Equal(4, st.Parameters.Count);
        Assert.DoesNotContain(Sentinel, st.Text);
        Assert.Equal(Sentinel, st.Parameters[1].Value);
    }

    [Fact]
    public void A_row_wider_than_the_parameter_limit_is_refused()
    {
        Assert.Throws<TransferException>(() => InsertLimits.For(EngineId.SqlServer).RowsFor(3000, 10));
    }
}
