using DbDataKit.Dialect;
using DbDataKit.Transfer.Conformance;
using DuckDB.NET.Data;
using Xunit;

namespace DbDataKit.Transfer.Conformance.Tests;

public class SuiteSelfTests
{
    private static readonly ConformanceEngine Duck = DuckDbEngine.Create();

    [Fact]
    public async Task The_suite_detects_a_wrong_expectation()
    {
        var good = StrategyCases.All.First(c => c.Strategy == StrategyNames.FullReplace);
        var wrong = good with { Expected = [] };
        var result = await ConformanceRunner.RunAsync(Duck, wrong, TestContext.Current.CancellationToken);
        Assert.False(result.Passed);
        Assert.NotEmpty(result.Differences);
    }

    [Fact]
    public async Task The_suite_detects_a_non_idempotent_expectation()
    {
        // an append that is not guarded by a watermark would double the rows on the second run: model that by expecting one run's result from a strategy that grows
        var schema = new TransferSchema([new TransferColumn("id", CanonicalType.Of(CanonicalKind.Int32), IsKey: true)]);
        var c = new StrategyCase("append twice", StrategyNames.WatermarkAppend, schema, [], [[1]], [[1]])
        {
            Configure = r => r with { WatermarkColumn = "id" },      // no watermark value: every run appends the whole stage
        };
        var result = await ConformanceRunner.RunAsync(Duck, c, TestContext.Current.CancellationToken);
        Assert.False(result.Passed);
        Assert.Contains(result.Differences, d => d.StartsWith("run 2"));
    }

    [Fact]
    public void Cases_cover_every_strategy_and_the_matrix_filters_unsupported_engines()
    {
        Assert.Equal(StrategyCatalog.Strategies.Select(s => s.Name).Order(), StrategyCases.All.Select(c => c.Strategy).Distinct().Order());
        var unknown = new ConformanceEngine("x", DialectCatalog.For(EngineId.Ansi), null, _ => throw new InvalidOperationException());
        Assert.Empty(ConformanceRunner.CasesFor(unknown));
    }

    [Fact]
    public async Task Staging_end_to_end_on_duckdb_create_load_merge_drop()
    {
        var d = Duck.Dialect;
        await using var conn = await Duck.OpenAsync(TestContext.Current.CancellationToken);
        var log = new MemoryStatementLog();
        var gate = new WriteGate(new AdoWriteExecutor(conn, d), new WriteGateOptions("e2e", log));
        var schema = new TransferSchema([new("id", CanonicalType.Of(CanonicalKind.Int32), false, true), new("name", CanonicalType.String(20)), new("at", CanonicalType.Timestamp(6, tz: true))]);
        var target = new TableName(null, "tgt");
        await gate.ExecuteAsync(new SqlStatement("t", "CREATE TABLE tgt (id INTEGER PRIMARY KEY, name VARCHAR, \"at\" TIMESTAMPTZ)", [], new WriteIntent("t", WriteKind.Ddl, "tgt")), TestContext.Current.CancellationToken);

        foreach (var temp in new[] { false, true })
        {
            var provider = new GenericStagingProvider(gate, d, new StagingPolicy());
            var stage = await provider.CreateAsync(new StagingRequest(target, schema, PreferTemporary: temp), TestContext.Current.CancellationToken);
            Assert.Equal(temp, stage.IsTemporary);
            var rows = new RowDataBatch(schema, [[1, "a", new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)], [2, null, new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero)]]);
            Assert.Equal(2, await provider.LoadAsync(stage, One(rows), TestContext.Current.CancellationToken));
            var set = StrategyCatalog.Build(StrategyNames.MergeByKey, new ApplyRequest(target, stage.Name, schema), d);
            await gate.RunAsync(set, TestContext.Current.CancellationToken);
            await provider.DropAsync(stage, TestContext.Current.CancellationToken);
        }

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM tgt";
        Assert.Equal(2L, Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        Assert.Contains(log.Entries, e => e.StepId.StartsWith("staging:"));
        await gate.DisposeAsync();
    }

    private static async IAsyncEnumerable<DataBatch> One(DataBatch b) { yield return b; await Task.CompletedTask; }

    [Fact]
    public async Task Values_survive_the_converter_the_stage_and_the_merge_end_to_end()
    {
        var d = Duck.Dialect;
        await using var conn = await Duck.OpenAsync(TestContext.Current.CancellationToken);
        var gate = new WriteGate(new AdoWriteExecutor(conn, d), new WriteGateOptions("e2e", new MemoryStatementLog()));
        var source = new TransferSchema([new("id", CanonicalType.String()), new("amt", CanonicalType.Of(CanonicalKind.Double))]);
        var target = new TransferSchema([new("id", CanonicalType.Of(CanonicalKind.Int32), false, true), new("amt", CanonicalType.Decimal(10, 2))]);
        var plans = ValueConverter.PlanAll(source, target, d, options: ConversionOptions.ForDialect(d, ErrorPolicy.Truncate));
        var converted = ValueConverter.ConvertBatch(new RowDataBatch(source, [["7", 1.239], ["8", 2.5]]), plans, target);

        await gate.ExecuteAsync(new SqlStatement("t", "CREATE TABLE tgt (id INTEGER PRIMARY KEY, amt DECIMAL(10,2))", [], new WriteIntent("t", WriteKind.Ddl, "tgt")), TestContext.Current.CancellationToken);
        var provider = new GenericStagingProvider(gate, d, new StagingPolicy());
        var stage = await provider.CreateAsync(new StagingRequest(new TableName(null, "tgt"), target), TestContext.Current.CancellationToken);
        await provider.LoadAsync(stage, One(converted), TestContext.Current.CancellationToken);
        await gate.RunAsync(StrategyCatalog.Build(StrategyNames.MergeByKey, new ApplyRequest(new TableName(null, "tgt"), stage.Name, target), d), TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, amt FROM tgt ORDER BY id";
        await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await r.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal((7, 1.23m), (r.GetInt32(0), r.GetDecimal(1)));
        Assert.True(await r.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal((8, 2.5m), (r.GetInt32(0), r.GetDecimal(1)));
        await gate.DisposeAsync();
    }

    [Fact]
    public async Task A_run_scoped_policy_keeps_two_runs_apart_on_one_target()
    {
        var d = Duck.Dialect;
        await using var conn = await Duck.OpenAsync(TestContext.Current.CancellationToken);
        var gate = new WriteGate(new AdoWriteExecutor(conn, d), new WriteGateOptions("e2e", new MemoryStatementLog()));
        var schema = new TransferSchema([new("id", CanonicalType.Of(CanonicalKind.Int32))]);
        var p = new GenericStagingProvider(gate, d, new StagingPolicy { RunScoped = true });
        var a = await p.CreateAsync(new StagingRequest(new TableName(null, "t"), schema, "run-a"), TestContext.Current.CancellationToken);
        var b = await p.CreateAsync(new StagingRequest(new TableName(null, "t"), schema, "run-b"), TestContext.Current.CancellationToken);
        Assert.NotEqual(a.Name, b.Name);
        await p.LoadAsync(a, One(new RowDataBatch(schema, [[1]])), TestContext.Current.CancellationToken);
        await p.LoadAsync(b, One(new RowDataBatch(schema, [[2], [3]])), TestContext.Current.CancellationToken);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {d.QuoteQualified(a.Name.Parts)}";
        Assert.Equal(1L, Convert.ToInt64(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken)));
        await gate.DisposeAsync();
    }
}
