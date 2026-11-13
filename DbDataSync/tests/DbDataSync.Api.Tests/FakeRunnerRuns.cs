using DbDataKit.Runner.Client;
using DbDataSync.Api.Runner;
using DbDataSync.State;

namespace DbDataSync.Api.Tests;

/// <summary>
/// A runner that answers what the Sync API reads of the runs of a task (plan 28c E13), scripted by a test: its runs, the lines of each one's log, the steps its runs recorded and the checkpoint it holds. <see cref="Down"/> makes every
/// call fail as an unreachable runner does. The calls are counted, so a test can say how many a poll made.
/// </summary>
public sealed class FakeRunnerRuns : IRunnerSync, IRunnerRuns
{
    private readonly object _gate = new();
    private readonly List<ClientRun> _runs = [];
    private readonly Dictionary<Guid, List<ClientLogLine>> _logs = [];
    private readonly List<(string Task, string Step, ClientStep Entry)> _steps = [];
    private long _line;

    public bool Down { get; set; }

    public int Calls { get; private set; }

    public List<ClientCheckpointItem> Checkpoint { get; } = [];

    public ClientRun AddRun(string task, string outcome, DateTimeOffset started, DateTimeOffset? finished = null, long? rows = null, string? failedItem = null, string? kind = null, string? message = null, Guid? id = null)
    {
        var run = new ClientRun(id ?? Guid.NewGuid(), task, 1, outcome, started, finished, rows, failedItem, kind, message, "1");
        lock (_gate) _runs.Add(run);
        return run;
    }

    public void Finish(Guid id, string outcome, DateTimeOffset at, long? rows = null)
    {
        lock (_gate)
        {
            var at0 = _runs.FindIndex(r => r.Id == id);
            _runs[at0] = _runs[at0] with { Outcome = outcome, Finished = at, Rows = rows };
        }
    }

    public long Log(Guid run, string message, DateTimeOffset? at = null, string level = "Information")
    {
        lock (_gate)
        {
            if (!_logs.TryGetValue(run, out var lines)) _logs[run] = lines = [];
            lines.Add(new ClientLogLine(++_line, at ?? DateTimeOffset.UtcNow, level, null, message));
            return _line;
        }
    }

    public void AddStep(string task, string step, string key, string resultJson, DateTimeOffset started)
    {
        lock (_gate) _steps.Add((task, step, new ClientStep(key, started, started, resultJson)));
    }

    public void AddPass(string task, PassRecord pass) => AddStep(task, PassRecordText.StepName, pass.RunId.ToString("N"), PassRecordText.Serialize(pass), pass.StartedAt);

    private readonly List<string?> _asked = [];

    /// <summary>How many calls named a task containing <paramref name="fragment"/>: the background pollers of the host (lag, run monitor) call the runner too, so "this request asked nobody" is counted per replication, not by the total.</summary>
    public int AskedAbout(string fragment)
    {
        lock (_gate) return _asked.Count(t => t is not null && t.Contains(fragment, StringComparison.Ordinal));
    }

    private void Enter(string? task = null)
    {
        lock (_gate) { Calls++; _asked.Add(task); }
        if (Down) throw new RunnerClientException("https://runner.test:8495 could not be reached (connection refused).");
    }

    public Task<IReadOnlyList<ClientRun>> RunsAsync(string? task, int limit, DateTimeOffset? before, string? outcome, CancellationToken ct)
    {
        Enter(task);
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ClientRun>>([.. _runs.Where(r => (task is null || r.Task == task) && (before is null || r.Started < before) && (outcome is null || r.Outcome == outcome)).OrderByDescending(r => r.Started).Take(limit)]);
    }

    public Task<ClientRun?> RunAsync(Guid id, CancellationToken ct)
    {
        Enter();
        lock (_gate) return Task.FromResult(_runs.FirstOrDefault(r => r.Id == id));
    }

    public Task<IReadOnlyList<ClientLogLine>?> LogsAsync(Guid id, long? after, int limit, CancellationToken ct)
    {
        Enter();
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ClientLogLine>?>(_runs.All(r => r.Id != id) ? null : [.. (_logs.GetValueOrDefault(id) ?? []).Where(l => after is null || l.Id > after).Take(limit)]);
    }

    public Task<IReadOnlyList<ClientStep>> StepsAsync(string task, string step, int limit, DateTimeOffset? before, string? key, CancellationToken ct)
    {
        Enter(task);
        lock (_gate)
            return Task.FromResult<IReadOnlyList<ClientStep>>([.. _steps.Where(s => s.Task == task && s.Step == step && (before is null || s.Entry.Started < before) && (key is null || s.Entry.Key == key)).Select(s => s.Entry).OrderByDescending(s => s.Started).Take(limit)]);
    }

    /// <summary>Adds the file a step kept as its streamed result; <see cref="StepPayloadAsync"/> answers it for that task and step (a payload not added reads as one the retention removed).</summary>
    public void AddPayload(string task, string step, Guid id, byte[] content)
    {
        lock (_gate) _payloads[(task, step, id)] = content;
    }

    private readonly Dictionary<(string Task, string Step, Guid Id), byte[]> _payloads = [];

    public async Task<bool> StepPayloadAsync(string task, string step, Guid payload, Stream destination, CancellationToken ct)
    {
        Enter(task);
        byte[]? content;
        lock (_gate) content = _payloads.GetValueOrDefault((task, step, payload));
        if (content is null) return false;
        await destination.WriteAsync(content, ct);
        return true;
    }

    public Task<IReadOnlyList<ClientCheckpointItem>> CheckpointAsync(string task, string name, CancellationToken ct)
    {
        Enter(task);
        return Task.FromResult<IReadOnlyList<ClientCheckpointItem>>([.. Checkpoint]);
    }

    public Task<ClientTaskSchedule> ScheduleOfAsync(string task, CancellationToken ct) => throw new NotSupportedException();

    public Task SetOwnerAsync(string task, string owner, string? reason, ClientSeed? seed, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> RunNowAsync(string task, IReadOnlyDictionary<string, string>? inputs, CancellationToken ct) => throw new NotSupportedException();

    public Task PauseAsync(string task, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>The runs the Sync API asked to cancel; a run the fake knows is cancelled (true), any other is not on the runner (false).</summary>
    public List<Guid> CancelRequests { get; } = [];

    public Task<bool> CancelRunAsync(Guid runId, CancellationToken ct)
    {
        if (Down) throw new RunnerClientException("The runner is down.");
        lock (_gate)
        {
            CancelRequests.Add(runId);
            return Task.FromResult(_runs.Any(r => r.Id == runId && r.Finished is null));
        }
    }

    public Task ResumeAsync(string task, CancellationToken ct) => throw new NotSupportedException();

    /// <summary>What the runner answers for the task's row (null: it has none).</summary>
    public ClientTaskStatus? TaskStatus { get; set; }

    public Task<ClientTaskStatus?> StatusAsync(string task, CancellationToken ct)
    {
        if (Down) throw new RunnerClientException("The runner is down.");
        return Task.FromResult(TaskStatus);
    }

    /// <summary>How many of the next changes of a checkpoint item find a drain has moved it first (the version moves, the change is refused).</summary>
    public int MovedUnderneath { get; set; }

    /// <summary>The hand changes made to checkpoint items (the key and what was written and removed).</summary>
    public List<(string Key, IReadOnlyDictionary<string, ClientPartValue> Set, IReadOnlyCollection<string> Remove)> CheckpointSets { get; } = [];

    public Task<long?> SetCheckpointAsync(string task, string name, string key, long version, IReadOnlyDictionary<string, ClientPartValue> set, IReadOnlyCollection<string> remove, CancellationToken ct)
    {
        if (Down) throw new RunnerClientException("The runner is down.");
        lock (_gate)
        {
            var at = Checkpoint.FindIndex(i => i.Key == key);
            var current = at < 0 ? null : Checkpoint[at];
            if (MovedUnderneath > 0)
            {
                MovedUnderneath--;
                var bumped = new ClientCheckpointItem(key, (current?.Version ?? 0) + 1, current?.Parts ?? new Dictionary<string, string>());
                if (at < 0) Checkpoint.Add(bumped); else Checkpoint[at] = bumped;
                return Task.FromResult<long?>(null);
            }

            if ((current?.Version ?? 0) != version) return Task.FromResult<long?>(null);
            var parts = new Dictionary<string, string>(current?.Parts ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            foreach (var part in remove) parts.Remove(part);
            foreach (var (part, value) in set) parts[part] = value.Value;
            var next = new ClientCheckpointItem(key, version + 1, parts);
            if (at < 0) Checkpoint.Add(next); else Checkpoint[at] = next;
            CheckpointSets.Add((key, set, remove));
            return Task.FromResult<long?>(next.Version);
        }
    }

    public Task EnqueueWorkAsync(string task, string kind, string subject, string part, string? payload, CancellationToken ct) => throw new NotSupportedException();
}
