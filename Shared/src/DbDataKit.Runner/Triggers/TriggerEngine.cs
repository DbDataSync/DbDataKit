using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Triggers;

/// <summary>
/// The triggers of a runner (plan 28a R13, decision DR9: <b>a trigger writes a work item, it never runs a task</b>). Once per scheduler tick, and for a webhook when it arrives, the engine turns what happened into items on the
/// queue the schedule uses, through the scheduler itself (<see cref="ITriggerHost.Enqueue"/>), so the lease, the resource limits, the run lock and the retry policy apply to every start.
/// <list type="bullet">
/// <item><b>After a run.</b> A task whose manifest says <c>after: {task: extract, on: [failed]}</c> is queued once for each finished run of <c>extract</c> whose outcome is in <c>on</c> (succeeded when none is written; <c>any</c> for every
/// one). The engine reads the finished run records and remembers where it got to in <c>trigger_state</c> as a cursor (the end time and id of the last run it dealt with). <b>Exactly once, also with two runners:</b> the cursor moves in the
/// same transaction that inserts the work item, and only if it still is where the runner read it, so of two runners that see one run, one inserts and the other finds the cursor gone and does nothing; the item's part is the run's id as
/// well, so a duplicate would be refused by the queue too. A failed run that the scheduler will retry is not yet an end: the trigger waits for the last attempt. The chain depth of the finished run (stored in its item) plus one is the new
/// item's; past <see cref="TriggerOptions.MaxChainDepth"/> nothing is queued and the status and the log say so. A trigger follows a task of its own package or, with <c>package</c>, of another package on the same channel.</item>
/// <item><b>Webhook.</b> A body the serving layer or the runner's endpoint stored as an inbox item (<see cref="WebhookReceiver"/>) makes the task due once; the task takes the items with <c>Inbox&lt;T&gt;().TakeAsync</c>, which hands each
/// to exactly one run. The engine also looks at the inbox every tick, so an item that arrived while the task was running or paused is not lost.</item>
/// <item><b>File arrival.</b> Every <c>poll</c> the engine lists the folder of the trigger's connection slot (<see cref="IFileSource"/>), hashes each new-looking file with a bounded buffer and asks the ledger: one run for each content
/// the ledger has not seen; the ledger note and the item are one transaction. A file that arrives twice runs once, a changed file (another hash) runs again. Marking the file processed stays the task's (<c>Files.Claim</c>).</item>
/// <item><b>Store change</b> is declared in the model and has no source yet: the trigger is reported as <c>waiting for a change source (not built)</c> and never fires. <b>By hand</b> is <see cref="RunnerScheduler.RunNow"/>.</item>
/// </list>
/// A paused task is not started by a trigger: its after-run events are dropped (as a schedule's times are), its webhooks and files wait for it.
/// </summary>
public sealed class TriggerEngine
{
    public const string AfterRunTrigger = "after_run";
    public const string WebhookTrigger = "webhook";
    public const string FileTrigger = "file";

    private const string WebhookStateId = "webhook";

    private readonly StateDatabase _database;
    private readonly ITriggerHost _host;
    private readonly TriggerOptions _options;
    private readonly ILogger? _log;
    private readonly TimeProvider _time;
    private readonly TriggerStates _states;
    private readonly RunStore _runs;
    private readonly ModuleWorkQueueStore _queue;
    private readonly string _service;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, byte> _seenFiles = new(StringComparer.Ordinal);
    private IReadOnlyList<SchedulerProblem> _problems = [];
    private IReadOnlyList<TriggerStatus> _statuses = [];

    public TriggerEngine(StateDatabase database, string service, ITriggerHost host, TriggerOptions? options = null, ILogger? log = null)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _options = options ?? new TriggerOptions();
        _options.Validate();
        _log = log;
        _time = database.Time;
        _states = new TriggerStates(database);
        _service = service;
        _runs = new RunStore(database, service);
        _queue = new ModuleWorkQueueStore(database, service);
    }

    /// <summary>What the engine reports as wrong or not built, as of its last pass (a trigger loop, a store-change trigger, a file trigger without a folder, a trigger text that does not read).</summary>
    public IReadOnlyList<SchedulerProblem> Problems() => _problems;

    /// <summary>Every trigger of the served tasks as of the last pass, with what it is doing.</summary>
    public IReadOnlyList<TriggerStatus> Statuses() => _statuses;

    /// <summary>What the last pass did.</summary>
    public TriggerTickReport? LastTick { get; private set; }

    // ---- one pass

    /// <summary>One pass over the triggers of the served tasks: queues what the events call for. A trigger that fails is reported and does not stop the others.</summary>
    public async Task<TriggerTickReport> TickAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var rows = _host.ServedRows();
            var pass = new Pass(now);

            foreach (var channel in rows.Select(r => r.Channel).Distinct(StringComparer.Ordinal))
                if (ProjectionDocuments.FindCycle(rows, "", channel, null) is { } cycle)
                    pass.Problems.Add(new SchedulerProblem($"channel {channel}", $"The triggers form a loop: {cycle}. The chain depth guard ({_options.MaxChainDepth}) stops it from running for ever; publish a package without the loop.", now));

            foreach (var row in rows)
            {
                ct.ThrowIfCancellationRequested();
                var task = new TaskRef(row.Package, row.Channel, row.Task);
                IReadOnlyList<TaskTrigger> triggers;
                try { triggers = ProjectionDocuments.ParseTriggers(row.Triggers); }
                catch (FormatException ex)
                {
                    pass.Problem(task, $"The triggers of this task cannot be read: {ex.Message}");
                    continue;
                }

                var webhookSeen = false;
                foreach (var trigger in triggers)
                {
                    try
                    {
                        switch (trigger.Kind)
                        {
                            case TriggerKind.AfterRun:
                                AfterRun(pass, rows, task, trigger);
                                break;
                            case TriggerKind.Webhook:
                                if (webhookSeen) break;      // one inbox per task, whatever sources it lists
                                webhookSeen = true;
                                Webhook(pass, task);
                                break;
                            case TriggerKind.File:
                                await WatchFiles(pass, task, trigger, ct).ConfigureAwait(false);
                                break;
                            case TriggerKind.StoreChange:
                                pass.Status(task, "store_change", $"store:{trigger.Model}{(trigger.Entity is null ? "" : ":" + trigger.Entity)}", NotBuilt, null);
                                pass.Problem(task, $"The store_change trigger (model '{trigger.Model}'{(trigger.Entity is null ? "" : $", entity '{trigger.Entity}'")}): {NotBuilt}.");
                                break;
                            default:
                                pass.Status(task, "manual", "manual", "by hand", null);
                                break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        pass.Problem(task, $"The {KindWord(trigger.Kind)} trigger failed: {ex.GetType().Name}: {ex.Message}");
                        _log?.LogWarning(ex, "The {Kind} trigger of {Task} failed.", KindWord(trigger.Kind), task);
                    }
                }
            }

            _problems = pass.Problems;
            _statuses = pass.Statuses;
            return LastTick = new TriggerTickReport(pass.Queued, pass.Skipped);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>What a store-change trigger says it is doing (it has no source, so it never fires).</summary>
    public const string NotBuilt = "waiting for a change source (not built)";

    private static string KindWord(TriggerKind kind) => kind switch
    {
        TriggerKind.AfterRun => "after",
        TriggerKind.Webhook => "webhook",
        TriggerKind.File => "file",
        TriggerKind.StoreChange => "store_change",
        _ => "manual",
    };

    private sealed class Pass(DateTimeOffset now)
    {
        public DateTimeOffset Now { get; } = now;
        public List<SchedulerProblem> Problems { get; } = [];
        public List<TriggerStatus> Statuses { get; } = [];
        public List<TaskRef> Queued { get; } = [];
        public int Skipped;

        public void Problem(TaskRef task, string message) => Problems.Add(new SchedulerProblem(task.ToString(), message, Now));

        public void Status(TaskRef task, string kind, string id, string state, DateTimeOffset? lastFired, string? problem = null) =>
            Statuses.Add(new TriggerStatus(task, kind, id, state, lastFired, problem));
    }

    // ---- after a run

    /// <summary>The cursor of an after-run trigger: the end time (ticks) and id of the last finished run the trigger dealt with. Text as <c>ticks|id</c>.</summary>
    private readonly record struct AfterCursor(long Ticks, string Id)
    {
        public override string ToString() => Ticks.ToString(CultureInfo.InvariantCulture) + "|" + Id;

        public static AfterCursor Of(StoredRun run) => new(run.Run.Finished!.Value.UtcTicks, run.Id.ToString("N"));

        public static bool TryParse(string text, out AfterCursor cursor)
        {
            cursor = default;
            var bar = text.IndexOf('|', StringComparison.Ordinal);
            if (bar < 0 || !long.TryParse(text.AsSpan(0, bar), NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)) return false;
            cursor = new AfterCursor(ticks, text[(bar + 1)..]);
            return true;
        }

        public bool IsLaterRun(StoredRun run) => run.Run.Finished!.Value.UtcTicks > Ticks || (run.Run.Finished.Value.UtcTicks == Ticks && string.CompareOrdinal(run.Id.ToString("N"), Id) > 0);
    }

    private void AfterRun(Pass pass, IReadOnlyList<TaskProjection> rows, TaskRef task, TaskTrigger trigger)
    {
        var source = new TaskRef(trigger.Package ?? task.Package, task.Channel, trigger.Task!);
        var id = $"after:{source.Package}/{source.Task}";
        var state = _states.Get(task.Package, task.Channel, task.Task, id);
        if (!rows.Any(r => r.Package == source.Package && r.Channel == source.Channel && r.Task == source.Task))
            pass.Problem(task, $"The after trigger follows {source.Package}/{source.Task}, which is not in the projection of channel '{task.Channel}' on this runner; it fires when that task's runs are recorded.");

        var cursorText = state?.Cursor;
        if (cursorText is null)
        {
            // first sight of the trigger: it follows runs that end from now on, not the history
            var initial = new AfterCursor(pass.Now.UtcTicks, "").ToString();
            if (!_states.TryAdvance(task.Package, task.Channel, task.Task, id, null, initial, null))
            {
                pass.Status(task, "after", id, $"following {source.Package}/{source.Task}", state?.LastFiredAt);
                return;     // another runner set it first; the next pass reads it
            }

            cursorText = initial;
        }

        if (!AfterCursor.TryParse(cursorText, out var cursor))
        {
            pass.Problem(task, $"The cursor of the trigger '{id}' is not readable ('{cursorText}'); it is reset to now.");
            _states.Set(task.Package, task.Channel, task.Task, id, state?.LastFiredAt, new AfterCursor(pass.Now.UtcTicks, "").ToString());
            return;
        }

        var lastFired = state?.LastFiredAt;
        var held = false;
        foreach (var run in _runs.FinishedSince(source.ToString(), new DateTimeOffset(cursor.Ticks, TimeSpan.Zero)).Where(cursor.IsLaterRun))
        {
            var expected = cursorText;
            var next = AfterCursor.Of(run).ToString();

            if (!trigger.Outcomes.Contains(run.Run.Outcome))
            {
                if (!_states.TryAdvance(task.Package, task.Channel, task.Task, id, expected, next, lastFired)) { held = true; break; }
                cursorText = next;
                continue;
            }

            var end = EndOf(source, run);
            if (end == RunEnd.Hold) { held = true; break; }     // a retry of it is coming: the trigger waits for the last attempt
            if (end == RunEnd.Superseded || _host.IsPaused(task))
            {
                // a run that a retry replaced is not an end; a paused task is not started (a schedule's times are dropped the same way)
                if (!_states.TryAdvance(task.Package, task.Channel, task.Task, id, expected, next, lastFired)) { held = true; break; }
                cursorText = next;
                if (end != RunEnd.Superseded)
                {
                    pass.Skipped++;
                    _log?.LogInformation("{Task} is paused: the end of run {Run} of {Source} does not start it.", task, run.Id, source);
                }

                continue;
            }

            var depth = DepthOf(run) + 1;
            if (depth > _options.MaxChainDepth)
            {
                if (!_states.TryAdvance(task.Package, task.Channel, task.Task, id, expected, next, lastFired)) { held = true; break; }
                cursorText = next;
                pass.Skipped++;
                _host.Report(task.ToString(), $"The end of run {run.Id:D} of {source} would start {task} at depth {depth} of a chain of triggered runs, deeper than the limit of {_options.MaxChainDepth}: not started. Is there a trigger loop?");
                continue;
            }

            var start = new TriggerStart(AfterRunTrigger, "after:" + run.Id.ToString("N"), depth, $"run {run.Id:D} of {source} ({OutcomeWord(run.Run.Outcome)})",
                AlsoWrite: (connection, transaction, _) =>
                {
                    if (!_states.TryAdvance(connection, transaction, task.Package, task.Channel, task.Task, id, expected, next, pass.Now)) throw new TriggerClaimLostException();
                });
            TriggerEnqueueResult result;
            try { result = _host.Enqueue(task, start); }
            catch (TriggerClaimLostException) { held = true; break; }

            if (result.Outcome == TriggerEnqueueOutcome.Queued)
            {
                cursorText = next;
                lastFired = pass.Now;
                pass.Queued.Add(task);
                continue;
            }

            if (result.Outcome == TriggerEnqueueOutcome.Unavailable) pass.Problem(task, result.Problem ?? "The task cannot be started.");
            held = true;    // a collision: another runner is queueing this run's item; the next pass finds the cursor moved
            break;
        }

        pass.Status(task, "after", id, held ? $"following {source.Package}/{source.Task} (waiting)" : $"following {source.Package}/{source.Task}", lastFired);
    }

    private static string OutcomeWord(RunOutcome outcome) => outcome switch
    {
        RunOutcome.CompletedWithFailures => "completed_with_failures",
        _ => outcome.ToString().ToLowerInvariant(),
    };

    private enum RunEnd
    {
        /// <summary>The last attempt of its item: what a trigger follows.</summary>
        End,
        /// <summary>Not decided yet: the item is still held, or the scheduler has queued a retry that has not started.</summary>
        Hold,
        /// <summary>A retry ran after it: the retry is the end, not this attempt.</summary>
        Superseded,
    }

    /// <summary>
    /// Whether a finished run is the end of its work item. A run that succeeded is (nothing retries it) once its item is concluded. Otherwise: a later run of the task with the next attempt number that started after this one ended is
    /// its retry, so this one is superseded; the item still held (the run's record is finished a moment before the scheduler concludes the item) or an item of the task whose payload is at a later attempt (a retry waiting for its
    /// back-off; it replaced the item's run id, so the earlier attempt's id finds no item) is a hold.
    /// </summary>
    private RunEnd EndOf(TaskRef task, StoredRun run)
    {
        static bool Open(WorkItem item) => item.Status is WorkItemStatus.Pending or WorkItemStatus.Claimed or WorkItemStatus.Running;
        if (run.Run.Outcome is RunOutcome.Succeeded or RunOutcome.CompletedWithFailures) return _queue.FindByRun(run.Id) is { } done && Open(done) ? RunEnd.Hold : RunEnd.End;
        var attempt = run.Attempt ?? 1;
        if (_runs.Read(new RunQuery(_service, task.ToString(), StartedSince: run.Run.Finished, Limit: 50)).Any(r => r.Id != run.Id && r.Attempt == attempt + 1)) return RunEnd.Superseded;
        if (_queue.FindByRun(run.Id) is { } item) return Open(item) ? RunEnd.Hold : RunEnd.End;
        foreach (var waiting in _queue.ListInFlight($"{task.Package}/{task.Channel}"))
            if (waiting.Item.Kind == RunnerScheduler.Kind && waiting.Item.Subject == task.Task && WorkPayload.TryParse(waiting.Item.PayloadJson, out _) is { } payload && payload.Attempt > attempt) return RunEnd.Hold;
        return RunEnd.End;
    }

    /// <summary>How deep in a chain of triggered runs the finished run was: the depth its item carried, 0 when it has none (a schedule's, a manual run's, one whose item retention removed).</summary>
    private int DepthOf(StoredRun run) =>
        _queue.FindByRun(run.Id) is { } item && WorkPayload.TryParse(item.PayloadJson, out _) is { } payload ? payload.TriggerDepth : 0;

    // ---- webhooks

    private void Webhook(Pass pass, TaskRef task)
    {
        var queued = SweepWebhooks(task, pass.Now);
        if (queued) pass.Queued.Add(task);
        var waiting = new ProgressStore(_database, new ProgressKey(task.Package, task.Channel, task.Task)).InboxWaiting();
        var state = _states.Get(task.Package, task.Channel, task.Task, WebhookStateId);
        pass.Status(task, "webhook", WebhookStateId, waiting == 0 ? "no webhook waiting" : $"{waiting} webhook(s) waiting for a run", state?.LastFiredAt);
    }

    /// <summary>
    /// Queues the task if a webhook is waiting in its inbox that no earlier look has queued it for. A task already queued or running is looked at again next time (the waiting item stays in the inbox until a run takes it).
    /// Returns whether an item was queued.
    /// </summary>
    private bool SweepWebhooks(TaskRef task, DateTimeOffset now)
    {
        var progress = new ProgressStore(_database, new ProgressKey(task.Package, task.Channel, task.Task));
        if (progress.NewestWaiting() is not { } newest) return false;
        var state = _states.Get(task.Package, task.Channel, task.Task, WebhookStateId);
        if (state?.Cursor is { } known && long.TryParse(known, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) && ticks >= newest.UtcTicks) return false;
        if (_host.IsPaused(task)) return false;     // the items wait for it

        var expected = state?.Cursor;
        var next = newest.UtcTicks.ToString(CultureInfo.InvariantCulture);
        var start = new TriggerStart(WebhookTrigger, null, 0, "webhook",
            AlsoWrite: (connection, transaction, _) =>
            {
                if (!_states.TryAdvance(connection, transaction, task.Package, task.Channel, task.Task, WebhookStateId, expected, next, now)) throw new TriggerClaimLostException();
            });
        try { return _host.Enqueue(task, start).Outcome == TriggerEnqueueOutcome.Queued; }
        catch (TriggerClaimLostException) { return false; }
    }

    /// <summary>
    /// A webhook was stored for <paramref name="task"/>: queues the task now instead of at the next tick. False when the task is not one this loop serves (a runner that serves the channel will see the item at its tick), or an item
    /// of it is queued or running already, or it is paused.
    /// </summary>
    public bool TriggerWebhook(TaskRef task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_host.ServedRows().Any(r => r.Package == task.Package && r.Channel == task.Channel && r.Task == task.Task)) return false;
        _gate.Wait();
        try { return SweepWebhooks(task, _time.GetUtcNow()); }
        finally { _gate.Release(); }
    }

    // ---- files

    private async Task WatchFiles(Pass pass, TaskRef task, TaskTrigger trigger, CancellationToken ct)
    {
        var connection = trigger.Connection!;
        var id = $"file:{connection}:{trigger.Path}";
        var state = _states.Get(task.Package, task.Channel, task.Task, id);
        var poll = trigger.Poll ?? _options.FilePoll;
        var describe = $"watching {connection} for {trigger.Path} every {ProjectionDocuments.DurationText(poll)}";
        if (state?.Cursor is { } last && long.TryParse(last, NumberStyles.None, CultureInfo.InvariantCulture, out var lastTicks) && pass.Now - new DateTimeOffset(lastTicks, TimeSpan.Zero) < poll)
        {
            pass.Status(task, "file", id, describe, state.LastFiredAt);
            return;
        }

        if (_host.IsPaused(task))
        {
            pass.Status(task, "file", id, describe + " (paused: files wait)", state?.LastFiredAt);
            return;
        }

        var source = _options.FileSources?.Open(task, connection);
        if (source is null)
        {
            pass.Status(task, "file", id, describe, state?.LastFiredAt, $"this runner has no folder for the connection slot '{connection}'");
            pass.Problem(task, $"The file trigger watches the connection slot '{connection}', and this runner has no folder for it (file connections are not built; the runner is told a local folder per slot).");
            return;
        }

        var path = trigger.Path!.Replace('\\', '/').Trim('/');
        if (path.Length == 0 || path.Split('/').Any(s => s == ".."))
        {
            pass.Problem(task, $"The file trigger's path '{trigger.Path}' has to stay inside the connection's folder.");
            return;
        }

        var slash = path.LastIndexOf('/');
        var directory = slash < 0 ? "" : path[..slash];
        var pattern = path[(slash + 1)..];

        var lastFired = state?.LastFiredAt;
        _states.Set(task.Package, task.Channel, task.Task, id, lastFired, pass.Now.UtcTicks.ToString(CultureInfo.InvariantCulture));    // the look is counted when it starts, so a slow folder is not looked at every tick
        var progress = new ProgressStore(_database, new ProgressKey(task.Package, task.Channel, task.Task));
        var queued = 0;
        await foreach (var file in source.ListAsync(directory, pattern, ct).ConfigureAwait(false))
        {
            if (queued >= _options.FileBatch) break;
            var age = pass.Now - file.ModifiedAt;
            if (age >= TimeSpan.Zero && age < _options.FileSettle) continue;      // maybe still being written

            var key = $"{task}|{connection}|{file.Path}|{file.Size}|{file.ModifiedAt.UtcTicks}";
            if (_seenFiles.ContainsKey(key)) continue;
            var hash = await HashAsync(source, file.Path, ct).ConfigureAwait(false);
            if (progress.FindFile(connection, hash) is not null)
            {
                Remember(key);
                continue;       // the task has seen this content: a file that arrives twice runs once
            }

            var start = new TriggerStart(FileTrigger, "file:" + hash, 0, $"file {file.Path}",
                AlsoWrite: (conn, transaction, _) =>
                {
                    if (!progress.NoteFile(conn, transaction, connection, file.Path, file.Size, file.ModifiedAt, hash)) throw new TriggerClaimLostException();
                });
            try
            {
                var result = _host.Enqueue(task, start);
                if (result.Outcome == TriggerEnqueueOutcome.Unavailable)
                {
                    pass.Problem(task, result.Problem ?? "The task cannot be started.");
                    break;
                }

                if (result.Outcome == TriggerEnqueueOutcome.Queued)
                {
                    queued++;
                    lastFired = pass.Now;
                    pass.Queued.Add(task);
                }
            }
            catch (TriggerClaimLostException)
            {
                // another runner noted the file first and queued the run
            }

            Remember(key);
        }

        if (queued > 0) _states.Set(task.Package, task.Channel, task.Task, id, lastFired, pass.Now.UtcTicks.ToString(CultureInfo.InvariantCulture));
        pass.Status(task, "file", id, describe, lastFired);
    }

    private void Remember(string key)
    {
        if (_seenFiles.Count > 10_000) _seenFiles.Clear();
        _seenFiles[key] = 0;
    }

    private static async Task<string> HashAsync(IFileSource source, string path, CancellationToken ct)
    {
        await using var stream = await source.OpenReadAsync(path, ct).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(80 * 1024);      // bounded memory: one buffer, whatever the file's size
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) > 0) hash.AppendData(buffer, 0, read);
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
