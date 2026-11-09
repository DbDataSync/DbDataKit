using DbDataKit.Runner.Kit;
using DbDataSync.TaskRunner.Runner;

namespace DbDataSync.TaskRunner.Tests.Runner;

/// <summary>
/// The runner's queue of one owner, in memory, with the rules a drain relies on: one item in flight per (kind, subject, part), a claim only of a pending item, only the claimer starts, finishes or releases, and a cancel mark
/// per item run. The real one is <c>ModuleWorkQueueStore</c> behind the protocol (tested on three engines in <c>EngineWorkProtocolTests</c> of the kit); this is what the drain's own logic is tested against.
/// </summary>
internal sealed class FakeDrainQueue : IDrainQueue
{
    public sealed class Row(long id, string kind, string subject, string part, string resource, string lane, string? payload)
    {
        public long Id { get; } = id;
        public string Kind { get; } = kind;
        public string Subject { get; } = subject;
        public string Part { get; } = part;
        public string Resource { get; } = resource;
        public string Lane { get; } = lane;
        public string? Payload { get; } = payload;
        public Guid RunId { get; } = Guid.NewGuid();
        public string Status { get; set; } = "Pending";
        public DateTimeOffset EnqueuedAt { get; } = DateTimeOffset.UtcNow;
        public bool CancelRequested { get; set; }
    }

    private readonly object _gate = new();
    private long _next;

    public List<Row> Rows { get; } = [];

    public List<string> Calls { get; } = [];

    /// <summary>When set, claims are refused after this many (the runner told the run to stop).</summary>
    public bool Stopped { get; set; }

    public Action<Row>? OnClaim { get; set; }

    private static bool InFlight(Row r) => r.Status is "Pending" or "Claimed" or "Running";

    public Task<Guid> EnqueueAsync(string kind, string subject, string part, string resource, string lane, string? payload, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add($"enqueue {kind}/{subject}");
            if (Rows.FirstOrDefault(r => r.Kind == kind && r.Subject == subject && r.Part == part && InFlight(r)) is { } existing) return Task.FromResult(existing.RunId);
            var row = new Row(++_next, kind, subject, part, resource, lane, payload);
            Rows.Add(row);
            return Task.FromResult(row.RunId);
        }
    }

    public Task<RunnerWorkItem?> ClaimAsync(string? lane, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add("claim");
            if (Stopped) return Task.FromResult<RunnerWorkItem?>(null);
            var row = Rows.Where(r => r.Status == "Pending" && (lane is null || r.Lane == lane)
                    && !Rows.Any(b => b != r && b.Kind == r.Kind && b.Subject == r.Subject && b.Status is "Claimed" or "Running" && (b.Part == "" || r.Part == "" || b.Part == r.Part))
                    && (r.Resource == "" || !Rows.Any(b => b != r && b.Resource == r.Resource && b.Status is "Claimed" or "Running")))
                .OrderBy(r => r.EnqueuedAt).ThenBy(r => r.Id).FirstOrDefault();
            if (row is null) return Task.FromResult<RunnerWorkItem?>(null);
            row.Status = "Claimed";
            OnClaim?.Invoke(row);
            return Task.FromResult<RunnerWorkItem?>(new RunnerWorkItem(row.Id, row.Kind, row.Subject, row.Part, row.Resource, row.Lane, row.RunId, row.Payload));
        }
    }

    public Task<bool> StartAsync(long id, CancellationToken ct) => Move(id, "start", from: ["Claimed", "Running"], to: "Running");

    public Task<bool> FinishAsync(long id, WorkFinish outcome, CancellationToken ct) => Move(id, $"finish {outcome}", from: ["Claimed", "Running"], to: outcome.ToString());

    public Task<bool> ReleaseAsync(long id, CancellationToken ct) => Move(id, "release", from: ["Claimed", "Running"], to: "Pending");

    private Task<bool> Move(long id, string call, string[] from, string to)
    {
        lock (_gate)
        {
            Calls.Add($"{call} {id}");
            var row = Rows.Single(r => r.Id == id);
            if (!from.Contains(row.Status)) return Task.FromResult(false);
            row.Status = to;
            return Task.FromResult(true);
        }
    }

    public Task<bool> IsCancelRequestedAsync(Guid itemRunId, CancellationToken ct)
    {
        lock (_gate) return Task.FromResult(Rows.Any(r => r.RunId == itemRunId && r.CancelRequested));
    }

    public Task<WorkSnapshot> StateAsync(IReadOnlyList<string> kinds, CancellationToken ct)
    {
        lock (_gate)
        {
            Calls.Add("state");
            var result = kinds.ToDictionary(
                kind => kind,
                kind => (IReadOnlyDictionary<string, WorkSubject>)Rows.Where(r => r.Kind == kind).GroupBy(r => r.Subject)
                    .ToDictionary(g => g.Key, g => new WorkSubject(g.Max(r => r.EnqueuedAt), g.Any(InFlight))));
            return Task.FromResult(new WorkSnapshot(Rows.Any(InFlight), result));
        }
    }
}
