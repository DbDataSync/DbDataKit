using DbData.Dialect;

namespace DbData.Transfer;

/// <summary>Per-engine statement limits that bound a multi-row INSERT.</summary>
public sealed record InsertLimits(int MaxParameters, int MaxRowsPerStatement)
{
    public static InsertLimits For(EngineId engine) => engine.Value switch
    {
        "sqlserver" or "fabric" => new(2100, 1000),     // 2100 parameters; 1000 row constructors per VALUES
        "postgres" => new(65535, int.MaxValue),
        "mysql" => new(65535, int.MaxValue),
        "oracle" => new(32767, 1),                      // no multi-row VALUES: one row per statement (INSERT ALL is a later optimisation)
        "duckdb" => new(32767, int.MaxValue),
        _ => new(999, int.MaxValue),
    };

    public int RowsFor(int columns, int requested)
    {
        if (columns <= 0) throw new ArgumentOutOfRangeException(nameof(columns));
        var byParams = MaxParameters / columns;
        if (byParams < 1) throw new TransferException($"a row of {columns} columns exceeds the parameter limit ({MaxParameters})");
        return Math.Max(1, Math.Min(Math.Min(byParams, MaxRowsPerStatement), requested));
    }
}

/// <summary>
/// The generic bulk route: batched, parameterised multi-row INSERT. Works on every engine, is the slowest route, and is what staging
/// uses when no engine-specific writer is configured. Statement text never contains values.
/// </summary>
public sealed class BatchInsertBulkWriter(ISqlDialect dialect, IStatementExecutor executor, string stepPrefix = "bulk") : IBulkWriter
{
    /// <summary>The statement for <paramref name="rows"/> row values.</summary>
    public static SqlStatement Build(ISqlDialect d, WriteTarget target, IReadOnlyList<object?[]> rows, string stepId)
    {
        var cols = target.Columns.Columns;
        var sb = new System.Text.StringBuilder();
        var parameters = new List<SqlParameter>(rows.Count * cols.Count);
        sb.Append("INSERT INTO ").Append(d.QuoteQualified(target.Table.Parts)).Append(" (")
          .Append(string.Join(", ", cols.Select(c => d.Quote(c.Name)))).Append(") VALUES ");
        for (var r = 0; r < rows.Count; r++)
        {
            if (r > 0) sb.Append(", ");
            sb.Append('(');
            for (var c = 0; c < cols.Count; c++)
            {
                if (c > 0) sb.Append(", ");
                var index = parameters.Count;
                var marker = d.ParameterName(index, d.ParameterStyle is ParameterStyle.NamedAt or ParameterStyle.NamedColon ? "p" + index : null);
                parameters.Add(new SqlParameter(marker, "p" + index, cols[c].Type, rows[r][c]));
                sb.Append(marker);
            }
            sb.Append(')');
        }
        return new SqlStatement(stepId, sb.ToString(), parameters, new WriteIntent(stepId, WriteKind.Insert | WriteKind.Bulk, target.Table.ToString()));
    }

    public async Task<long> WriteAsync(WriteTarget target, IAsyncEnumerable<DataBatch> batches, BulkOptions options, CancellationToken ct)
    {
        var perStatement = InsertLimits.For(dialect.Id).RowsFor(target.Columns.Count, Math.Max(1, options.BatchSize));
        var pending = new List<object?[]>(perStatement);
        long total = 0;
        var n = 0;

        async Task Flush()
        {
            if (pending.Count == 0) return;
            await executor.ExecuteAsync(Build(dialect, target, pending, $"{stepPrefix}:insert-{++n}"), ct);
            total += pending.Count;
            options.Progress?.Report(total);
            pending.Clear();
        }

        await foreach (var batch in batches.WithCancellation(ct))
        {
            using var reader = batch.AsDataReader();
            while (reader.Read())
            {
                var row = new object?[target.Columns.Count];
                for (var c = 0; c < row.Length; c++) row[c] = reader.IsDBNull(c) ? null : reader.GetValue(c);
                pending.Add(row);
                if (pending.Count >= perStatement) await Flush();
            }
        }
        await Flush();
        return total;
    }
}

/// <summary>
/// Creates, loads and drops staging tables through the gate. Loading uses the supplied bulk writer (SqlBulkCopy, COPY) or, by default,
/// the batched INSERT route through the same gate.
/// </summary>
public sealed class GenericStagingProvider(WriteGate gate, ISqlDialect dialect, StagingPolicy policy, IBulkWriter? bulk = null) : IStagingProvider
{
    public async Task<StagingTable> CreateAsync(StagingRequest request, CancellationToken ct)
    {
        var temporary = (request.PreferTemporary || policy.PreferTemporary) && StagingDdl.CanBeTemporary(dialect);
        var name = StagingNamer.Name(policy, dialect, request.Target, request.RunId, temporary);
        var table = new StagingTable(name, request.Schema, temporary, request.Lifecycle);
        // a retained table of an interrupted run (deterministic name) is dropped and re-created so its shape matches this run
        await gate.ExecuteAsync(StagingDdl.Drop(dialect, table), ct);
        await gate.ExecuteAsync(StagingDdl.Create(dialect, table), ct);
        return table;
    }

    public async Task<long> LoadAsync(StagingTable table, IAsyncEnumerable<DataBatch> rows, CancellationToken ct)
    {
        var target = new WriteTarget(table.Name, table.Schema);
        var intent = new WriteIntent("staging:load", WriteKind.Insert | WriteKind.Bulk, table.Name.ToString());
        return await gate.BulkWriteAsync(intent, bulk ?? new BatchInsertBulkWriter(dialect, new GateExecutor(gate), "staging"), target, rows, new BulkOptions(), ct);
    }

    public Task DropAsync(StagingTable table, CancellationToken ct) => gate.ExecuteAsync(StagingDdl.Drop(dialect, table), ct);

    /// <summary>Lets the batched INSERT writer run its statements through the gate (checks and log included).</summary>
    private sealed class GateExecutor(WriteGate gate) : IStatementExecutor
    {
        public Task<long> ExecuteAsync(SqlStatement statement, CancellationToken ct) => gate.ExecuteAsync(statement, ct);
        public async Task<object?> ScalarAsync(SqlStatement statement, CancellationToken ct) => await gate.ScalarAsync(statement, ct);
    }
}
