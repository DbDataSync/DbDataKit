using System.Collections.Concurrent;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Execution;
using DbDataKit.Runner.Triggers;
using DbDataKit.Scheduling;
using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Scheduler;

/// <summary>
/// The runner's scheduler loop (plan 28a R09, decisions DR7, DR8, DR9, DR11). One loop reads the <c>task_projection</c> rows of the channels it serves (and nothing else: no manifest is parsed to decide when a task is due), asks the shared
/// <see cref="ScheduleEvaluator"/> whether each task is due against its <c>job_state</c>, honours <c>job_pause</c>, and puts a work item on the shared queue; it claims items with a lease, runs each through the <see cref="ITaskRunner"/> (the R06
/// executor) from a materialized, hash-verified copy of its version, renews the lease while the task runs and finishes the item with the status <see cref="TaskEndMapping.WorkItemStatusOf"/> gives.
/// <list type="bullet">
/// <item><b>An item</b> is owner <c>package/channel</c>, kind <c>task</c>, subject the task's name, resource <c>package:&lt;package&gt;</c> with the manifest's <c>concurrency</c> as its limit (none: no resource), payload a <see cref="WorkPayload"/>.
/// The queue holds one item of a task at a time, so a task never overlaps itself; a manifest's <c>allow_overlap</c> gives each item a part of its own. A run lock (<c>run_lock</c>) on the same name guards a start that did not come through the queue.</item>
/// <item><b>A broken projection row</b> (schedule or limits that do not read, a name that breaks the rule, labels that are not an array) is skipped and reported in <see cref="Status"/> and the log; the other rows run (DR11).</item>
/// <item><b>The lease.</b> A running item is renewed every <see cref="RunnerSchedulerOptions.Heartbeat"/> (30 s) and held for <see cref="RunnerSchedulerOptions.Lease"/> (90 s); a loop that stops renewing loses the item to another loop's
/// <see cref="ModuleWorkQueueStore.ReleaseExpired"/>, and its late finish is refused because the finish checks who holds the item. A loop that finds it lost the lease cancels its task. The new loop gives the item a new run id and the next
/// attempt number (the dead run keeps its record, marked interrupted), on the same chain.</item>
/// <item><b>Retries</b> (R08): a failed run is planned by <see cref="RetryPlanner"/>; the same item goes back to the queue with a not-before time, a new run id and the attempt number, on the same chain and, once an attempt had started, on the
/// same version, whatever has been activated since.</item>
/// <item><b>Stopping.</b> <see cref="RunAsync"/> ends when its token does: running tasks are cancelled (the executor gives them the grace period), their items go back to the queue unfinished, and the leases this loop still holds are released.</item>
/// </list>
/// The clock is the state database's (<see cref="StateDatabase.Time"/>): the queue's leases and the schedules are judged on one clock, which a test can move. The tick and the heartbeat wait in real time.
/// </summary>
public sealed partial class RunnerScheduler
{
    /// <summary>The kind of every work item of the runner.</summary>
    public const string Kind = "task";

    private readonly StateDatabase _database;
    private readonly ITaskRunner _runner;
    private readonly ArtifactCache _cache;
    private readonly IVersionSource _versions;
    private readonly RunnerSchedulerOptions _options;
    private readonly TimeProvider _time;
    private readonly ModuleWorkQueueStore _queue;
    private readonly ModuleJobStateStore _jobs;
    private readonly ModuleJobPauseStore _pauses;
    private readonly ModuleRunLockStore _locks;
    private readonly TaskProjections _projections;
    private readonly ActivationHolds _holds;
    private readonly RunnerInstances _instances;
    private readonly RunStore _runs;
    private readonly WorkItemRetries _retries;
    private readonly ILogger? _log;

    private readonly ConcurrentDictionary<long, Running> _running = new();
    private readonly ConcurrentQueue<SchedulerProblem> _problems = new();
    private readonly SemaphoreSlim _tickGate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private IReadOnlyDictionary<string, SchedulerProblem> _rowProblems = new Dictionary<string, SchedulerProblem>();

    public RunnerScheduler(StateDatabase database, ITaskRunner runner, ArtifactCache cache, IVersionSource versions, RunnerSchedulerOptions options)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _runner = runner ?? throw new ArgumentNullException(nameof(runner));
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
        _versions = versions ?? throw new ArgumentNullException(nameof(versions));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        options.Validate();
        _time = database.Time;
        _queue = new ModuleWorkQueueStore(database, options.Service) { LeaseDuration = options.Lease };
        _jobs = new ModuleJobStateStore(database, options.Service);
        _pauses = new ModuleJobPauseStore(database, options.Service);
        _locks = new ModuleRunLockStore(database, options.Service);
        _projections = new TaskProjections(database);
        _holds = new ActivationHolds(database);
        _instances = new RunnerInstances(database);
        _runs = new RunStore(database, options.Service);
        _retries = new WorkItemRetries(_queue);
        _log = options.Logger;
        _triggers = new TriggerEngine(database, options.Service, this, options.Triggers, options.Logger);
    }

    /// <summary>This loop's claim name.</summary>
    public string WorkerId => _options.WorkerId;

    /// <summary>
    /// For a test or a diagnosis: while true the loop does not renew its leases, as a runner that stalled would not. The task keeps running; another loop takes the item once the lease runs out and this loop's finish is refused.
    /// </summary>
    public bool SuspendHeartbeats { get; set; }

    /// <summary>The number of runs this loop has going.</summary>
    public int RunningCount => _running.Count;

    // ---- loop control

    /// <summary>
    /// Runs the loop: a tick now and then every <see cref="RunnerSchedulerOptions.Tick"/> until <paramref name="ct"/> is cancelled; then stops gracefully (running tasks are cancelled and given the grace period, their items go back to the
    /// queue, the leases this loop holds are released) and returns. A tick that throws (the state database is down) is logged and the loop goes on.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.Tick, _time);
            do
            {
                if (SuspendHeartbeats) continue;   // a frozen process neither renews nor takes anything
                try { await TickAsync(ct).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Report("scheduler", $"A tick failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // asked to stop
        }
        finally
        {
            await StopAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Cancels the running tasks, waits for them (the grace period and a little more), and releases every lease this loop holds. <see cref="RunAsync"/> calls it when it ends.</summary>
    public async Task StopAsync()
    {
        _stop.Cancel();
        var done = _running.Values.Select(r => r.Done.Task).ToArray();
        if (done.Length > 0)
        {
            try { await Task.WhenAll(done).WaitAsync(_options.CancellationGrace + TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (TimeoutException) { Report("scheduler", "Some tasks did not stop within the grace period; their leases are released anyway."); }
        }

        var released = _queue.ReleaseClaimsBy(_options.WorkerId);
        if (released > 0) _log?.LogInformation("Released {Count} unfinished item(s) held by {Worker}.", released, _options.WorkerId);
    }

    /// <summary>Waits until no task of this loop is running (tests, and a host that wants quiet before a restart).</summary>
    public async Task WhenIdleAsync()
    {
        while (true)
        {
            var running = _running.Values.Select(r => r.Done.Task).ToArray();
            if (running.Length == 0) return;
            await Task.WhenAll(running).ConfigureAwait(false);
        }
    }

    // ---- one tick

    /// <summary>One pass: gives up leases that ran out, queues every task that is due, and starts claimed items up to the bound. Returns what it did.</summary>
    public async Task<TickReport> TickAsync(CancellationToken ct = default)
    {
        await _tickGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var expired = _queue.ReleaseExpired();
            var (served, rowProblems) = ReadProjection();
            _rowProblems = rowProblems;

            var enqueued = new List<TaskRef>();
            var refused = 0;
            var paused = 0;
            foreach (var task in served)
            {
                ct.ThrowIfCancellationRequested();
                var owner = OwnerOf(task.Ref);
                var state = _jobs.Get(owner, task.Ref.Task);
                var decision = ScheduleEvaluator.Evaluate(task.Schedule, state?.HandledUpTo, _time.GetUtcNow());
                if (decision.HandledUpTo is { } handled && handled != state?.HandledUpTo) _jobs.SaveHandled(owner, task.Ref.Task, handled);
                if (!decision.Due) continue;
                if (!RunGate.ShouldRun(enabled: true, paused: _pauses.IsPaused(owner, task.Ref.Task)))
                {
                    paused++;          // the time is handled above and not run: resuming does not start the task for the times it was paused through
                    continue;
                }

                if (TryEnqueue(task, WorkPayload.ScheduleTrigger, null, null) is null) refused++;
                else enqueued.Add(task.Ref);
            }

            await _triggers.TickAsync(ct).ConfigureAwait(false);   // R13: events become work items on the same path

            var claimed = ClaimAndStart(served.Select(t => OwnerOf(t.Ref)).Distinct(StringComparer.Ordinal).ToList(), served);
            return new TickReport(enqueued, refused, paused, claimed, rowProblems.Count, expired);
        }
        finally
        {
            _tickGate.Release();
        }
    }

    private sealed record Served(TaskProjection Row, TaskRef Ref, Schedule Schedule, TaskLimits Limits, bool AllowOverlap);

    /// <summary>The valid rows of the served channels, and a problem for each row that is not (DR11). A row that cannot be read does not stop the others.</summary>
    private (List<Served> Served, IReadOnlyDictionary<string, SchedulerProblem> Problems) ReadProjection()
    {
        var served = new List<Served>();
        var problems = new Dictionary<string, SchedulerProblem>(StringComparer.Ordinal);
        foreach (var row in Rows())
        {
            var subject = $"{row.Package}/{row.Channel}/{row.Task}";
            if (Parse(row) is { } task) served.Add(task);
            else
            {
                var message = ProblemOf(row);
                problems[subject] = new SchedulerProblem(subject, message, _time.GetUtcNow());
                if (!_rowProblems.TryGetValue(subject, out var known) || known.Message != message)
                    _log?.LogWarning("The projection row {Subject} is skipped: {Message}", subject, message);
            }
        }

        return (served, problems);
    }

    private IEnumerable<TaskProjection> Rows()
    {
        foreach (var channel in _options.Channels.Distinct(StringComparer.Ordinal))
            foreach (var row in _projections.ListByChannel(channel))
                if (_options.Packages is null || _options.Packages.Contains(row.Package))
                    yield return row;
    }

    private Served? Parse(TaskProjection row) => ParseOrProblem(row, out var served) ? served : null;

    private static string ProblemOf(TaskProjection row) => ParseOrProblem(row, out _, out var problem) ? "" : problem!;

    private static bool ParseOrProblem(TaskProjection row, out Served? served) => ParseOrProblem(row, out served, out _);

    private static bool ParseOrProblem(TaskProjection row, out Served? served, out string? problem)
    {
        served = null;
        problem = row.Problem;
        if (problem is not null) return false;
        if (TaskRef.Problem(row.Package, row.Channel, row.Task) is { } name)
        {
            problem = name;
            return false;
        }

        try
        {
            var schedule = ProjectionDocuments.ParseSchedule(row.Schedule);
            var (limits, overlap) = ProjectionDocuments.ParseLimits(row.Limits);
            served = new Served(row, new TaskRef(row.Package, row.Channel, row.Task), schedule, limits, overlap);
            return true;
        }
        catch (FormatException ex)
        {
            problem = ex.Message;
            return false;
        }
    }

    private static string OwnerOf(TaskRef task) => $"{task.Package}/{task.Channel}";

    /// <summary>Puts one item of the task on the queue, or returns null when an earlier one is queued or running and the task does not allow overlap.</summary>
    private Guid? TryEnqueue(Served task, string trigger, IReadOnlyDictionary<string, string>? inputs, string? requestedBy,
        string? part = null, int triggerDepth = 0, string? triggerSource = null, Action<System.Data.Common.DbConnection, System.Data.Common.DbTransaction, Guid>? alsoWrite = null)
    {
        var payload = new WorkPayload
        {
            Task = task.Ref.ToString(),
            Version = task.Row.Version,
            Inputs = inputs is null ? [] : new Dictionary<string, string>(inputs),
            Chain = Guid.CreateVersion7().ToString("D"),
            Trigger = trigger,
            RequestedBy = requestedBy,
            TriggerDepth = triggerDepth,
            TriggerSource = triggerSource,
        };
        var concurrency = task.Limits.Concurrency;
        try
        {
            return _queue.EnqueueOrThrow(
                OwnerOf(task.Ref), Kind, task.Ref.Task,
                part: part ?? (task.AllowOverlap ? payload.Chain : ModuleWorkQueueStore.NoPart),
                resource: concurrency is null ? "" : $"package:{task.Ref.Package}",
                payloadJson: payload.ToJson(),
                resourceLimit: concurrency ?? 1,
                alsoWrite: alsoWrite);
        }
        catch (WorkQueueCollisionException)
        {
            return null;
        }
    }

    /// <summary>This runner's labels as stored now (the administrator's and the reported ones); none when the loop is not an instance. An administrator adding a label takes effect at the next tick.</summary>
    private IReadOnlySet<string> MyLabels() =>
        _options.Instance?.Invoke() is { } id ? _instances.LabelSet(id) : new HashSet<string>(StringComparer.Ordinal);

    private int ClaimAndStart(IReadOnlyList<string> owners, IReadOnlyList<Served> served)
    {
        // routing (plan 28 question 7): an item is claimed only by a runner that has every label its task requires; the others stay Pending
        var mine = MyLabels();
        // an activation waits for the runs of these packages: nothing of them starts until it has moved the version (plan 30 section 3.5)
        var held = _holds.Held(DefinitionVersionSource.Kind).Select(h => $"{h.Name}/{h.Channel}").ToHashSet(StringComparer.Ordinal);
        if (held.Count > 0) owners = [.. owners.Where(o => !held.Contains(o))];
        var skip = owners.ToDictionary(o => o, o => (IReadOnlyCollection<string>)served.Where(t => OwnerOf(t.Ref) == o && !RoutingCheck.Serves(t.Row.RequiredLabels, mine)).Select(t => t.Ref.Task).ToList(), StringComparer.Ordinal);
        var claimed = 0;
        var progress = true;
        while (progress && _running.Count < _options.MaxConcurrentRuns && !_stop.IsCancellationRequested)
        {
            progress = false;
            foreach (var owner in owners)
            {
                if (_running.Count >= _options.MaxConcurrentRuns) break;
                if (_queue.TryClaimNext(owner, _options.WorkerId, excludeSubjects: skip[owner]) is not { } item) continue;
                Start(item);
                claimed++;
                progress = true;
            }
        }

        return claimed;
    }

    private void Start(WorkItem item)
    {
        var running = new Running();
        // this loop's own lease ran out and its own tick took the item back while the earlier attempt still runs here: the earlier attempt is a lost one, whoever holds the item now
        if (_running.TryGetValue(item.Id, out var earlier))
        {
            earlier.LeaseLost = true;
            earlier.Cancel?.Invoke();
            Report($"item {item.Id}", $"This runner's lease on {item.Owner}/{item.Subject} ran out and the item was taken again (as a new attempt) while the earlier attempt still ran here; the earlier attempt is cancelled and its result will not be recorded.");
        }

        _running[item.Id] = running;
        _ = Task.Run(async () =>
        {
            try { await RunItemAsync(item, running).ConfigureAwait(false); }
            catch (Exception ex)
            {
                // nothing may leave a claimed item to its lease: it would be run again for a failure that is not the task's
                Report($"item {item.Id}", $"The scheduler failed while running {item.Owner}/{item.Subject}: {ex.GetType().Name}: {ex.Message}");
                try { _queue.TryFinish(item.Id, _options.WorkerId, WorkItemStatus.Failed); }
                catch (Exception inner) { _log?.LogError(inner, "Could not finish item {Id} after a failure.", item.Id); }
            }
            finally
            {
                _running.TryRemove(new KeyValuePair<long, Running>(item.Id, running));
                running.Done.TrySetResult();
            }
        }, CancellationToken.None);
    }

    private sealed class Running
    {
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile bool LeaseLost;
        public volatile Action? Cancel;
        public volatile bool CancelRequested;
    }

    // ---- one item

    private async Task RunItemAsync(WorkItem item, Running running)
    {
        var worker = _options.WorkerId;
        var payload = WorkPayload.TryParse(item.PayloadJson, out var unreadable);
        if (payload is null)
        {
            Drop(item, unreadable!);
            return;
        }

        var taskRef = TaskRef.Parse(payload.Task);
        // claimed just as an activation took hold of the package: the item goes back and waits, so the run starts on the version the activation leaves
        if (_holds.IsHeld(DefinitionVersionSource.Kind, taskRef.Package, taskRef.Channel))
        {
            if (_queue.Reschedule(item.Id, worker, _time.GetUtcNow() + _options.Tick, payload.ToJson()) is null) running.LeaseLost = true;
            return;
        }

        var row = _projections.Get(taskRef.Package, taskRef.Channel, taskRef.Task);
        if (row is null)
        {
            Drop(item, $"{taskRef} is no longer in the projection.");
            return;
        }

        if (!ParseOrProblem(row, out _, out var rowProblem))
        {
            Drop(item, $"{taskRef}'s projection row cannot be read: {rowProblem}");
            return;
        }

        // an attempt that never started follows the active version; one that started keeps its own (DR8)
        var version = payload.Started ? payload.Version : row.Version;

        // an earlier run left a record under this item's run id: a loop died (or stopped) on it. This run gets a record of its own, one attempt later, on the same chain.
        var earlierRunId = item.RunId;
        if (_runs.Get(item.RunId) is { } earlier)
        {
            if (earlier.Run.Outcome == RunOutcome.Running)
                _runs.Finish(item.RunId, RunOutcome.Interrupted, failure: new RunFailure("Interrupted", "The loop that was running this attempt stopped renewing its lease; another loop took the item."));
            payload = payload with { Attempt = payload.Attempt + 1 };
            if (_queue.Restart(item.Id, worker, payload.ToJson()) is not { } fresh)
            {
                running.LeaseLost = true;
                return;
            }

            item = item with { RunId = fresh };
        }

        PackageVersionContent? content;
        try
        {
            content = await _versions.GetAsync(taskRef.Package, version, taskRef.Channel, _stop.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FailWithoutRun(item, payload, taskRef, version, $"The version {version} of '{taskRef.Package}' could not be read: {ex.Message}", IsTemporary(ex), null);
            return;
        }

        if (content is null)
        {
            FailWithoutRun(item, payload, taskRef, version, $"The version {version} of '{taskRef.Package}' is not available on channel '{taskRef.Channel}'.", temporary: false, null);
            return;
        }

        var task = content.Manifest.Tasks.FirstOrDefault(t => t.Name == taskRef.Task);
        if (task is null)
        {
            FailWithoutRun(item, payload, taskRef, version, $"The version {version} of '{taskRef.Package}' has no task '{taskRef.Task}'.", temporary: false, null);
            return;
        }

        var owner = OwnerOf(taskRef);
        var lockTaken = false;
        if (!task.AllowOverlap)
        {
            // the queue already keeps a task from running twice; the lock keeps it from running while something outside the queue holds its name. A holder that is this item's own earlier run is a dead loop's.
            var holder = _locks.Holder(owner, Kind, taskRef.Task);
            if (holder is { } other && other != item.RunId && other != earlierRunId)
            {
                Report(taskRef.ToString(), $"The run lock of {taskRef} is held by run {other}; the item waits.");
                if (_queue.Reschedule(item.Id, worker, _time.GetUtcNow() + _options.Tick, payload.ToJson()) is null) running.LeaseLost = true;
                return;
            }

            if (holder is not null) _locks.Release(owner, Kind, taskRef.Task);
            lockTaken = _locks.TryAcquire(owner, Kind, taskRef.Task, item.RunId);
            if (!lockTaken)
            {
                Report(taskRef.ToString(), $"The run lock of {taskRef} was taken by another start; the item waits.");
                if (_queue.Reschedule(item.Id, worker, _time.GetUtcNow() + _options.Tick, payload.ToJson()) is null) running.LeaseLost = true;
                return;
            }
        }

        using var runCts = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        running.Cancel = () => { try { runCts.Cancel(); } catch (ObjectDisposedException) { /* the run ended */ } };
        using var beatCts = new CancellationTokenSource();
        Task? beat = null;
        try
        {
            if (!_queue.TryMarkRunning(item.Id, worker))
            {
                running.LeaseLost = true;
                return;
            }

            _jobs.SaveStarted(owner, taskRef.Task, _time.GetUtcNow());
            beat = Task.Run(() => HeartbeatAsync(item, running, runCts, beatCts.Token), CancellationToken.None);

            MaterializedVersion materialized;
            try
            {
                materialized = await _cache.MaterializeAsync($"{taskRef.Package}@{version}", content.Files, runCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await StopBeating(beat, beatCts).ConfigureAwait(false);
                beat = null;
                FailWithoutRun(item, payload, taskRef, version, $"The version {version} of '{taskRef.Package}' could not be materialized: {ex.Message}", IsTemporary(ex), task);
                return;
            }
            catch (OperationCanceledException)
            {
                await StopBeating(beat, beatCts).ConfigureAwait(false);
                beat = null;
                Finish(item, running, TaskEndState.Stopped, stoppedByLoop: _stop.IsCancellationRequested && !running.CancelRequested);
                return;
            }

            using (materialized)
            {
                if (!payload.Started)
                {
                    payload = payload with { Version = version, Started = true };
                    if (!_queue.TrySetPayload(item.Id, worker, payload.ToJson()))
                    {
                        running.LeaseLost = true;
                        return;
                    }
                }

                var request = new TaskRunRequest
                {
                    Task = taskRef,
                    PackageDirectory = materialized.Directory,
                    PackageVersion = version,
                    Launch = content.Manifest.Launch,
                    Entry = task.Entry,
                    RunId = item.RunId,
                    Chain = payload.Chain,
                    Attempt = payload.Attempt,
                    Inputs = WithDefaults(task.Inputs, payload.Inputs),
                    Limits = task.Limits,
                    CancellationGrace = _options.CancellationGrace,
                };

                TaskEnd end;
                try
                {
                    end = (await _runner.RunAsync(request, runCts.Token).ConfigureAwait(false)).End;
                }
                catch (OperationCanceledException) when (runCts.IsCancellationRequested)
                {
                    end = TaskEndMapping.Cancelled("The run was cancelled.");
                }
                catch (Exception ex)
                {
                    await StopBeating(beat, beatCts).ConfigureAwait(false);
                    beat = null;
                    Report(taskRef.ToString(), $"The executor failed on run {item.RunId}: {ex.GetType().Name}: {ex.Message}");
                    _queue.TryFinish(item.Id, worker, WorkItemStatus.Failed);
                    return;
                }

                await StopBeating(beat, beatCts).ConfigureAwait(false);
                beat = null;
                _jobs.SaveExit(owner, taskRef.Task, ExitOf(end.State));

                if (running.LeaseLost)
                {
                    Report(taskRef.ToString(), $"The lease on run {item.RunId} was lost while it ran; its result is not recorded on the item, which another loop has.");
                    return;
                }

                if (end.State == TaskEndState.Stopped && _stop.IsCancellationRequested && !running.CancelRequested)
                {
                    _queue.TryRelease(item.Id, worker);   // a graceful stop: the work is unfinished, not cancelled
                    return;
                }

                Conclude(item, payload, end, version, RetryPolicy.From(task.Limits, _options.RetryDefaults), started: true);
            }
        }
        finally
        {
            if (beat is not null) await StopBeating(beat, beatCts).ConfigureAwait(false);
            if (lockTaken && _locks.Holder(owner, Kind, taskRef.Task) == item.RunId) _locks.Release(owner, Kind, taskRef.Task);
        }
    }

    /// <summary>The inputs a run is given, with the manifest's default for each declared input the run (a schedule, a trigger) gave no value for.</summary>
    public static IReadOnlyDictionary<string, string> WithDefaults(IReadOnlyList<TaskInput> declared, IReadOnlyDictionary<string, string> given)
    {
        if (declared.All(i => i.Default is null || given.ContainsKey(i.Name))) return given;
        var all = new Dictionary<string, string>(given, StringComparer.Ordinal);
        foreach (var input in declared)
            if (input.Default is { } value) all.TryAdd(input.Name, value);
        return all;
    }

    private void Finish(WorkItem item, Running running, TaskEndState state, bool stoppedByLoop)
    {
        if (stoppedByLoop) _queue.TryRelease(item.Id, _options.WorkerId);
        else if (!running.LeaseLost) _queue.TryFinish(item.Id, _options.WorkerId, TaskEndMapping.WorkItemStatusOf(state));
    }

    /// <summary>After a run that started: plans a retry (R08) and re-enqueues the item with a not-before time, or finishes it with the status the end maps to.</summary>
    private void Conclude(WorkItem item, WorkPayload payload, TaskEnd end, string version, RetryPolicy policy, bool started)
    {
        var worker = _options.WorkerId;
        var plan = RetryPlanner.Plan(end, payload.Attempt, policy, _time, _options.Jitter);
        if (plan is null)
        {
            _queue.TryFinish(item.Id, worker, TaskEndMapping.WorkItemStatusOf(end.State));
            return;
        }

        var next = WorkItemRetries.NextPayload(payload, plan, version) with { Started = started };
        if (_retries.Reschedule(item, worker, plan, next) is null)
            Report($"item {item.Id}", $"The retry of {payload.Task} (attempt {plan.NextAttempt}) was not queued: the lease on the item was lost.");
        else
            _log?.LogInformation("{Task} failed ({Failure}); attempt {Attempt} is queued for {At:u}.", payload.Task, plan.Failure.Message, plan.NextAttempt, plan.At);
    }

    /// <summary>An attempt that could not start (the version is missing or unreadable, its files do not verify): a failed run record of its own, so history is honest, and a retry when the failure may pass.</summary>
    private void FailWithoutRun(WorkItem item, WorkPayload payload, TaskRef taskRef, string version, string message, bool temporary, PackageTask? manifestTask)
    {
        var failure = new TaskFailure(temporary ? TaskFailureKind.Temporary : TaskFailureKind.Permanent, message);
        var end = new TaskEnd(TaskEndState.Failed, Failure: failure);
        _runs.Start(taskRef.ToString(), item.RunId, package: taskRef.Package, packageVersion: version, channel: taskRef.Channel, attempt: payload.Attempt);
        _runs.Log(item.RunId, "Error", message);
        _runs.Finish(item.RunId, RunOutcome.Failed, failure: failure.ToRunFailure());
        Report(taskRef.ToString(), message);

        var defaults = _options.RetryDefaults;
        var policy = manifestTask is not null ? RetryPolicy.From(manifestTask.Limits, defaults)
            : temporary ? new RetryPolicy(_options.StartFailureAttempts, defaults.Backoff, defaults.MaxBackoff, defaults.Jitter)
            : RetryPolicy.None;
        Conclude(item, payload, end, version, policy, started: false);
    }

    private static bool IsTemporary(Exception ex) => ex is IOException or HttpRequestException or TimeoutException;

    private static int ExitOf(TaskEndState state) => state switch
    {
        TaskEndState.Succeeded => 0,
        TaskEndState.CompletedWithFailures => 1,
        TaskEndState.Failed => 2,
        _ => 3,
    };

    /// <summary>Drops an item that can no longer run (its task left the projection, its payload is unreadable): cancelled, and reported.</summary>
    private void Drop(WorkItem item, string reason)
    {
        Report($"item {item.Id}", $"{item.Owner}/{item.Subject}: {reason} The item is cancelled.");
        _queue.TryFinish(item.Id, _options.WorkerId, WorkItemStatus.Cancelled);
    }

    private async Task HeartbeatAsync(WorkItem item, Running running, CancellationTokenSource runCts, CancellationToken stop)
    {
        try
        {
            using var timer = new PeriodicTimer(_options.Heartbeat, _time);
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                if (SuspendHeartbeats) continue;
                try
                {
                    if (!_queue.Heartbeat(item.Id, _options.WorkerId))
                    {
                        running.LeaseLost = true;
                        Report($"item {item.Id}", $"This runner lost the lease on {item.Owner}/{item.Subject} (run {item.RunId}): it did not renew it in time and another runner took the item. The local task is cancelled and its result will not be recorded.");
                        await runCts.CancelAsync().ConfigureAwait(false);   // the item is someone else's now
                        return;
                    }

                    if (!running.CancelRequested && _queue.IsCancelRequested(item.RunId))
                    {
                        running.CancelRequested = true;
                        await runCts.CancelAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // a database that is briefly out of reach must not end the renewals for good: the next beat tries again
                    Report($"item {item.Id}", $"Renewing the lease failed: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the run ended
        }
    }

    private static async Task StopBeating(Task beat, CancellationTokenSource beatCts)
    {
        await beatCts.CancelAsync().ConfigureAwait(false);
        await beat.ConfigureAwait(false);
    }

    // ---- by hand

    /// <summary>
    /// A manual start (R13's "by hand"): queues one item of the task with <paramref name="inputs"/>, apart from its schedule and also when it is paused (a person asked). Returns the run id, or null when a run of it is already queued or
    /// running (and it does not allow overlap). Throws when the task is not in the projection of a channel this loop serves, or its row is broken.
    /// </summary>
    public Guid? RunNow(TaskRef task, IReadOnlyDictionary<string, string>? inputs = null, string? requestedBy = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        var served = Find(task);
        return TryEnqueue(served, WorkPayload.ManualTrigger, inputs, requestedBy);
    }

    /// <summary>Stops the task from being queued at its scheduled times until it is resumed. A run already going is not touched.</summary>
    public void Pause(TaskRef task, string by)
    {
        Find(task);
        _pauses.Set(OwnerOf(task), task.Task, by, _time.GetUtcNow());
    }

    public void Resume(TaskRef task)
    {
        Find(task);
        _pauses.Clear(OwnerOf(task), task.Task);
    }

    private Served Find(TaskRef task)
    {
        if (!_options.Channels.Contains(task.Channel)) throw new InvalidOperationException($"This scheduler serves the channels {string.Join(", ", _options.Channels)}, not '{task.Channel}'.");
        var row = _projections.Get(task.Package, task.Channel, task.Task) ?? throw new InvalidOperationException($"{task} is not in the projection.");
        if (_options.Packages is not null && !_options.Packages.Contains(row.Package)) throw new InvalidOperationException($"This scheduler does not serve the package '{row.Package}'.");
        return ParseOrProblem(row, out var served, out var problem) ? served! : throw new InvalidOperationException($"{task}'s projection row cannot be read: {problem}");
    }

    // ---- status

    /// <summary>A snapshot: for each task of the served channels its next due time, last run, what is running or waiting, whether it is paused and why its row is skipped; and what the loop reported.</summary>
    public SchedulerStatus Status()
    {
        var now = _time.GetUtcNow();
        var tasks = new List<ScheduledTaskStatus>();
        IReadOnlyList<LiveInstance>? live = null;
        var queues = new Dictionary<string, IReadOnlyList<QueuedWork>>(StringComparer.Ordinal);
        foreach (var row in Rows())
        {
            var owner = $"{row.Package}/{row.Channel}";
            if (!queues.TryGetValue(owner, out var inFlight)) queues[owner] = inFlight = _queue.ListInFlight(owner);
            var mine = inFlight.Where(w => w.Item.Subject == row.Task).ToList();
            var reference = TaskRef.Problem(row.Package, row.Channel, row.Task) is null ? new TaskRef(row.Package, row.Channel, row.Task) : null;
            if (reference is null) continue;

            var parsed = ParseOrProblem(row, out var served, out var problem);
            var state = _jobs.Get(owner, row.Task);
            DateTimeOffset? next = null;
            if (parsed && served!.Schedule.HasSchedule)
                next = state?.HandledUpTo is null ? now : ScheduleEvaluator.Evaluate(served.Schedule, state.HandledUpTo, now).NextDue;
            var pause = _pauses.Get(owner, row.Task);
            var waiting = mine.Where(w => w.Item.Status == WorkItemStatus.Pending).ToList();
            tasks.Add(new ScheduledTaskStatus(
                reference, row.Version, next, _runs.LastRun(reference.ToString()),
                mine.Count(w => w.Item.Status is WorkItemStatus.Claimed or WorkItemStatus.Running), waiting.Count,
                waiting.Count == 0 ? null : waiting.Min(w => w.AvailableAt), pause is not null, pause?.By, parsed ? null : problem,
                row.RequiredLabels, WaitingReasonOf(row.RequiredLabels, ref live)));
        }

        var problems = _rowProblems.Values.Concat(_problems).Concat(_triggers.Problems()).OrderBy(p => p.At).ToList();
        return new SchedulerStatus(_options.WorkerId, now, tasks, problems, _running.Count, _options.MaxConcurrentRuns);
    }

    /// <summary>Which required label no live runner has, as a sentence; null when the task requires none or some live runner has them all.</summary>
    private string? WaitingReasonOf(IReadOnlyList<string> requires, ref IReadOnlyList<LiveInstance>? live)
    {
        if (requires.Count == 0) return null;
        live ??= _instances.LiveInstances(_options.Lease);
        var missing = RoutingCheck.CanActivate(requires, [.. live]);
        return missing.Count == 0 ? null : RoutingCheck.Reason(missing, live.Count);
    }

    private void Report(string subject, string message)
    {
        _problems.Enqueue(new SchedulerProblem(subject, message, _time.GetUtcNow()));
        while (_problems.Count > 50) _problems.TryDequeue(out _);
        _log?.LogWarning("{Subject}: {Message}", subject, message);
    }
}
