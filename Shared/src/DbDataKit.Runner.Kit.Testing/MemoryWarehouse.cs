using DbDataKit.Dialect;
using DbDataKit.Transfer;

namespace DbDataKit.Runner.Kit.Testing;

/// <summary>
/// A staging database in memory for the tests of an integration that lands rows (plan 27a IX21): a <see cref="WriteGate"/> whose executor keeps the batches <c>StagingLander</c> writes, by their idempotency key, so a test can say how many rows landed, that
/// landing the same key again changed nothing, and what a dry run would have sent. It understands the statements of the lander only (probe, count of a key, batch insert, delete of a batch, transactions); it is not a SQL engine, and the lander
/// itself is tested on real engines (<c>LandingTests</c>). Rows are kept as the lander coerced them.
/// </summary>
public sealed class MemoryWarehouse
{
    private readonly object _gate = new();
    private readonly Dictionary<string, List<object?[]>> _batches = new(StringComparer.Ordinal);
    private readonly Executor _executor;

    public MemoryWarehouse(ISqlDialect? dialect = null)
    {
        Dialect = dialect ?? DialectCatalog.For(EngineId.DuckDb);
        _executor = new Executor(this);
    }

    public ISqlDialect Dialect { get; }

    /// <summary>The statements the gate logged (every phase: begin and end, or dry-run).</summary>
    public MemoryStatementLog Log { get; } = new();

    /// <summary>The statements that were executed, as text (the kinds only: no values).</summary>
    public IReadOnlyList<string> Executed
    {
        get { lock (_gate) return [.. _executed]; }
    }

    private readonly List<string> _executed = [];

    /// <summary>The gate to hand to the code under test. With <paramref name="dryRun"/> the gate checks and logs every statement and executes none: <see cref="Executed"/> stays empty.</summary>
    public WriteGate Gate(string command = "test", bool dryRun = false) =>
        new(dryRun ? null : _executor, new WriteGateOptions(command, Log) { DryRun = dryRun });

    /// <summary>The rows under an idempotency key, in the order they were landed.</summary>
    public IReadOnlyList<object?[]> Rows(string key)
    {
        lock (_gate) return _batches.TryGetValue(key, out var rows) ? [.. rows] : [];
    }

    public long RowsUnder(string key) => Rows(key).Count;

    /// <summary>The keys that have rows.</summary>
    public IReadOnlyList<string> Keys
    {
        get { lock (_gate) return [.. _batches.Keys.Order(StringComparer.Ordinal)]; }
    }

    public long TotalRows
    {
        get { lock (_gate) return _batches.Values.Sum(r => (long)r.Count); }
    }

    /// <summary>The number of statements the gate logged as a dry run (what a dry run would have sent).</summary>
    public int DryRunStatements => Log.Entries.Count(e => e.Phase == "dry-run" && e.Kind is not "Read" and not "Control");

    private sealed class Executor(MemoryWarehouse owner) : IWriteExecutor
    {
        private Dictionary<string, List<object?[]>>? _tx;

        private Dictionary<string, List<object?[]>> Target => _tx ?? owner._batches;

        public Task BeginAsync(CancellationToken ct)
        {
            lock (owner._gate) _tx = owner._batches.ToDictionary(p => p.Key, p => new List<object?[]>(p.Value), StringComparer.Ordinal);
            return Task.CompletedTask;
        }

        public Task CommitAsync(CancellationToken ct)
        {
            lock (owner._gate)
            {
                if (_tx is not null)
                {
                    owner._batches.Clear();
                    foreach (var (key, rows) in _tx) owner._batches[key] = rows;
                }
                _tx = null;
            }
            return Task.CompletedTask;
        }

        public Task RollbackAsync(CancellationToken ct)
        {
            lock (owner._gate) _tx = null;
            return Task.CompletedTask;
        }

        public Task<long> ExecuteAsync(SqlStatement statement, CancellationToken ct)
        {
            lock (owner._gate)
            {
                owner._executed.Add(statement.Id);
                if (statement.Id == "landing:delete-batch")
                {
                    var key = Convert.ToString(statement.Parameters[0].Value, System.Globalization.CultureInfo.InvariantCulture)!;
                    var removed = Target.TryGetValue(key, out var old) ? old.Count : 0;
                    Target.Remove(key);
                    return Task.FromResult((long)removed);
                }

                if (statement.Id.StartsWith("landing:insert", StringComparison.Ordinal))
                {
                    // the statement is INSERT INTO t (c1, c2, ...) VALUES (...), (...): the columns tell the width of a row, the batch key is the second last column of each
                    var open = statement.Text.IndexOf('(', StringComparison.Ordinal);
                    var close = statement.Text.IndexOf(')', open + 1);
                    var width = statement.Text[(open + 1)..close].Split(',').Length;
                    var count = statement.Parameters.Count / width;
                    for (var r = 0; r < count; r++)
                    {
                        var row = new object?[width];
                        for (var c = 0; c < width; c++) row[c] = statement.Parameters[r * width + c].Value;
                        var key = Convert.ToString(row[width - 2], System.Globalization.CultureInfo.InvariantCulture)!;
                        if (!Target.TryGetValue(key, out var list)) Target[key] = list = [];
                        list.Add(row);
                    }
                    return Task.FromResult((long)count);
                }

                return Task.FromResult(0L);
            }
        }

        public Task<object?> ScalarAsync(SqlStatement statement, CancellationToken ct)
        {
            lock (owner._gate)
            {
                if (statement.Id == "landing:count")
                {
                    var key = Convert.ToString(statement.Parameters[0].Value, System.Globalization.CultureInfo.InvariantCulture)!;
                    return Task.FromResult<object?>(Target.TryGetValue(key, out var rows) ? (long)rows.Count : 0L);
                }

                return Task.FromResult<object?>(0L);
            }
        }

        public Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct) => Task.FromResult(true);

        public Task UnlockAsync(string resource, bool shared, CancellationToken ct) => Task.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
