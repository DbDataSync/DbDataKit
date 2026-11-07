using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol.V1;
using DbDataKit.State;

namespace DbDataKit.Runner.Protocol.Server;

/// <summary>
/// What the runner grants a task for one run (plan 28a DR3): the start message it will be given, the attempt chain the run belongs to, and the state the run's calls reach. The token that comes of it names this run
/// and nothing else: its task, its package and channel, its chain.
/// </summary>
/// <param name="Start">Run id, attempt, task, inputs as strings, connection handles, the cancellation grace and the protocol version the runner serves.</param>
/// <param name="Chain">The attempt chain: a retry of a work item passes the same chain, so its steps and units answer from memory.</param>
/// <param name="Environment">The state database the run's progress, steps, units, payloads and log live in, with the package and channel they are kept under.</param>
/// <param name="Lifetime">How long the token works if the run does not end first; null takes the host's default.</param>
public sealed record RunGrant(TaskStart Start, string Chain, RunnerEnvironment Environment, TimeSpan? Lifetime = null);

/// <summary>One line of lineage a task reported.</summary>
public sealed record LineageEntry(IReadOnlyList<string> Reads, IReadOnlyList<string> Writes);

/// <summary>What a run reported one way: counts, the status line, lineage, and when it last said it was alive. For the runner's own view of the run.</summary>
public sealed record RunTelemetry(
    IReadOnlyDictionary<string, long> Counts,
    string? Status,
    IReadOnlyList<LineageEntry> Lineage,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, long>> UnitCounts,
    DateTimeOffset? LastHeartbeat,
    bool Started,
    bool Ended,
    string? CancelReason,
    ChunkStats Uploaded,
    ChunkStats Downloaded);

/// <summary>How many chunks a stream carried and the largest one (plan 28b: a stream holds one chunk of at most 1 MiB at a time).</summary>
public sealed record ChunkStats(long Chunks, long Bytes, int LargestChunk);

/// <summary>The state of one run behind its token: the in-process backend over the stores, and what the task was given and has touched, so a call can only reach what belongs to this run.</summary>
internal sealed class RunSession
{
    private readonly object _gate = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly List<LineageEntry> _lineage = [];
    private readonly Dictionary<string, IReadOnlyDictionary<string, long>> _unitCounts = new(StringComparer.Ordinal);
    private string? _status;
    private StartInfo? _info;
    private Task<StartInfo>? _start;

    public RunSession(RunGrant grant, DateTimeOffset expiresAt, TimeProvider time)
    {
        Grant = grant;
        ExpiresAt = expiresAt;
        Time = time;
        Identity = new RunIdentity(grant.Start.RunId, grant.Start.Attempt, grant.Chain, grant.Start.Task);
        Backend = new InProcessRun(grant.Environment, Identity);
        Progress = new ProgressStore(grant.Environment.Database, new ProgressKey(Identity.Task.Package, Identity.Task.Channel, Identity.Task.Task));
    }

    public RunGrant Grant { get; }

    /// <summary>The token, kept in memory to revoke it when the run ends.</summary>
    public string Token { get; internal set; } = "";

    public RunIdentity Identity { get; }

    public InProcessRun Backend { get; }

    public ProgressStore Progress { get; }

    public DateTimeOffset ExpiresAt { get; }

    public TimeProvider Time { get; }

    /// <summary>The payloads this run may read or complete a step with: the ones it uploaded, and the ones the runner handed it (a reused step's result, a progress value, an inbox body).</summary>
    public ConcurrentDictionary<Guid, byte> Payloads { get; } = new();

    public ConcurrentDictionary<(string Name, string Key), Guid> StepIds { get; } = new();

    public ConcurrentDictionary<Guid, byte> TakenInbox { get; } = new();

    public TaskCompletionSource<string> CancelSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource EndSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool Started => _info is not null;

    public bool Ended { get; private set; }

    public DateTimeOffset? LastHeartbeat { get; set; }

    public string? CancelReason { get; private set; }

    public bool RequestCancel(string reason)
    {
        CancelReason ??= reason;
        return CancelSignal.TrySetResult(reason);
    }

    public void MarkEnded()
    {
        Ended = true;
        EndSignal.TrySetResult();
    }

    /// <summary>Records the start once and returns the same message for a repeated call: the progress is read once (DR5).</summary>
    public Task<StartInfo> StartAsync(Func<StartInfo> build, CancellationToken ct)
    {
        lock (_gate) return _start ??= RunAsync();

        async Task<StartInfo> RunAsync()
        {
            await Backend.StartAsync(ct).ConfigureAwait(false);
            _info = build();
            return _info;
        }
    }

    public void Add(ReportBatch batch)
    {
        lock (_gate)
        {
            foreach (var (name, n) in batch.Counts) _counts[name] = _counts.GetValueOrDefault(name) + n;
            if (batch.Status.Length > 0) _status = batch.Status;
            foreach (var record in batch.Lineage) _lineage.Add(new LineageEntry([.. record.Reads], [.. record.Writes]));
        }
    }

    private long _upChunks, _upBytes, _downChunks, _downBytes;
    private int _upLargest, _downLargest;

    public void NoteChunk(bool upload, int size)
    {
        lock (_gate)
        {
            if (upload)
            {
                _upChunks++;
                _upBytes += size;
                _upLargest = Math.Max(_upLargest, size);
            }
            else
            {
                _downChunks++;
                _downBytes += size;
                _downLargest = Math.Max(_downLargest, size);
            }
        }
    }

    public void SetUnitCounts(string name, string key, IReadOnlyDictionary<string, long> counts)
    {
        lock (_gate) _unitCounts[$"{name}/{key}"] = counts;
    }

    public RunTelemetry Telemetry()
    {
        lock (_gate)
            return new RunTelemetry(new Dictionary<string, long>(_counts, StringComparer.Ordinal), _status, [.. _lineage], new Dictionary<string, IReadOnlyDictionary<string, long>>(_unitCounts, StringComparer.Ordinal),
                LastHeartbeat, Started, Ended, CancelReason, new ChunkStats(_upChunks, _upBytes, _upLargest), new ChunkStats(_downChunks, _downBytes, _downLargest));
    }
}

/// <summary>The run tokens that work now. A token is random, names exactly one run, stops working when that run ends or when its lifetime is over, and is never reused. Only a hash of it is kept.</summary>
internal sealed class RunSessions(TimeProvider time, TimeSpan defaultLifetime)
{
    private readonly ConcurrentDictionary<string, RunSession> _live = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Reason, RunSession Session)> _gone = new(StringComparer.Ordinal);

    public TimeProvider Time { get; } = time;

    private static string KeyOf(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public (string Token, RunSession Session) Add(RunGrant grant)
    {
        var lifetime = grant.Lifetime ?? defaultLifetime;
        if (lifetime <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(grant), "A run token lives for a positive time.");
        var session = new RunSession(grant, Time.GetUtcNow() + lifetime, Time);
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        session.Token = token;
        if (!_live.TryAdd(KeyOf(token), session)) throw new InvalidOperationException("A run token collided (this does not happen).");
        return (token, session);
    }

    public bool TryGet(string? token, out RunSession session, out string reason)
    {
        session = null!;
        reason = "The run token is missing: every call carries it in the metadata key " + RunnerProtocolNames.TokenHeader + ".";
        if (string.IsNullOrEmpty(token)) return false;
        var key = KeyOf(token);
        if (!_live.TryGetValue(key, out var found))
        {
            reason = _gone.TryGetValue(key, out var gone) ? gone.Reason : "The run token is not one this runner issued.";
            return false;
        }

        if (Time.GetUtcNow() >= found.ExpiresAt)
        {
            Revoke(token, "The run token has expired.");
            reason = "The run token has expired.";
            return false;
        }

        session = found;
        return true;
    }

    public RunSession? Find(string token) => _live.TryGetValue(KeyOf(token), out var session) ? session : null;

    /// <summary>The session of a token that is live or ended and not yet forgotten: what the runner reads after a run to record what it reported.</summary>
    public RunSession? FindEvenIfEnded(string token)
    {
        var key = KeyOf(token);
        return _live.TryGetValue(key, out var session) ? session : _gone.TryGetValue(key, out var gone) ? gone.Session : null;
    }

    /// <summary>The runner is done with the token: a live one is revoked, an ended one is forgotten (its reason and its telemetry go).</summary>
    public bool Forget(string token, string reason)
    {
        var revoked = Revoke(token, reason);
        return _gone.TryRemove(KeyOf(token), out _) || revoked;
    }

    public bool Revoke(string token, string reason)
    {
        var key = KeyOf(token);
        if (!_live.TryRemove(key, out var session)) return false;
        _gone[key] = (reason, session);
        session.MarkEnded();
        return true;
    }
}
