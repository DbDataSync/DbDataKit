using System.Data.Common;
using System.Globalization;
using DbData.Dialect;
using DbData.Secrets;

namespace DbData.Transfer.Conformance;

/// <summary>
/// Runs <see cref="StrategyCase"/>s on an engine: create tables, seed through the gate, apply the strategy statements, compare the target with the
/// expected multiset of rows, apply again and compare again (idempotent re-run). Only strategies the matrix allows on the engine are run.
/// </summary>
public static class ConformanceRunner
{
    public static IEnumerable<StrategyCase> CasesFor(ConformanceEngine engine, bool requireVerified = false) =>
        StrategyCases.All.Where(c => StrategyMatrix.Builtin.Check(c.Strategy, engine.Dialect.Id, engine.Version, requireVerified).Allowed);

    public static async Task<ConformanceResult> RunAsync(ConformanceEngine engine, StrategyCase c, CancellationToken ct = default)
    {
        var d = engine.Dialect;
        var diffs = new List<string>();
        await using var connection = await engine.OpenAsync(ct);
        var executor = new AdoWriteExecutor(connection, d);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var target = new TableName(null, $"ddk_t_{tag}");
        var stage = new TableName(null, $"ddk_s_{tag}");
        var log = new MemoryStatementLog();
        // the gate owns the executor; the harness reads the target back on the same connection
        var gate = new WriteGate(new NonDisposingExecutor(executor), new WriteGateOptions("conformance", log) { Redactor = new Redactor() });
        try
        {
            await CreateAsync(gate, d, target, c, unique: c.UniqueKeyOnTarget, ct);
            await CreateAsync(gate, d, stage, c, unique: false, ct);
            await SeedAsync(gate, d, target, c.Schema, c.TargetRows, ct);
            await SeedAsync(gate, d, stage, c.Schema, c.StageRows, ct);

            var seeded = Normalise(c.TargetRows, c.Schema);
            for (var run = 1; run <= 2; run++)
            {
                var request = new ApplyRequest(target, stage, c.Schema, engine.Version);
                if (c.Configure is not null) request = c.Configure(request);
                if (c.Resolve is not null) request = await c.Resolve(request, d, s => gate.ScalarAsync(s, ct));
                var set = StrategyCatalog.Build(c.Strategy, request, d);
                try
                {
                    await gate.RunAsync(set, ct);
                    if (c.ExpectedFailure is not null) { diffs.Add($"run {run}: expected {c.ExpectedFailure.Name}, the apply succeeded"); break; }
                }
                catch (Exception ex) when (c.ExpectedFailure is not null && ex.GetType() == c.ExpectedFailure) { /* expected; the transaction was rolled back */ }

                var actual = await ReadAsync(connection, d, target, c.Schema, ct);
                var expected = c.ExpectedFailure is not null ? seeded : Normalise(c.Expected, c.Schema);
                foreach (var diff in Diff(expected, actual)) diffs.Add($"run {run}: {diff}");
                if (run == 1 && diffs.Count > 0) break;
                if (!c.Idempotent) break;
            }
        }
        finally
        {
            // live engines keep tables: leave nothing behind (errors here must not hide the real result)
            foreach (var t in new[] { target, stage })
            {
                try { await gate.ExecuteAsync(new SqlStatement("cleanup:drop", $"DROP TABLE IF EXISTS {d.QuoteQualified(t.Parts)}", [], new WriteIntent("cleanup:drop", WriteKind.Ddl, t.ToString())), CancellationToken.None); }
                catch (Exception ex) when (ex is DbException or GateRefusedException) { }
            }
            await gate.DisposeAsync();
        }
        return new ConformanceResult(c.Name, c.Strategy, diffs.Count == 0, diffs, c.Idempotent && diffs.Count == 0);
    }

    // ---------------------------------------------------------------- setup

    private static async Task CreateAsync(WriteGate gate, ISqlDialect d, TableName table, StrategyCase c, bool unique, CancellationToken ct)
    {
        var cols = string.Join(", ", c.Schema.Columns.Select(col => $"{d.Quote(col.Name)} {d.ColumnType(col.Type).Require()}{(unique && col.IsKey ? " NOT NULL" : "")}"));
        var pk = unique ? $", PRIMARY KEY ({string.Join(", ", c.Schema.Keys.Select(k => d.Quote(k.Name)))})" : "";
        var text = $"CREATE TABLE {d.QuoteQualified(table.Parts)} ({cols}{pk})";
        await gate.ExecuteAsync(new SqlStatement("setup:create", text, [], new WriteIntent("setup:create", WriteKind.Ddl, table.ToString())), ct);
    }

    private static async Task SeedAsync(WriteGate gate, ISqlDialect d, TableName table, TransferSchema schema, IReadOnlyList<object?[]> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var writer = new BatchInsertBulkWriter(d, new Exec(gate), "seed");
        await gate.BulkWriteAsync(new WriteIntent("seed", WriteKind.Insert, table.ToString()), writer, new WriteTarget(table, schema),
            One(new RowDataBatch(schema, rows)), new BulkOptions(), ct);
    }

    private static async IAsyncEnumerable<DataBatch> One(DataBatch b) { yield return b; await Task.CompletedTask; }

    // ---------------------------------------------------------------- read back and compare

    private static async Task<List<string>> ReadAsync(DbConnection connection, ISqlDialect d, TableName table, TransferSchema schema, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = $"SELECT {string.Join(", ", schema.Columns.Select(c => d.Quote(c.Name)))} FROM {d.QuoteQualified(table.Parts)}";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rows = new List<string>();
        while (await reader.ReadAsync(ct))
        {
            var values = new object?[schema.Count];
            for (var i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(Key(values, schema));
        }
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static List<string> Normalise(IReadOnlyList<object?[]> rows, TransferSchema schema)
    {
        var list = rows.Select(r => Key(r, schema)).ToList();
        list.Sort(StringComparer.Ordinal);
        return list;
    }

    /// <summary>A comparable text for a row. Values compare by canonical meaning (decimal digits, instants), not by driver CLR type.</summary>
    private static string Key(object?[] row, TransferSchema schema) =>
        string.Join("|", row.Select((v, i) => Cell(v, schema[i].Type)));

    private static string Cell(object? v, CanonicalType type) => v switch
    {
        null => "<null>",
        DBNull => "<null>",
        decimal m => m.ToString("0.#########", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        DateTimeOffset o => o.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        TimeOnly t => t.ToString("HH:mm:ss.ffffff", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString() ?? "",
    };

    /// <summary>Multiset difference. Names row counts and the differing rows' keys are not exposed beyond the comparable text (test data only).</summary>
    private static IEnumerable<string> Diff(List<string> expected, List<string> actual)
    {
        var e = expected.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        var a = actual.GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        foreach (var (row, n) in e)
            if (a.GetValueOrDefault(row) != n) yield return $"expected {n} x [{row}], found {a.GetValueOrDefault(row)}";
        foreach (var (row, n) in a)
            if (!e.ContainsKey(row)) yield return $"unexpected {n} x [{row}]";
    }

    private sealed class Exec(WriteGate gate) : IStatementExecutor
    {
        public Task<long> ExecuteAsync(SqlStatement statement, CancellationToken ct) => gate.ExecuteAsync(statement, ct);
        public async Task<object?> ScalarAsync(SqlStatement statement, CancellationToken ct) => await gate.ScalarAsync(statement, ct);
    }

    /// <summary>Lets the harness keep using the connection after the gate is done with its executor.</summary>
    private sealed class NonDisposingExecutor(IWriteExecutor inner) : IWriteExecutor
    {
        public Task BeginAsync(CancellationToken ct) => inner.BeginAsync(ct);
        public Task CommitAsync(CancellationToken ct) => inner.CommitAsync(ct);
        public Task RollbackAsync(CancellationToken ct) => inner.RollbackAsync(ct);
        public Task<long> ExecuteAsync(SqlStatement s, CancellationToken ct) => inner.ExecuteAsync(s, ct);
        public Task<object?> ScalarAsync(SqlStatement s, CancellationToken ct) => inner.ScalarAsync(s, ct);
        public Task<bool> TryLockAsync(string r, bool shared, CancellationToken ct) => inner.TryLockAsync(r, shared, ct);
        public Task UnlockAsync(string r, bool shared, CancellationToken ct) => inner.UnlockAsync(r, shared, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
