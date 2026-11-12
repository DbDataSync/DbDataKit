using System.Globalization;
using DbDataKit.Dialect;
using DbDataKit.Runner.Kit.Formats;
using DbDataKit.Transfer;

namespace DbDataKit.Runner.Kit.Landing;

/// <summary>What a landing did.</summary>
/// <param name="Key">The idempotency key the batch was landed under.</param>
/// <param name="Rows">The rows the table holds for the key after the call.</param>
/// <param name="AlreadyLanded">The key had landed before and nothing was written (<see cref="LandOptions.Replace"/> was not set).</param>
/// <param name="Rejected">Records left out because a value did not fit its column (<see cref="LandOptions.OnBadRow"/> Skip or Collect).</param>
public sealed record LandingResult(string Key, long Rows, bool AlreadyLanded, long Rejected = 0);

public sealed record LandOptions
{
    /// <summary>Rows in one INSERT statement (the engine's limits shorten it). Every statement of a batch is in the one transaction, whatever this is.</summary>
    public int StatementRows { get; init; } = 500;

    /// <summary>A batch that is already in the table under its key is landed again: deleted and written anew. Off (the default): it is not written at all.</summary>
    public bool Replace { get; init; }

    /// <summary>The columns the lander adds to the table: the idempotency key, and the record's number within its batch.</summary>
    public string BatchColumn { get; init; } = "__batch";

    public string RowColumn { get; init; } = "__row";

    /// <summary>What a record whose value does not fit its column means, as for the readers of <c>DbDataKit.Runner.Kit.Formats</c>. Refuse (the default) stops the landing and writes nothing of the batch.</summary>
    public BadRowDecision OnBadRow { get; init; } = BadRowDecision.Refuse;

    /// <summary>Where the rejected records go (Collect keeps their position and reason, never the value).</summary>
    public BadRows? Bad { get; init; }

    /// <summary>The name of the file or call the rows came from, for messages.</summary>
    public string? Source { get; init; }
}

/// <summary>A landing that cannot go on: a record the table cannot take (named by its number, never its value).</summary>
public sealed class LandingException(string message, Exception? inner = null) : Exception(message, inner)
{
    public bool IsTemporary => false;
}

/// <summary>
/// Lands rows into a table of a suite connection through <c>DbDataKit.Transfer</c> (plan 27a IX11): every statement goes through the **write gate** (its checks, dry run, statement log and read-only mode apply), a batch is **one transaction** (all of it or none), and a batch has an
/// **idempotency key**: the lander adds the key to every row it writes, so a batch that is already in the table under its key is not written again (a retry of a run that failed after landing, a file that arrives twice). The key names a batch's content, not a try: the
/// same key with other rows is a mistake the lander does not detect (use a hash of the file, a day, a page's cursor). The rows then go on to Build (a model reads the staging table) or to a load strategy of Transfer.
/// </summary>
public sealed class StagingLander
{
    private readonly WriteGate _gate;
    private readonly ISqlDialect _dialect;
    private readonly TableName _table;
    private readonly TransferSchema _schema;
    private readonly TransferSchema _full;
    private readonly LandOptions _o;
    private readonly string _targetName;

    public StagingLander(WriteGate gate, ISqlDialect dialect, TableName table, TransferSchema schema, LandOptions? options = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _dialect = dialect ?? throw new ArgumentNullException(nameof(dialect));
        _table = table;
        _schema = schema;
        _o = options ?? new LandOptions();
        if (schema.Contains(_o.BatchColumn) || schema.Contains(_o.RowColumn)) throw new ArgumentException($"The table's own columns include '{_o.BatchColumn}' or '{_o.RowColumn}', which the lander adds; name them otherwise (LandOptions).", nameof(schema));
        _full = new TransferSchema([.. schema.Columns, new TransferColumn(_o.BatchColumn, CanonicalType.String(200), Nullable: false), new TransferColumn(_o.RowColumn, CanonicalType.Of(CanonicalKind.Int64), Nullable: false)]);
        _targetName = table.ToString();
    }

    /// <summary>Makes the table if it is not there (the columns of the schema, the batch key and the record number, all as nullable columns: a staging table takes what arrived and the model downstream enforces the rules).</summary>
    public async Task EnsureTableAsync(CancellationToken ct = default)
    {
        try
        {
            await _gate.ScalarAsync(new SqlStatement("landing:probe", $"SELECT COUNT(*) FROM {Quoted} WHERE 1 = 0", [], new WriteIntent("landing:probe", WriteKind.None, _targetName)), ct).ConfigureAwait(false);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not GateRefusedException)
        {
            // not there (or not readable: then the create below fails with the engine's own reason)
        }

        await _gate.ExecuteAsync(StagingDdl.Create(_dialect, new StagingTable(_table, _full, IsTemporary: false, StagingLifecycle.Retain)), ct).ConfigureAwait(false);
    }

    /// <summary>How many rows the table holds under <paramref name="key"/>.</summary>
    public async Task<long> CountAsync(string key, CancellationToken ct = default) =>
        Convert.ToInt64(await _gate.ScalarAsync(new SqlStatement("landing:count", $"SELECT COUNT(*) FROM {Quoted} WHERE {_dialect.Quote(_o.BatchColumn)} = {Marker(0)}", [Param(0, key)], new WriteIntent("landing:count", WriteKind.None, _targetName)), ct).ConfigureAwait(false) ?? 0L, CultureInfo.InvariantCulture);

    /// <summary>Removes a batch (the rows of a key), for a rerun that must replace it.</summary>
    public Task<long> DropBatchAsync(string key, CancellationToken ct = default) =>
        _gate.ExecuteAsync(new SqlStatement("landing:delete-batch", $"DELETE FROM {Quoted} WHERE {_dialect.Quote(_o.BatchColumn)} = {Marker(0)}", [Param(0, key)], new WriteIntent("landing:delete-batch", WriteKind.Delete, _targetName)), ct);

    /// <summary>Lands rows whose values are already typed as the table's columns (<see cref="ClrContract"/>), one value per column in the schema's order.</summary>
    public Task<LandingResult> LandAsync(string idempotencyKey, IAsyncEnumerable<IReadOnlyList<object?>> rows, CancellationToken ct = default) =>
        LandCoreAsync(idempotencyKey, Typed(rows, ct), ct);

    /// <summary>Lands the rows of a reader of <c>DbDataKit.Runner.Kit.Formats</c>: each value is read from the column of the same name and coerced to the column's type (<see cref="ValueCoercion"/>); a column the file lacks is null; a value that does not fit is a bad record by <see cref="LandOptions.OnBadRow"/>.</summary>
    public Task<LandingResult> LandAsync(string idempotencyKey, IAsyncEnumerable<Row> rows, CancellationToken ct = default) =>
        LandCoreAsync(idempotencyKey, FromRows(rows, ct), ct);

    private async Task<LandingResult> LandCoreAsync(string key, IAsyncEnumerable<(long Number, object?[]? Values, string? Problem)> rows, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 200) throw new ArgumentException("An idempotency key is at most 200 characters (use a hash of what identifies the batch).", nameof(key));
        var existing = await CountAsync(key, ct).ConfigureAwait(false);
        if (existing > 0 && !_o.Replace) return new LandingResult(key, existing, AlreadyLanded: true);

        var gate = new Rejections(_o);
        var perStatement = InsertLimits.For(_dialect.Id).RowsFor(_full.Count, Math.Max(1, _o.StatementRows));
        var target = new WriteTarget(_table, _full);
        var pending = new List<object?[]>(perStatement);
        long written = 0, statements = 0;
        var begun = false;
        try
        {
            await _gate.BeginTransactionAsync("landing:" + key, ct).ConfigureAwait(false);
            begun = true;
            if (existing > 0) await DropBatchAsync(key, ct).ConfigureAwait(false);
            await foreach (var (number, values, problem) in rows.WithCancellation(ct).ConfigureAwait(false))
            {
                if (values is null)
                {
                    gate.Reject(number, problem ?? "the record cannot be landed");
                    continue;
                }

                var full = new object?[_full.Count];
                Array.Copy(values, full, values.Length);
                full[values.Length] = key;
                full[values.Length + 1] = number;
                pending.Add(full);
                if (pending.Count >= perStatement) await FlushAsync().ConfigureAwait(false);
            }

            await FlushAsync().ConfigureAwait(false);
            await _gate.CommitAsync("landing:" + key, ct).ConfigureAwait(false);
            begun = false;
        }
        catch
        {
            if (begun) await _gate.RollbackAsync("landing:" + key, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        return new LandingResult(key, written, AlreadyLanded: false, gate.Count);

        async Task FlushAsync()
        {
            if (pending.Count == 0) return;
            await _gate.ExecuteAsync(BatchInsertBulkWriter.Build(_dialect, target, pending, $"landing:insert-{++statements}"), ct).ConfigureAwait(false);
            written += pending.Count;
            pending.Clear();
        }
    }

    private async IAsyncEnumerable<(long, object?[]?, string?)> Typed(IAsyncEnumerable<IReadOnlyList<object?>> rows, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        long n = 0;
        await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
        {
            n++;
            if (row.Count != _schema.Count) throw new LandingException($"Record {n} has {row.Count} values and the table has {_schema.Count} columns.");
            var values = new object?[row.Count];
            string? problem = null;
            for (var i = 0; i < values.Length && problem is null; i++)
            {
                try { values[i] = ValueCoercion.Coerce(row[i], _schema[i].Type); }
                catch (FormatException ex) { problem = $"column {_schema[i].Name}: {ex.Message}"; }
                if (problem is null && values[i] is null && !_schema[i].Nullable) problem = $"column {_schema[i].Name} does not allow a missing value";
            }

            yield return problem is null ? (n, values, null) : (n, null, problem);
        }
    }

    private async IAsyncEnumerable<(long, object?[]?, string?)> FromRows(IAsyncEnumerable<Row> rows, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in rows.WithCancellation(ct).ConfigureAwait(false))
        {
            var values = new object?[_schema.Count];
            string? problem = null;
            for (var i = 0; i < values.Length && problem is null; i++)
            {
                try { values[i] = ValueCoercion.Coerce(row[_schema[i].Name], _schema[i].Type); }
                catch (FormatException ex) { problem = $"column {_schema[i].Name}: {ex.Message}"; }
                if (problem is null && values[i] is null && !_schema[i].Nullable) problem = $"column {_schema[i].Name} does not allow a missing value";
            }

            yield return problem is null ? (row.Number, values, null) : (row.Number, null, problem);
        }
    }

    private string Quoted => _dialect.QuoteQualified(_table.Parts);

    private string Marker(int i) => _dialect.ParameterName(i, "p" + i);

    private SqlParameter Param(int i, string value) => new(Marker(i), "p" + i, CanonicalType.String(200), value);

    private sealed class Rejections
    {
        private readonly BadRows _bad;
        private readonly BadRowGate _gate;

        public Rejections(LandOptions o)
        {
            _bad = o.Bad ?? new BadRows();
            _gate = new BadRowGate("landing", new ReadOptions { OnBadRow = o.OnBadRow, Bad = _bad, File = o.Source });
        }

        public long Count => _bad.Count;

        public void Reject(long number, string reason) => _gate.Report(number, 0, 0, reason);
    }
}
