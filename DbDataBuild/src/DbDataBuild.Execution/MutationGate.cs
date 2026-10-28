using System.Data;
using System.Data.Common;
using System.Globalization;
using DbDataBuild.Core;
using DbDataKit.Dialect;
using DbDataKit.Transfer;

namespace DbDataBuild.Execution;

public sealed class GateRefusedException(Diagnostic diagnostic) : Exception(diagnostic.Found)
{
    public Diagnostic Diagnostic { get; } = diagnostic;
}

/// <summary>The write side of a connection. The one place a statement is handed to a driver (see the invariant test).</summary>
internal interface IWriteExecutor : IAsyncDisposable
{
    Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct);

    /// <summary>Writes the rows to a table with the engine's bulk route and returns how many were written. The default refuses: only the real executors know a bulk route.</summary>
    Task<long> BulkCopyAsync(string schema, string table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken ct) =>
        throw new NotSupportedException("This executor has no bulk route.");

    /// <summary>Tries to take a session-level application lock without waiting, exclusive or shared. The lock lives as long as this connection (or until <see cref="UnlockAsync"/>).</summary>
    Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct);
    Task UnlockAsync(string resource, bool shared, CancellationToken ct);

    /// <summary>After a failed statement: leaves no transaction open (PostgreSQL refuses further statements in an aborted transaction).</summary>
    Task RecoverAsync(CancellationToken ct);
}

internal sealed class AdoWriteExecutor(DbConnection connection, bool postgres) : IWriteExecutor
{
    public async Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = postgres
            ? (shared ? "SELECT pg_try_advisory_lock_shared(hashtextextended(@res, 0))" : "SELECT pg_try_advisory_lock(hashtextextended(@res, 0))")
            : $"DECLARE @r int; EXEC @r = sp_getapplock @Resource = @res, @LockMode = N'{(shared ? "Shared" : "Exclusive")}', @LockOwner = N'Session', @LockTimeout = 0; SELECT @r;";
        var p = cmd.CreateParameter();
        p.ParameterName = "@res";
        p.DbType = DbType.String;
        p.Value = resource;
        cmd.Parameters.Add(p);
        var result = await cmd.ExecuteScalarAsync(ct);
        return postgres ? Convert.ToBoolean(result, CultureInfo.InvariantCulture) : Convert.ToInt32(result, CultureInfo.InvariantCulture) >= 0;
    }

    public async Task UnlockAsync(string resource, bool shared, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = postgres ? (shared ? "SELECT pg_advisory_unlock_shared(hashtextextended(@res, 0))" : "SELECT pg_advisory_unlock(hashtextextended(@res, 0))") : "EXEC sp_releaseapplock @Resource = @res, @LockOwner = N'Session';";
        var p = cmd.CreateParameter();
        p.ParameterName = "@res";
        p.DbType = DbType.String;
        p.Value = resource;
        cmd.Parameters.Add(p);
        await cmd.ExecuteScalarAsync(ct);
    }

    public async Task RecoverAsync(CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = postgres ? "ROLLBACK;" : "IF @@TRANCOUNT > 0 ROLLBACK;";
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Rows per batch handed to the bulk writer: the memory a copy holds at once, whatever the table's size.</summary>
    private const int Batch = 20_000;

    /// <summary>
    /// The rows (already the canonical values of the declared types, see <see cref="TransferValues"/>) go to the table through the shared bulk writer of the engine: SqlBulkCopy streamed from the batches on SQL Server, binary COPY
    /// on PostgreSQL (plan 13, B08). The staging table was created from these columns, in this order, and the writers map them by name.
    /// </summary>
    public async Task<long> BulkCopyAsync(string schema, string table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken ct)
    {
        IBulkWriter writer = connection switch
        {
            Microsoft.Data.SqlClient.SqlConnection sql => new DbDataKit.Transfer.SqlServer.SqlServerBulkWriter(sql),
            Npgsql.NpgsqlConnection pg => new DbDataKit.Transfer.Postgres.PostgresBulkWriter(pg),
            _ => throw new NotSupportedException("No bulk route for this connection."),
        };
        var canonical = TransferValues.Schema(columns);
        return await writer.WriteAsync(new WriteTarget(new TableName(schema, table), canonical), Batches(rows, canonical, ct), new BulkOptions { BatchSize = Batch }, ct);
    }

    private static async IAsyncEnumerable<DataBatch> Batches(IAsyncEnumerable<object?[]> rows, TransferSchema schema, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var buffer = new List<object?[]>(Batch);
        await foreach (var row in rows.WithCancellation(ct))
        {
            buffer.Add(row);
            if (buffer.Count < Batch) continue;
            yield return new RowDataBatch(schema, buffer);
            buffer = new List<object?[]>(Batch);
        }
        if (buffer.Count > 0) yield return new RowDataBatch(schema, buffer);
    }

    public async Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = text;
        cmd.CommandTimeout = 0;           // a statement that has started is never abandoned because it is slow: the load of a large model runs as long as it takes (the person can stop between steps)
        foreach (var p in parameters)
        {
            var dp = cmd.CreateParameter();
            dp.ParameterName = p.Name.StartsWith('@') ? p.Name : "@" + p.Name;
            dp.DbType = p.Type;
            dp.Value = p.Value ?? DBNull.Value;
            cmd.Parameters.Add(dp);
        }
        // A load script is several statements (stage, delete, insert), and the driver's own total adds their counts: 251 rows loaded read as 502.
        // The rows of a script are those of the last statement that reports a count, which is the INSERT or MERGE of a load.
        long lastCounted = -1;
        if (cmd is Microsoft.Data.SqlClient.SqlCommand sql) sql.StatementCompleted += (_, e) => lastCounted = e.RecordCount;
        var total = await cmd.ExecuteNonQueryAsync(ct);
#pragma warning disable CS0618 // the batch API would need the script split by us; the per-statement counts of a command are what this reads
        if (cmd is Npgsql.NpgsqlCommand pg)
            foreach (var st in pg.Statements)
                if (st.StatementType is Npgsql.StatementType.Insert or Npgsql.StatementType.Update or Npgsql.StatementType.Delete or Npgsql.StatementType.Merge or Npgsql.StatementType.CreateTableAs or Npgsql.StatementType.Select)
                    lastCounted = (long)st.Rows;
#pragma warning restore CS0618
        return lastCounted >= 0 ? lastCounted : total;
    }

    public ValueTask DisposeAsync() => connection.DisposeAsync();
}

/// <summary>The rows of a copy and where they go: the payload of a bulk statement, which only <see cref="SharedExecutor"/> reads.</summary>
internal sealed record BulkCopyRequest(string Schema, string Table, IReadOnlyList<TransferColumn> Columns, IAsyncEnumerable<object?[]> Rows);

/// <summary>
/// The shared gate's executor over Build's: the statement the gate hands over carries Build's own <see cref="GateStatement"/> (with the driver types of its parameters, which the portable parameters cannot say) as its
/// payload. Transactions are in the scripts, not in the gate, and the tool reads through <see cref="ReadSession"/>, so those calls are refused.
/// </summary>
internal sealed class SharedExecutor(IWriteExecutor inner) : DbDataKit.Transfer.IWriteExecutor
{
    public Task<long> ExecuteAsync(SqlStatement statement, CancellationToken ct) => statement.Payload switch
    {
        BulkCopyRequest bulk => inner.BulkCopyAsync(bulk.Schema, bulk.Table, bulk.Columns, bulk.Rows, ct),
        GateStatement own => inner.ExecuteAsync(own.Text, own.Parameters, ct),
        _ => throw new InvalidOperationException("A statement reached the write executor that did not come from this tool's gate."),
    };

    public Task<object?> ScalarAsync(SqlStatement statement, CancellationToken ct) => throw new NotSupportedException("The tool reads through ReadSession, never through the write gate.");
    public Task BeginAsync(CancellationToken ct) => throw new NotSupportedException("A load's transaction is in its script.");
    public Task CommitAsync(CancellationToken ct) => throw new NotSupportedException("A load's transaction is in its script.");
    public Task RollbackAsync(CancellationToken ct) => inner.RecoverAsync(ct);
    public Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct) => inner.TryLockAsync(resource, shared, ct);
    public Task UnlockAsync(string resource, bool shared, CancellationToken ct) => inner.UnlockAsync(resource, shared, ct);
    public ValueTask DisposeAsync() => inner.DisposeAsync();
}

/// <summary>
/// The single route to a write-capable connection (DESIGN.md 9.3), in this tool's terms: it accepts only <see cref="GateStatement"/>s and turns a refusal into a diagnostic (DDB-502, DDB-503). The gate itself is the shared
/// <see cref="WriteGate"/> (plan 13, B06): it checks the statement's effect class against what the running command permits, writes the statement log before executing (a log that cannot be written means the statement does
/// not run), records the outcome after, rolls back after a failure, holds the application lock, and in a dry run does everything but execute. What the log keeps is fixed here and pinned by a test: the text and the
/// <b>values of the parameters</b> (never replaced by hashes), exactly as this tool has always logged them.
/// </summary>
public sealed class MutationGate : IAsyncDisposable
{
    private readonly WriteGate gate;
    private readonly string command;
    private readonly StatementKind permitted;

    public Guid RunId => gate.RunId;
    public bool DryRun => gate.DryRun;

    private MutationGate(WriteGate gate, string command, StatementKind permitted)
    {
        this.gate = gate; this.command = command; this.permitted = permitted;
    }

    /// <summary>What this tool's gate logs and how it checks. The one place the full-value level is chosen: the parameters' values are part of the log (the person who reads it after a failure needs them).</summary>
    private static WriteGateOptions Options(string command, StatementKind permitted, IStatementLog log, Guid runId, bool dryRun) => new(command, log)
    {
        RunId = runId, Permitted = KindsOf(permitted), Detail = StatementLogDetail.WithValues, DryRun = dryRun, LogLockResource = true,
    };

    private static WriteKind KindOf(StatementKind kind) => kind switch { StatementKind.Tracking => WriteKind.Tracking, StatementKind.Data => WriteKind.Data, StatementKind.Ddl => WriteKind.Ddl, _ => WriteKind.None };

    private static WriteKind KindsOf(StatementKind kinds) =>
        Enum.GetValues<StatementKind>().Where(k => kinds.HasFlag(k)).Aggregate(WriteKind.None, (all, k) => all | KindOf(k));

    /// <summary>
    /// Another gate of the same run: its own write session, the same command, permissions, statement log and ordinal. A refresh with `parallel` above 1 gives each concurrent load one, because a connection runs one
    /// statement at a time. Each sibling is still the only handle on its connection.
    /// </summary>
    public async Task<MutationGate> OpenSiblingAsync(LoginSettings write, StatementKind? permittedKinds = null, CancellationToken ct = default)
    {
        if (DryRun) throw new InvalidOperationException("A dry-run gate has no session to open a sibling of.");
        if (write.Login != Login.Write) throw new ArgumentException("The mutation gate needs the write login.", nameof(write));
        var kinds = permittedKinds ?? permitted;
        return new MutationGate(gate.Sibling(new SharedExecutor(new AdoWriteExecutor(await write.OpenAsync(ct), write.Engine == "postgres")), KindsOf(kinds)), command, kinds);
    }

    /// <summary>For tests: a gate of the same run on another executor, sharing the log and the ordinal.</summary>
    internal MutationGate SiblingForTesting(IWriteExecutor executor) => new(gate.Sibling(new SharedExecutor(executor)), command, permitted);

    /// <summary>Opens the write login and wraps it. The returned gate is the only handle on the connection.</summary>
    public static async Task<MutationGate> OpenAsync(LoginSettings write, string command, StatementKind permitted, IStatementLog log, Guid runId, CancellationToken ct = default)
    {
        if (write.Login != Login.Write) throw new ArgumentException("The mutation gate needs the write login.", nameof(write));
        return new MutationGate(new WriteGate(new SharedExecutor(new AdoWriteExecutor(await write.OpenAsync(ct), write.Engine == "postgres")), Options(command, permitted, log, runId, dryRun: false)), command, permitted);
    }

    /// <summary>A gate that runs every check and writes every log entry but never touches a database.</summary>
    public static MutationGate DryRunGate(string command, StatementKind permitted, IStatementLog log, Guid runId) =>
        new(new WriteGate(null, Options(command, permitted, log, runId, dryRun: true)), command, permitted);

    internal static MutationGate ForTesting(IWriteExecutor executor, string command, StatementKind permitted, IStatementLog log, bool dryRun = false) =>
        new(new WriteGate(new SharedExecutor(executor), Options(command, permitted, log, Guid.NewGuid(), dryRun)), command, permitted);

    /// <summary>Returns rows affected as the driver reports it (-1 for statements that do not report one, 0 in dry-run mode).</summary>
    public Task<long> ExecuteAsync(GateStatement statement, CancellationToken ct = default) => RunAsync(statement, statement, ct);

    /// <summary>
    /// Writes the rows of a copy to the table the statement names, through the engine's bulk route, and returns the row count. The same checks, the same log entries (before and after) and the same
    /// dry-run behaviour as <see cref="ExecuteAsync"/>; in a dry run the rows are not read.
    /// </summary>
    public Task<long> BulkCopyAsync(GateStatement statement, string schema, string table, IReadOnlyList<TransferColumn> columns, IAsyncEnumerable<object?[]> rows, CancellationToken ct = default) =>
        RunAsync(statement, new BulkCopyRequest(schema, table, columns, rows), ct);

    private async Task<long> RunAsync(GateStatement statement, object payload, CancellationToken ct)
    {
        try { return await gate.ExecuteAsync(Shared(statement, payload), ct); }
        catch (DbDataKit.Transfer.GateRefusedException refused) { throw Refusal(refused, statement); }
    }

    private static SqlStatement Shared(GateStatement s, object payload) =>
        new(s.StepId, s.Text, [.. s.Parameters.Select(p => new SqlParameter("@" + p.Name.TrimStart('@'), p.Name, LogType(p.Type), p.Value))], new WriteIntent(s.StepId, KindOf(s.Kind), ""), StatementRole.Apply, payload, s.BulkValues);

    /// <summary>The type a parameter is logged with: the portable kind of its driver type (the driver type itself travels in the payload).</summary>
    private static CanonicalType LogType(DbType type) => type switch
    {
        DbType.Boolean => CanonicalType.Of(CanonicalKind.Boolean),
        DbType.Byte or DbType.SByte or DbType.Int16 or DbType.UInt16 => CanonicalType.Of(CanonicalKind.Int16),
        DbType.Int32 or DbType.UInt32 => CanonicalType.Of(CanonicalKind.Int32),
        DbType.Int64 or DbType.UInt64 => CanonicalType.Of(CanonicalKind.Int64),
        DbType.Decimal or DbType.Currency or DbType.VarNumeric => CanonicalType.Of(CanonicalKind.Decimal),
        DbType.Double => CanonicalType.Of(CanonicalKind.Double),
        DbType.Single => CanonicalType.Of(CanonicalKind.Float),
        DbType.Date => CanonicalType.Of(CanonicalKind.Date),
        DbType.Time => CanonicalType.Of(CanonicalKind.Time),
        DbType.DateTime or DbType.DateTime2 or DbType.DateTimeOffset => CanonicalType.Of(CanonicalKind.Timestamp),
        DbType.Guid => CanonicalType.Of(CanonicalKind.Guid),
        DbType.Binary => CanonicalType.Of(CanonicalKind.Binary),
        DbType.String or DbType.AnsiString or DbType.StringFixedLength or DbType.AnsiStringFixedLength or DbType.Xml => CanonicalType.Of(CanonicalKind.String),
        _ => CanonicalType.Unmappable,
    };

    /// <summary>The shared gate says why it refused; this tool says it as a diagnostic, in its own words.</summary>
    private GateRefusedException Refusal(DbDataKit.Transfer.GateRefusedException refused, GateStatement s) => refused.Reason switch
    {
        GateRefusal.LogUnavailable => new(new Diagnostic(DiagnosticCatalog.StatementLogUnavailable, new($"step:{s.StepId}", 0, 0), refused.Message)),
        _ => new(new Diagnostic(DiagnosticCatalog.GateRefused, new($"step:{s.StepId}", 0, 0),
            $"`{command}` permits {Describe(permitted)} statements; step `{s.StepId}` is a {s.Kind.ToString().ToLowerInvariant()} statement. Nothing was executed.")),
    };

    /// <summary>
    /// Takes the application lock that makes `apply` mutually exclusive on one target (`sp_getapplock` on SQL Server, an advisory lock on PostgreSQL).
    /// Returns false when another apply holds it. The attempt and the outcome are in the statement log.
    /// </summary>
    public Task<bool> TryAcquireApplicationLockAsync(string resource, CancellationToken ct = default) => AcquireApplicationLockAsync(resource, shared: false, TimeSpan.Zero, ct);

    /// <summary>
    /// A lock that can be shared (a refresh holds the connection's lock shared while one load runs, so refreshes of different loads run side by side and a deploy, which takes it exclusive, conflicts with any of them) and
    /// waited for: <paramref name="maxWait"/> is how long to try again before giving up (<see cref="TimeSpan.Zero"/> tries once, <see cref="Timeout.InfiniteTimeSpan"/> until it is free). The attempt and the outcome are in the log once, not per retry.
    /// </summary>
    public Task<bool> AcquireApplicationLockAsync(string resource, bool shared, TimeSpan maxWait, CancellationToken ct = default) => gate.AcquireLockAsync(resource, shared, maxWait, ct);

    public Task ReleaseApplicationLockAsync(string resource, CancellationToken ct = default) => ReleaseApplicationLockAsync(resource, shared: false, ct);

    public Task ReleaseApplicationLockAsync(string resource, bool shared, CancellationToken ct = default) => gate.ReleaseLockAsync(resource, shared, ct);

    private static string Describe(StatementKind k) => string.Join("/", Enum.GetValues<StatementKind>().Where(v => k.HasFlag(v)).Select(v => v.ToString().ToLowerInvariant()));

    public ValueTask DisposeAsync() => gate.DisposeAsync();
}
