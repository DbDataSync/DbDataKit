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

    private void Enter()
    {
        lock (_gate) Calls++;
        if (Down) throw new RunnerClientException("https://runner.test:8495 could not be reached (connection refused).");
    }

    public Task<IReadOnlyList<ClientRun>> RunsAsync(string? task, int limit, DateTimeOffset? before, string? outcome, CancellationToken ct)
    {
        Enter();
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
        Enter();
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
        Enter();
        byte[]? content;
        lock (_gate) content = _payloads.GetValueOrDefault((task, step, payload));
        if (content is null) return false;
        await destination.WriteAsync(content, ct);
        return true;
    }

    public Task<IReadOnlyList<ClientCheckpointItem>> CheckpointAsync(string task, string name, CancellationToken ct)
    {
        Enter();
        return Task.FromResult<IReadOnlyList<ClientCheckpointItem>>([.. Checkpoint]);
    }

    public Task<ClientTaskSchedule> ScheduleOfAsync(string task, CancellationToken ct) => throw new NotSupportedException();

    public Task SetOwnerAsync(string task, string owner, string? reason, ClientSeed? seed, CancellationToken ct) => throw new NotSupportedException();

    public Task<string?> RunNowAsync(string task, IReadOnlyDictionary<string, string>? inputs, CancellationToken ct) => throw new NotSupportedException();

    public Task PauseAsync(string task, CancellationToken ct) => throw new NotSupportedException();

    public Task ResumeAsync(string task, CancellationToken ct) => throw new NotSupportedException();

    public Task<ClientTaskStatus?> StatusAsync(string task, CancellationToken ct) => throw new NotSupportedException();

    public Task EnqueueWorkAsync(string task, string kind, string subject, string part, string? payload, CancellationToken ct) => throw new NotSupportedException();
}
