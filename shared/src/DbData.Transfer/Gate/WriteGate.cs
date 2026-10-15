using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbData.Secrets;

namespace DbData.Transfer;

public enum AccessMode { ReadOnly, ReadWrite }

public sealed record WriteGateOptions(string Command, IStatementLog Log)
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    /// <summary>ReadOnly refuses every statement that carries a write intent; reads (scalar statements) and locks still run.</summary>
    public AccessMode Access { get; init; } = AccessMode.ReadWrite;
    /// <summary>The effect classes this command may perform. A statement whose kind is not fully contained is refused.</summary>
    public WriteKind Permitted { get; init; } = WriteKind.Any;
    public StatementLogDetail Detail { get; init; } = StatementLogDetail.HashAndTypes;
    /// <summary>Applied to text, parameter values and outcomes before they are logged.</summary>
    public IRedactor? Redactor { get; init; }
    /// <summary>Runs every check and writes every log entry but executes nothing.</summary>
    public bool DryRun { get; init; }
}

/// <summary>Counts per statement of one <see cref="WriteGate.RunAsync"/>; <see cref="RowsWritten"/> sums statements that insert or update.</summary>
public sealed record ApplyResult(IReadOnlyList<(string StepId, long Rows)> Statements)
{
    public long RowsWritten => Statements.Where(s => s.StepId.EndsWith(":insert", StringComparison.Ordinal) || s.StepId.EndsWith(":merge", StringComparison.Ordinal)).Sum(s => s.Rows);
    public long RowsDeleted => Statements.Where(s => s.StepId.Contains("delete", StringComparison.Ordinal) || s.StepId.EndsWith(":clear", StringComparison.Ordinal)).Sum(s => s.Rows);
}

/// <summary>
/// The write choke point (Build's MutationGate, generalised). It accepts only <see cref="SqlStatement"/>s with a <see cref="WriteIntent"/>,
/// checks access and permitted effect classes, writes the statement log before executing (an unwritable log means the statement does not run),
/// controls transactions, holds application locks and, in dry-run mode, does everything except execute.
/// </summary>
public sealed class WriteGate : IAsyncDisposable
{
    private readonly IWriteExecutor? _executor;
    private readonly WriteGateOptions _o;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly object _seq = new();
    private int _ordinal;

    public WriteGate(IWriteExecutor? executor, WriteGateOptions options)
    {
        if (executor is null && !options.DryRun) throw new ArgumentException("an executor is required unless the gate is a dry run", nameof(executor));
        _executor = executor; _o = options;
    }

    public Guid RunId => _o.RunId;
    public bool DryRun => _o.DryRun;

    // ------------------------------------------------------------------ statements

    /// <summary>Executes a write statement. Returns rows affected as the driver reports it (0 in dry-run).</summary>
    public async Task<long> ExecuteAsync(SqlStatement statement, CancellationToken ct = default)
    {
        Check(statement.Intent, statement.Id);
        var n = Begin(DryRun ? "dry-run" : "begin", statement.Id, statement.Intent.Kind.ToString(), statement.Text, statement);
        if (DryRun) return 0;
        try
        {
            var rows = await _executor!.ExecuteAsync(statement, ct);
            Record(n, "end", statement.Id, statement.Intent.Kind.ToString(), null, null, $"ok rows={rows.ToString(CultureInfo.InvariantCulture)}", statement.Text);
            return rows;
        }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            TryRecord(n, statement, $"failed {Describe(ex)}");
            await RecoverAsync(statement.Id);
            throw;
        }
    }

    /// <summary>Runs a read-only scalar statement (the watermark resolver, a count). Refused if the statement carries a write intent.</summary>
    public async Task<object?> ScalarAsync(SqlStatement statement, CancellationToken ct = default)
    {
        if (statement.Intent.Kind != WriteKind.None) throw new GateRefusedException($"step `{statement.Id}` is a write; use ExecuteAsync");
        var n = Begin(DryRun ? "dry-run" : "begin", statement.Id, "Read", statement.Text, statement);
        if (DryRun) return null;
        try
        {
            var v = await _executor!.ScalarAsync(statement, ct);
            Record(n, "end", statement.Id, "Read", null, null, "ok", statement.Text);
            return v;
        }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            TryRecord(n, statement, $"failed {Describe(ex)}");
            await RecoverAsync(statement.Id);
            throw;
        }
    }

    /// <summary>Runs a bulk route through the gate: same checks and log entries. The writer owns its connection.</summary>
    public async Task<long> BulkWriteAsync(WriteIntent intent, IBulkWriter writer, WriteTarget target, IAsyncEnumerable<DataBatch> rows, BulkOptions options, CancellationToken ct = default)
    {
        if ((intent.Kind & WriteKind.Bulk) == 0) intent = intent with { Kind = intent.Kind | WriteKind.Bulk };
        Check(intent, intent.StepId);
        var n = Begin(DryRun ? "dry-run" : "begin", intent.StepId, intent.Kind.ToString(), $"BULK {target.Table}", null);
        if (DryRun) return 0;
        try
        {
            var written = await writer.WriteAsync(target, rows, options, ct);
            Record(n, "end", intent.StepId, intent.Kind.ToString(), null, null, $"ok rows={written.ToString(CultureInfo.InvariantCulture)}", null);
            return written;
        }
        catch (Exception ex) when (ex is not GateRefusedException)
        {
            TryRecord(n, intent.StepId, intent.Kind.ToString(), $"failed {Describe(ex)}");
            await RecoverAsync(intent.StepId);
            throw;
        }
    }

    // ------------------------------------------------------------------ apply runner

    /// <summary>
    /// Runs a strategy's statements in order. A transactional set is wrapped in one transaction (rolled back on any failure); the delete guard
    /// of a key reconcile is evaluated after its delete and a breach rolls back and throws <see cref="DeleteGuardExceededException"/>.
    /// </summary>
    public async Task<ApplyResult> RunAsync(StatementSet set, CancellationToken ct = default)
    {
        var results = new List<(string, long)>();
        var begun = false;
        long scopeCount = 0;
        try
        {
            if (set.Transactional) { await BeginTransactionAsync(set.Strategy, ct); begun = true; }
            foreach (var s in set.Statements)
            {
                switch (s.Role)
                {
                    case StatementRole.CountScope:
                        scopeCount = Convert.ToInt64(await ScalarAsync(s, ct) ?? 0L, CultureInfo.InvariantCulture);
                        break;
                    case StatementRole.Scalar:
                        await ScalarAsync(s, ct);
                        break;
                    default:
                    {
                        var rows = await ExecuteAsync(s, ct);
                        results.Add((s.Id, rows));
                        if (set.Guard is { } g && (s.Intent.Kind & WriteKind.Delete) != 0 && s.Intent.Risk == WriteRisk.Destructive && !DryRun && !g.Allows(scopeCount, rows))
                            throw new DeleteGuardExceededException(scopeCount, rows, g.MaxRatio);
                        break;
                    }
                }
            }
            if (begun) { await CommitAsync(set.Strategy, ct); begun = false; }
            return new ApplyResult(results);
        }
        catch
        {
            if (begun) await RollbackAsync(set.Strategy, CancellationToken.None);
            throw;
        }
    }

    // ------------------------------------------------------------------ transactions and locks

    public Task BeginTransactionAsync(string stepId, CancellationToken ct = default) => Control(stepId, "begin transaction", e => e.BeginAsync(ct));
    public Task CommitAsync(string stepId, CancellationToken ct = default) => Control(stepId, "commit", e => e.CommitAsync(ct));
    public Task RollbackAsync(string stepId, CancellationToken ct = default) => Control(stepId, "rollback", e => e.RollbackAsync(ct));

    private async Task Control(string stepId, string what, Func<IWriteExecutor, Task> action)
    {
        var id = $"{stepId}:{what.Replace(' ', '-')}";
        var n = Begin(DryRun ? "dry-run" : "begin", id, "Control", what, null);
        if (DryRun) return;
        await action(_executor!);
        Record(n, "end", id, "Control", null, null, "ok", what);
    }

    /// <summary>Takes an application lock without waiting past <paramref name="maxWait"/> (zero = once). Attempt and outcome are logged once, not per retry.</summary>
    public async Task<bool> AcquireLockAsync(string resource, bool shared = false, TimeSpan maxWait = default, CancellationToken ct = default)
    {
        var id = "lock";
        var n = Begin(DryRun ? "dry-run" : "begin", id, "Lock", $"acquire application lock{(shared ? " (shared)" : "")}", null);
        if (DryRun) return true;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        bool granted;
        while (!(granted = await _executor!.TryLockAsync(resource, shared, ct)) && (maxWait == Timeout.InfiniteTimeSpan || clock.Elapsed < maxWait))
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        Record(n, "end", id, "Lock", null, null, granted ? "granted" : "held by another process", null);
        return granted;
    }

    public async Task ReleaseLockAsync(string resource, bool shared = false, CancellationToken ct = default)
    {
        if (DryRun) return;
        var n = Begin("begin", "unlock", "Lock", "release application lock", null);
        await _executor!.UnlockAsync(resource, shared, ct);
        Record(n, "end", "unlock", "Lock", null, null, "released", null);
    }

    // ------------------------------------------------------------------ checks and logging

    private void Check(WriteIntent intent, string step)
    {
        if (_o.Access == AccessMode.ReadOnly)
            throw new GateRefusedException($"`{_o.Command}` has read-only access; step `{step}` ({intent.Kind}) was refused. Nothing was executed.");
        if ((_o.Permitted & intent.Kind) != intent.Kind)
            throw new GateRefusedException($"`{_o.Command}` permits {_o.Permitted} statements; step `{step}` is {intent.Kind}. Nothing was executed.");
    }

    private async Task RecoverAsync(string step)
    {
        // a failed statement must not leave a transaction open (PostgreSQL refuses statements in an aborted one)
        try { Begin("recover", step + ":rollback", "Control", "rollback", null); await _executor!.RollbackAsync(CancellationToken.None); }
        catch (Exception ex) when (ex is not OperationCanceledException) { /* the original error matters more */ }
    }

    private int Begin(string phase, string step, string kind, string? text, SqlStatement? statement)
    {
        lock (_seq)
        {
            var n = ++_ordinal;
            var parms = statement is null ? null : Parameters(statement);
            Record(n, phase, step, kind, text, parms, null, text);
            return n;
        }
    }

    private IReadOnlyList<LoggedParameter> Parameters(SqlStatement s) =>
        [.. s.Parameters.Select(p => new LoggedParameter(p.Name, p.Type.ToString(), ValueHash(p.Value),
            _o.Detail == StatementLogDetail.WithValues ? Redact(Convert.ToString(p.Value, CultureInfo.InvariantCulture) ?? "NULL") : null))];

    /// <summary>Salted per run, so equal values correlate within a run but cannot be looked up in a table of guesses.</summary>
    private string ValueHash(object? value)
    {
        var text = value switch { null => "\0null", byte[] b => Convert.ToBase64String(b), _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" };
        return Convert.ToHexString(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
    }

    private string Redact(string text) => _o.Redactor is { } r ? r.Redact(text) : text;

    private void Record(int n, string phase, string step, string kind, string? text, IReadOnlyList<LoggedParameter>? parameters, string? outcome, string? hashSource)
    {
        // text is kept only from WithText up, and only at the opening line; hashes are always kept
        var keepText = _o.Detail >= StatementLogDetail.WithText && phase is "begin" or "dry-run";
        try
        {
            _o.Log.Append(new StatementLogEntry(_o.RunId, n, DateTime.UtcNow, _o.Command, phase, step, kind,
                Hash(hashSource ?? step), keepText && text is not null ? Redact(text) : null,
                phase is "begin" or "dry-run" ? parameters : null, outcome is null ? null : Redact(outcome)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new GateRefusedException($"The statement log could not be written ({ex.GetType().Name}), so step `{step}` was not executed.");
        }
    }

    private void TryRecord(int n, SqlStatement s, string outcome) => TryRecord(n, s.Id, s.Intent.Kind.ToString(), outcome, s.Text);
    private void TryRecord(int n, string step, string kind, string outcome, string? hashSource = null)
    {
        try { Record(n, "end", step, kind, null, null, outcome, hashSource); } catch (GateRefusedException) { }
    }

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    /// <summary>Exception type and driver error code only: driver messages can quote data.</summary>
    private static string Describe(Exception ex) => ex is DbException { ErrorCode: var code } && code != 0 ? $"{ex.GetType().Name} code={code}" : ex.GetType().Name;

    public ValueTask DisposeAsync() => _executor?.DisposeAsync() ?? ValueTask.CompletedTask;
}
