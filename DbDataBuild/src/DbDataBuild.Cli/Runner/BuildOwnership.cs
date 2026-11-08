using DbDataBuild.Cli.Server;
using DbDataKit.Runner.Client;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Cli.Runner;

/// <summary>A move of a plan's schedule that was refused or could not be finished; the message says what was found and what is as it was.</summary>
internal sealed class OwnershipException(string message) : InvalidOperationException(message);

/// <summary>What the Build server asks of the runner about a plan's task (plan 28c E09): the runner's own words for who schedules it, a change of that with the position handed over, and the decisions it recorded.</summary>
internal interface IRunnerSchedule
{
    Task<ClientTaskSchedule> ScheduleOfAsync(string package, string task, CancellationToken ct);

    Task SetOwnerAsync(string package, string task, string owner, string? reason, ClientSeed? seed, CancellationToken ct);

    Task<IReadOnlyList<ClientDecision>> DecisionsAsync(string package, string task, DateTimeOffset? since, CancellationToken ct);
}

/// <summary>The runner a link names, spoken to through the shared publishing client (a paired server client; the owner call needs the operator role of the pairing).</summary>
internal sealed class ClientRunnerSchedule(RunnerPublishClient client, ScheduleRunnerLink link, string? code = null) : IRunnerSchedule
{
    public Task<ClientTaskSchedule> ScheduleOfAsync(string package, string task, CancellationToken ct) => client.ScheduleOfAsync(link.Server, code, package, link.Channel, task, null, ct);

    public Task SetOwnerAsync(string package, string task, string owner, string? reason, ClientSeed? seed, CancellationToken ct) => client.SetOwnerAsync(link.Server, code, package, link.Channel, task, owner, reason, seed, null, ct);

    public Task<IReadOnlyList<ClientDecision>> DecisionsAsync(string package, string task, DateTimeOffset? since, CancellationToken ct) => client.DecisionsAsync(link.Server, code, package, link.Channel, task, since, null, ct);

    public Task ReportAsync(string package, string task, DateTimeOffset at, int missed, bool paused, CancellationToken ct) => client.ReportDecisionAsync(link.Server, code, package, link.Channel, task, at, missed, paused, null, ct);
}

/// <summary>Reports to the runner what this server's scheduler queued for an observed plan (the scheduler's tick is synchronous, so the call is too, and short: a runner that cannot be reached costs a line in the log and nothing else).</summary>
internal sealed class RunnerDecisionReporter(Func<ClientRunnerSchedule?> runner, Action<string> log) : IObservedDecisions
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public void Report(string project, ScheduledJob job, DateTimeOffset at, int missed, bool paused)
    {
        try
        {
            if (runner() is not { } target) return;
            using var limit = new CancellationTokenSource(Timeout);
            target.ReportAsync(RunnerNaming.PackageOf(project), RunnerNaming.TaskOf(job.Connection, job.Plan), at, missed, paused, limit.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            log($"Project '{project}', {job.Subject}: the decision at {at:u} could not be reported to the runner ({ex.GetType().Name}: {ex.Message}); the comparison will lack it.");
        }
    }
}

/// <summary>The names a project's plan has on the runner: the package is named by the project's id (publish it with <c>--package &lt;project id&gt;</c>), the task by <see cref="RunnerProject.TaskName"/>.</summary>
internal static class RunnerNaming
{
    public static string PackageOf(string projectId) => RunnerProject.NameOf(projectId);

    public static string TaskOf(string connection, string plan) => RunnerProject.TaskName(connection, plan);
}

/// <summary>What one move did, for the document and the audit.</summary>
internal sealed record OwnershipChange(string Project, string Connection, string Plan, string From, string To, string Package, string Task, DateTimeOffset? HandledUpTo, string? PausedBy, bool Waited);

/// <summary>One decision of an observed plan that has no partner on the other side.</summary>
internal sealed record DecisionDifference(string Kind, DateTimeOffset At, bool Paused);

/// <summary>The decisions of an observed plan compared: the runner's with the ones this server reported for what its own scheduler queued, each runner decision paired with an old one within the tolerance and the same pause.</summary>
internal sealed record OwnershipComparison(int Matched, IReadOnlyList<DecisionDifference> Differences)
{
    public bool Agrees => Differences.Count == 0;
}

/// <summary>One plan's owner as this server's file says it and as the runner says it.</summary>
internal sealed record OwnerRow(string Project, string Connection, string Plan, string Setting, string? Runner, string? RunnerSetBy, bool Mismatch);

/// <summary>
/// Moving the schedule of one refresh plan from this server's scheduler to a runner, and back (plan 28c E09, ED6 and ED7), with a recorded owner on both sides: <c>server.yaml</c> here, <c>task_owner</c> on the runner. Every step is ordered so that a failure
/// leaves a plan that nobody schedules, never one that both do:
/// <list type="bullet">
/// <item><b>observe</b>: the runner evaluates the plan's schedule against a copy of this server's position and records what it would have queued; this scheduler goes on and reports what it queued; nothing runs on the runner. <b>compare</b> pairs the two.</item>
/// <item><b>cutover</b>: the owner is written as <c>runner</c>, which makes this scheduler leave the plan alone (it queues nothing and moves nothing, so no scheduled time is skipped meanwhile); the run in flight, queued or going, is waited for; the position
/// (<c>HandledUpTo</c>) and the pause are handed to the runner with the owner, whose schedule is active from then on. A scheduled time that came while waiting is run once by the runner (the missed-time rule), not skipped and not run by both. If the
/// wait runs out or the runner refuses, the owner here is written back and nothing on the runner changed.</item>
/// <item><b>rollback</b>: the runner is told to let go (it stops looking at the schedule), its run in flight is waited for, and its position and pause are copied into this server's state before the owner here is written back as <c>old</c>.</item>
/// </list>
/// Both sides audit: the runner records <c>runner.task.owner</c> with the paired client as the actor, this server a line in <c>management-audit.log</c>.
/// </summary>
internal sealed class BuildOwnership(string serverDirectory, ProjectsFile projects, StateDatabase database, TimeProvider clock, IRunnerSchedule runner, IAuditLog audit, string actor, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public static readonly TimeSpan DefaultWait = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(90);

    private readonly ModuleJobStateStore _jobs = new(database, BuildSchedulerSchema.Service);
    private readonly ModuleJobPauseStore _pauses = new(database, BuildSchedulerSchema.Service);
    private readonly ModuleWorkQueueStore _queue = new(database, BuildSchedulerSchema.Service);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? ((span, ct) => Task.Delay(span, ct));

    private sealed record Resolved(HostedProject Project, ScheduledJob Job, string Package, string Task, string Owner);

    private Resolved Resolve(string projectId, string connection, string plan)
    {
        var project = projects.Find(projectId) ?? throw new OwnershipException($"This server hosts no project '{projectId}'.");
        var root = projects.RootOf(project);
        var job = Directory.Exists(root) ? RefreshJobs.Discover(root).FirstOrDefault(j => j.Connection == connection && j.Plan == plan) : null;
        if (job is null) throw new OwnershipException($"Project '{projectId}' has no refresh plan '{connection}/{plan}' that declares a schedule: only a scheduled plan has an owner.");
        return new Resolved(project, job, RunnerNaming.PackageOf(projectId), RunnerNaming.TaskOf(connection, plan), BuildScheduleSettings.Read(serverDirectory).OwnerOf(projectId, connection, plan));
    }

    /// <summary>The runner has to serve the task already: a package whose version is active in the channel (a published project is staged and activates once every runner of the group has it ready).</summary>
    private async Task<ClientTaskSchedule> RequireActiveAsync(Resolved plan, string channel, CancellationToken ct)
    {
        var state = await runner.ScheduleOfAsync(plan.Package, plan.Task, ct);
        if (state.Version is null)
            throw new OwnershipException($"The runner has no active version of the package '{plan.Package}' with the task '{plan.Task}' in channel '{channel}'. Publish the project (`project publish --to runner --package {plan.Package} --channel {channel}`) and wait until it is active; nothing was changed.");
        return state;
    }

    private (DateTimeOffset? Handled, string? PausedBy, DateTimeOffset? PausedAt) PositionOf(Resolved plan)
    {
        var pause = _pauses.Get(plan.Project.Id, plan.Job.Subject);
        return (_jobs.Get(plan.Project.Id, plan.Job.Subject)?.HandledUpTo, pause?.By, pause?.At);
    }

    private void Audit(string command, Resolved plan, string outcome) =>
        audit.Append(new AuditEntry(clock.GetUtcNow(), "command", "dbdatabuild server schedule", "local:" + actor, actor, false, command, BuildScheduleSettings.KeyOf(plan.Project.Id, plan.Job.Connection, plan.Job.Plan), outcome, ""));

    /// <summary>Starts observing the plan: the runner copies this server's position and pause, evaluates the schedule beside this scheduler and records decisions. The plan stays this scheduler's.</summary>
    public async Task<OwnershipChange> ObserveAsync(string projectId, string connection, string plan, ScheduleRunnerLink link, string? reason, CancellationToken ct = default)
    {
        var p = Resolve(projectId, connection, plan);
        if (p.Owner == TaskOwners.Runner) throw new OwnershipException($"{BuildScheduleSettings.KeyOf(projectId, connection, plan)} is scheduled by the runner; roll it back first.");
        await RequireActiveAsync(p, link.Channel, ct);
        var (handled, by, at) = PositionOf(p);
        // no position here is a first sight: the runner's copy starts from one too, as this scheduler would
        await runner.SetOwnerAsync(p.Package, p.Task, TaskOwners.Observe, reason, handled is { } h ? new ClientSeed(h, by, at) : null, ct);
        BuildScheduleSettings.Write(serverDirectory, projectId, connection, plan, TaskOwners.Observe, link);
        Audit("schedule.observe", p, "ok");
        return new OwnershipChange(projectId, connection, plan, p.Owner, TaskOwners.Observe, p.Package, p.Task, handled, by, false);
    }

    /// <summary>Hands the plan's schedule to the runner (see the class). <paramref name="wait"/> bounds the wait for the run in flight.</summary>
    public async Task<OwnershipChange> CutOverAsync(string projectId, string connection, string plan, ScheduleRunnerLink link, string? reason, TimeSpan? wait = null, CancellationToken ct = default)
    {
        var p = Resolve(projectId, connection, plan);
        var key = BuildScheduleSettings.KeyOf(projectId, connection, plan);
        if (p.Owner == TaskOwners.Runner) throw new OwnershipException($"{key} is scheduled by the runner already.");
        var active = await RequireActiveAsync(p, link.Channel, ct);
        if (active.Owner == TaskOwners.Runner && active.OwnerSetBy is not null) throw new OwnershipException($"The runner says it schedules {p.Package}/{p.Task} already (set by {active.OwnerSetBy}), while this server's file says {p.Owner}. Nothing was changed: find out which is right first (`server schedule owners`).");

        // 1. this scheduler lets go: it queues nothing and moves nothing for the plan from its next look
        BuildScheduleSettings.Write(serverDirectory, projectId, connection, plan, TaskOwners.Runner, link);
        var waited = false;
        var handedOver = false;
        DateTimeOffset? copied = null;
        string? copiedPause = null;
        try
        {
            // 2. the run in flight, queued or going, finishes where it started; the check is made twice, a poll apart, so a tick that read the old owner just before the write has queued by then
            var until = clock.GetUtcNow() + (wait ?? DefaultWait);
            for (var quiet = 0; quiet < 2;)
            {
                if (_queue.HasInFlight(p.Project.Id, BuildScheduler.Kind, p.Job.Subject))
                {
                    waited = true;
                    quiet = 0;
                    if (clock.GetUtcNow() >= until) throw new OwnershipException($"A run of {key} was still queued or going after {(wait ?? DefaultWait).TotalSeconds:0} seconds (is the server running its scheduler?). The plan is still scheduled here and nothing was changed on the runner.");
                }
                else quiet++;
                await _delay(PollEvery, ct);
            }

            // 3. the position and the pause go to the runner with the owner; a plan this scheduler never saw starts from now, as a first sight would
            var (handled, by, at) = PositionOf(p);
            handled ??= clock.GetUtcNow();
            (copied, copiedPause) = (handled, by);
            try { await runner.SetOwnerAsync(p.Package, p.Task, TaskOwners.Runner, reason, new ClientSeed(handled.Value, by, at), ct); }
            catch (Exception ex) when (ex is RunnerClientException or HttpRequestException or TaskCanceledException)
            {
                // an answer that did not come may still have been acted on: ask before putting the plan back, because putting it back while the runner has it would schedule it twice
                ClientTaskSchedule? now = null;
                try { now = await runner.ScheduleOfAsync(p.Package, p.Task, CancellationToken.None); } catch (Exception) { /* cannot ask either */ }
                if (now is null)
                {
                    handedOver = true;     // leave the owner as written: the runner has it or nobody does, never both
                    throw new OwnershipException($"The runner did not answer the hand-over of {key} ({ex.Message}) and could not be asked afterwards. The owner here stays 'runner' so that the plan is never scheduled twice: when the runner is back, run `server schedule owners` and either `cutover` again or `rollback`.");
                }

                if (!(now.Owner == TaskOwners.Runner && now.OwnerSetBy is not null)) throw;
            }

            handedOver = true;
            Audit("schedule.cutover", p, "ok");
            return new OwnershipChange(projectId, connection, plan, p.Owner, TaskOwners.Runner, p.Package, p.Task, copied, copiedPause, waited);
        }
        catch (Exception)
        {
            if (!handedOver) BuildScheduleSettings.Write(serverDirectory, projectId, connection, plan, p.Owner);     // back to what it was: this scheduler looks at the plan again, from its own position
            Audit("schedule.cutover", p, "failed");
            throw;
        }
    }

    /// <summary>Takes the plan's schedule back from the runner: the runner lets go and its run in flight is waited for, then its position and pause are copied here and the owner is written back as <c>old</c>.</summary>
    public async Task<OwnershipChange> RollBackAsync(string projectId, string connection, string plan, ScheduleRunnerLink link, string? reason, TimeSpan? wait = null, CancellationToken ct = default)
    {
        var p = Resolve(projectId, connection, plan);
        var key = BuildScheduleSettings.KeyOf(projectId, connection, plan);
        if (p.Owner == TaskOwners.Old) throw new OwnershipException($"{key} is scheduled by this server already.");
        if (p.Owner == TaskOwners.Observe)
        {
            // observed only: the runner never scheduled it, there is no position to bring back
            await runner.SetOwnerAsync(p.Package, p.Task, TaskOwners.Old, reason, null, ct);
            BuildScheduleSettings.Write(serverDirectory, projectId, connection, plan, TaskOwners.Old, link);
            Audit("schedule.rollback", p, "ok");
            return new OwnershipChange(projectId, connection, plan, TaskOwners.Observe, TaskOwners.Old, p.Package, p.Task, null, null, false);
        }

        // 1. the runner stops looking at the schedule; from here nobody schedules the plan until the owner is written below
        await runner.SetOwnerAsync(p.Package, p.Task, TaskOwners.Old, reason, null, ct);
        var waited = false;
        var done = false;
        try
        {
            // 2. its run in flight finishes where it started
            var until = clock.GetUtcNow() + (wait ?? DefaultWait);
            ClientTaskSchedule state;
            while ((state = await runner.ScheduleOfAsync(p.Package, p.Task, ct)) is { Running: > 0 } or { Queued: > 0 })
            {
                waited = true;
                if (clock.GetUtcNow() >= until) throw new OwnershipException($"A run of {p.Package}/{p.Task} was still queued or going on the runner after {(wait ?? DefaultWait).TotalSeconds:0} seconds. The runner schedules the plan again, as before, and nothing was changed here.");
                await _delay(PollEvery, ct);
            }

            // 3. the position and the pause the runner left are this server's again, then the owner
            _jobs.SaveHandled(p.Project.Id, p.Job.Subject, state.HandledUpTo ?? clock.GetUtcNow());
            _pauses.Clear(p.Project.Id, p.Job.Subject);
            if (state.PausedBy is { } by) _pauses.Set(p.Project.Id, p.Job.Subject, by, state.PausedAt ?? clock.GetUtcNow());
            BuildScheduleSettings.Write(serverDirectory, projectId, connection, plan, TaskOwners.Old, link);
            done = true;
            Audit("schedule.rollback", p, "ok");
            return new OwnershipChange(projectId, connection, plan, p.Owner, TaskOwners.Old, p.Package, p.Task, state.HandledUpTo, state.PausedBy, waited);
        }
        catch (Exception ex)
        {
            if (!done)
            {
                try { await runner.SetOwnerAsync(p.Package, p.Task, TaskOwners.Runner, "rollback failed: " + ex.Message, null, CancellationToken.None); }   // the runner takes the plan again, from the position it still has
                catch (Exception back) { throw new OwnershipException($"The rollback of {BuildScheduleSettings.KeyOf(projectId, connection, plan)} failed ({ex.Message}) and the runner could not be told to take the plan again ({back.Message}): nobody schedules it. Run `server schedule rollback` or `cutover` again when the runner is back."); }
            }

            Audit("schedule.rollback", p, done ? "ok" : "failed");
            throw;
        }
    }

    /// <summary>Compares, for an observed plan, what the runner would have queued with what this scheduler queued, from <paramref name="since"/> on.</summary>
    public async Task<OwnershipComparison> CompareAsync(string projectId, string connection, string plan, DateTimeOffset? since = null, TimeSpan? tolerance = null, CancellationToken ct = default)
    {
        var p = Resolve(projectId, connection, plan);
        var decisions = await runner.DecisionsAsync(p.Package, p.Task, since, ct);
        return Compare(decisions, tolerance ?? DefaultTolerance);
    }

    /// <summary>Pairs each decision of the runner with a decision of the old scheduler within the tolerance and with the same pause; what has no partner is a difference.</summary>
    public static OwnershipComparison Compare(IEnumerable<ClientDecision> decisions, TimeSpan tolerance)
    {
        var all = decisions.ToList();
        var old = all.Where(d => d.Source == "old").OrderBy(d => d.At).ToList();
        var matched = 0;
        var differences = new List<DecisionDifference>();
        foreach (var d in all.Where(d => d.Source == "runner").OrderBy(d => d.At))
        {
            var partner = old.FirstOrDefault(o => o.Paused == d.Paused && (o.At - d.At).Duration() <= tolerance);
            if (partner is null) differences.Add(new DecisionDifference("only the runner would have " + (d.Paused ? "passed this time over" : "queued a run"), d.At, d.Paused));
            else
            {
                old.Remove(partner);
                matched++;
            }
        }

        differences.AddRange(old.Select(o => new DecisionDifference("only this scheduler " + (o.Paused ? "passed this time over" : "queued a run"), o.At, o.Paused)));
        return new OwnershipComparison(matched, [.. differences.OrderBy(d => d.At)]);
    }

    /// <summary>Every scheduled plan of the hosted projects with the owner this server's file gives it and the one the runner gives it; the runner's word is the one that counts, and a plan the two disagree about is flagged.</summary>
    public async Task<IReadOnlyList<OwnerRow>> OwnersAsync(string? projectId = null, CancellationToken ct = default)
    {
        var settings = BuildScheduleSettings.Read(serverDirectory);
        var rows = new List<OwnerRow>();
        foreach (var project in projects.All().Where(x => projectId is null || x.Id == projectId))
        {
            var root = projects.RootOf(project);
            if (!Directory.Exists(root)) continue;
            foreach (var job in RefreshJobs.Discover(root))
            {
                var mine = settings.OwnerOf(project.Id, job.Subject);
                string? theirs = null, by = null;
                if (settings.Runner is not null)
                {
                    try
                    {
                        var state = await runner.ScheduleOfAsync(RunnerNaming.PackageOf(project.Id), RunnerNaming.TaskOf(job.Connection, job.Plan), ct);
                        if (state.Version is not null) (theirs, by) = (state.Owner, state.OwnerSetBy);
                    }
                    catch (RunnerClientException) { /* the runner is not reachable: the row says it has no word */ }
                }

                // the runner schedules a task nobody handed over, so "no word" and "old" differ: a plan this server schedules that the runner would schedule too is the dangerous one
                rows.Add(new OwnerRow(project.Id, job.Connection, job.Plan, mine, theirs, by, theirs is not null && theirs != mine));
            }
        }

        return rows;
    }
}
