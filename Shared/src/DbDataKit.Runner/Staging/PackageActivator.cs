using System.Globalization;
using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Staging;

/// <summary>Settings of the activation of a package version (plan 28a R18).</summary>
public sealed class ActivationOptions
{
    /// <summary>The longest an activation waits for the runs in flight of the package: ten minutes. After it the activation is not applied, the tasks that are still running are named, and the next try waits again.</summary>
    public TimeSpan MaxWait { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often the wait looks at the runs in flight.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>The lease of the hold on new runs; the wait renews it, a holder that dies stops holding after it.</summary>
    public TimeSpan HoldLease { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The service of the scheduler's work queue (<c>runner</c>).</summary>
    public string Service { get; init; } = "runner";

    public ILogger? Logger { get; init; }
}

/// <summary>Who asks for an activation and on what terms.</summary>
public enum ActivationMode
{
    /// <summary>A runner that sees every live instance ready: activates only then (atomic, once).</summary>
    Automatic,

    /// <summary>An administrator's activation: only when every live instance is ready.</summary>
    Administrator,

    /// <summary>An administrator's choice to activate although instances failed or are not ready (a recorded choice).</summary>
    Anyway,
}

public enum ActivationState
{
    Activated,

    /// <summary>Not every live instance is ready; nothing changed.</summary>
    NotReady,

    /// <summary>The dry run lists losses and nobody confirmed them; nothing changed.</summary>
    LossesNotConfirmed,

    /// <summary>Runs of these tasks were still in flight when the longest wait ran out; nothing changed, the next try waits again.</summary>
    WaitingForRuns,

    /// <summary>Another caller activated the version first; nothing was done here.</summary>
    AlreadyDone,

    NothingStaged,
}

/// <summary>What an activation did. <see cref="Losses"/> are the lines of the progress the activation loses (confirmed or not), <see cref="BusyTasks"/> the tasks still running when the wait ran out, <see cref="Message"/> the words for a person.</summary>
public sealed record ActivationOutcome(ActivationState State, int? Version, IReadOnlyList<string> Losses, IReadOnlyList<string> BusyTasks, string Message, StageProgress? Progress = null)
{
    public bool Activated => State == ActivationState.Activated;
}

/// <summary>One activation, switch or rollback asked for.</summary>
/// <param name="Mode">Who asks and on what terms.</param>
/// <param name="Actor">Who: a person, or <c>runner:&lt;instance&gt;</c> for an automatic activation.</param>
/// <param name="Live">The ids of the live instances (the group, as the caller reads it).</param>
/// <param name="ConfirmLosses">True when an administrator confirmed the losses the dry run listed.</param>
/// <param name="Reason">An administrator's words for activating anyway.</param>
/// <param name="ExpectedVersion">The version the channel has active, as the caller saw it; null does not check.</param>
public sealed record ActivationRequest(ActivationMode Mode, string Actor, IReadOnlyCollection<string> Live, bool ConfirmLosses = false, string? Reason = null, int? ExpectedVersion = null);

/// <summary>
/// Activation of a runner package version (plan 30 section 3.5, plan 29 section 4.3, plan 28a R18). It holds new runs of the package, waits for its runs in flight to finish (a run finishes on the version it started with),
/// and then, <b>in one transaction</b>, migrates every task's progress from the old declaration to the new one, moves the serving pointer and rebuilds the task projection. Two runners that activate at once migrate once: the
/// pointer is moved only if it is still where the transaction read it, and the loser sees the version already active. A rollback puts the progress back from the history the migration kept, in one transaction with the pointer
/// and the projection.
/// </summary>
public sealed class PackageActivator
{
    public const string Kind = DefinitionVersionSource.Kind;

    private readonly StateDatabase _state;
    private readonly DefinitionPublisher _publisher;
    private readonly ActivationOptions _options;
    private readonly ActivationHolds _holds;
    private readonly ModuleWorkQueueStore _queue;
    private readonly TaskProjections _projections;
    private readonly DefinitionStore _store;

    public PackageActivator(StateDatabase state, DefinitionPublisher publisher, ActivationOptions? options = null)
    {
        _state = state;
        _publisher = publisher;
        _options = options ?? new ActivationOptions();
        _holds = new ActivationHolds(state);
        _queue = new ModuleWorkQueueStore(state, _options.Service);
        _projections = new TaskProjections(state);
        _store = new DefinitionStore(state);
    }

    // ---- activate

    /// <summary>The staged version of the package in the channel and the progress its activation would lose (the lines an administrator confirms); null when nothing is staged.</summary>
    public (int Version, IReadOnlyList<string> Losses)? Preview(string name, string channel)
    {
        var head = _publisher.Head(Kind, name, channel);
        if (head?.Staged is not { } staged) return null;
        var previous = head.Active is { } active ? Load(name, active) : null;
        return (staged, LossLines(Plans(previous, Load(name, staged))));
    }

    /// <summary>Activates the staged version of <paramref name="name"/> in <paramref name="channel"/> as <paramref name="request"/> says.</summary>
    public async Task<ActivationOutcome> ActivateAsync(string name, string channel, ActivationRequest request, CancellationToken ct = default)
    {
        var head = _publisher.Head(Kind, name, channel);
        if (head?.Staged is not { } staged)
            return new ActivationOutcome(ActivationState.NothingStaged, head?.Active, [], [], $"The integration '{name}' has nothing staged in channel '{channel}'.");

        if (request.ExpectedVersion is { } expected && expected != (head.Active ?? 0))
            throw new DefinitionConflictException($"The {Kind} '{name}' has version {head.Active ?? 0} active, not {expected}: it changed since you looked. Read it again and decide.", expected, head.Active ?? 0);
        var next = Load(name, staged);
        var previous = head.Active is { } active ? Load(name, active) : null;
        var plans = Plans(previous, next);
        var losses = LossLines(plans);
        if (losses.Count > 0 && !request.ConfirmLosses)
            return new ActivationOutcome(ActivationState.LossesNotConfirmed, null, losses, [], $"Version {staged} of '{name}' loses progress and waits for an administrator to confirm it: {string.Join("; ", losses)}.");

        if (request.Mode != ActivationMode.Anyway)
        {
            var progress = _publisher.Status.Progress(Kind, name, channel, request.Live);
            if (!progress.IsAllReady)
            {
                // another runner may have activated the version between the look above and this one: an activation resets the instances' states, which reads as not ready
                if (_publisher.Head(Kind, name, channel) is { } now && now.Staged != staged)
                    return new ActivationOutcome(ActivationState.AlreadyDone, now.Active, losses, [], $"Another runner activated '{name}' first.", progress);
                return new ActivationOutcome(ActivationState.NotReady, null, losses, [], $"Version {staged} of '{name}' is not ready everywhere: {progress.Describe()}.", progress);
            }
        }

        var busy = await WaitForRunsAsync(name, channel, request.Actor, ct).ConfigureAwait(false);
        if (busy.Count > 0)
            return new ActivationOutcome(ActivationState.WaitingForRuns, null, losses, busy, $"Version {staged} of '{name}' waits for the runs in flight of {string.Join(", ", busy.Select(b => $"'{b}'"))}; it is tried again.");

        try
        {
            var lossNote = losses.Count > 0 ? $"confirmed losses: {string.Join("; ", losses)}" : null;
            void Migrate(DefinitionChange change) => ApplyMigration(change, previous, next, plans, request.Actor);
            if (request.Mode == ActivationMode.Automatic)
            {
                ActivationResult result;
                try { result = await Task.Run(() => _publisher.ActivateIfAllReady(Kind, name, request.Live, request.Actor, channel, Migrate), ct).ConfigureAwait(false); }
                catch (DefinitionNotFoundException)
                {
                    // another runner activated it between the look above and this call
                    return new ActivationOutcome(ActivationState.AlreadyDone, _publisher.Head(Kind, name, channel)?.Active, losses, [], $"Another runner activated '{name}' first.");
                }

                if (result.AlreadyDone) return new ActivationOutcome(ActivationState.AlreadyDone, _publisher.Head(Kind, name, channel)?.Active, losses, [], $"Another runner activated '{name}' first.", result.Progress);
                if (!result.Activated) return new ActivationOutcome(ActivationState.NotReady, null, losses, [], $"Version {staged} of '{name}' is not ready everywhere: {result.Progress.Describe()}.", result.Progress);
                return new ActivationOutcome(ActivationState.Activated, result.Version!.Version, losses, [], $"Version {result.Version.Version} of '{name}' is active.", result.Progress);
            }

            if (request.Mode == ActivationMode.Anyway)
            {
                var result = await Task.Run(() => _publisher.ActivateAnyway(Kind, name, request.Live, request.Actor, channel, request.Reason, Migrate, lossNote), ct).ConfigureAwait(false);
                if (result.AlreadyDone) return new ActivationOutcome(ActivationState.AlreadyDone, _publisher.Head(Kind, name, channel)?.Active, losses, [], $"'{name}' was activated by someone else first.", result.Progress);
                return new ActivationOutcome(ActivationState.Activated, result.Version!.Version, losses, [], $"Version {result.Version.Version} of '{name}' is active (anyway: {result.Progress.Describe()}).", result.Progress);
            }

            var done = await Task.Run(() => _publisher.Activate(Kind, name, request.Actor, channel, request.ExpectedVersion, lossNote, Migrate), ct).ConfigureAwait(false);
            return new ActivationOutcome(ActivationState.Activated, done.Version, losses, [], $"Version {done.Version} of '{name}' is active.");
        }
        finally
        {
            ReleaseHolds(name, channel, request.Actor);
        }
    }

    // ---- switch and rollback

    /// <summary>Points the channel at any stored version (promotion), migrating the progress of every task from the active version's declaration to the version's. A migration that loses progress needs <see cref="ActivationRequest.ConfirmLosses"/>.</summary>
    public async Task<ActivationOutcome> SwitchAsync(string name, string channel, int version, ActivationRequest request, CancellationToken ct = default)
    {
        var head = _publisher.Head(Kind, name, channel);
        var next = Load(name, version);
        var previous = head?.Active is { } active ? Load(name, active) : null;
        if (head?.Active == version) return new ActivationOutcome(ActivationState.AlreadyDone, version, [], [], $"Version {version} of '{name}' is already active.");
        var plans = Plans(previous, next);
        var losses = LossLines(plans);
        if (losses.Count > 0 && !request.ConfirmLosses)
            return new ActivationOutcome(ActivationState.LossesNotConfirmed, null, losses, [], $"Switching '{name}' to version {version} loses progress: {string.Join("; ", losses)}. Confirm it to go on.");
        var busy = await WaitForRunsAsync(name, channel, request.Actor, ct).ConfigureAwait(false);
        if (busy.Count > 0)
            return new ActivationOutcome(ActivationState.WaitingForRuns, null, losses, busy, $"Switching '{name}' waits for the runs in flight of {string.Join(", ", busy.Select(b => $"'{b}'"))}.");
        try
        {
            var switched = await Task.Run(() => _publisher.Switch(Kind, name, version, request.Actor, channel, request.ExpectedVersion, change => ApplyMigration(change, previous, next, plans, request.Actor), losses.Count > 0 ? $"confirmed losses: {string.Join("; ", losses)}" : null), ct).ConfigureAwait(false);
            return new ActivationOutcome(ActivationState.Activated, switched.Version, losses, [], $"Version {switched.Version} of '{name}' is active.");
        }
        finally
        {
            ReleaseHolds(name, channel, request.Actor);
        }
    }

    /// <summary>
    /// Returns the channel to its previous version and puts the progress back: the values before the migration that activated the version being left (kept in <c>checkpoint_history</c> and <c>state_history</c>), in one transaction
    /// with the pointer and the projection. When the version being left has no such migration (it was not activated through one), the progress is migrated by the plan, as a switch is, and losses need confirmation.
    /// </summary>
    public async Task<ActivationOutcome> RollbackAsync(string name, string channel, ActivationRequest request, CancellationToken ct = default)
    {
        var head = _publisher.Head(Kind, name, channel);
        if (head?.Active is not { } current || head.Previous is not { } target)
            return new ActivationOutcome(ActivationState.NothingStaged, head?.Active, [], [], $"'{name}' on {channel} has no earlier version to return to.");
        var left = Load(name, current);
        var back = Load(name, target);
        var prefix = MigrationPrefix(name, target, current);
        var names = left.Tasks.Select(t => t.Name).Union(back.Tasks.Select(t => t.Name), StringComparer.Ordinal).ToList();
        var hasHistory = names.Any(task => ProgressMigration.CanRestore(Store(name, channel, task), prefix));
        IReadOnlyList<(string Task, MigrationPlan Plan)> plans = hasHistory ? [] : Plans(left, back);
        var losses = hasHistory ? [] : LossLines(plans);
        if (losses.Count > 0 && !request.ConfirmLosses)
            return new ActivationOutcome(ActivationState.LossesNotConfirmed, null, losses, [], $"Rolling '{name}' back to version {target} loses progress: {string.Join("; ", losses)}. Confirm it to go on.");
        var busy = await WaitForRunsAsync(name, channel, request.Actor, ct).ConfigureAwait(false);
        if (busy.Count > 0)
            return new ActivationOutcome(ActivationState.WaitingForRuns, null, losses, busy, $"Rolling '{name}' back waits for the runs in flight of {string.Join(", ", busy.Select(b => $"'{b}'"))}.");
        try
        {
            var restored = 0;
            var rolled = await Task.Run(() => _publisher.Rollback(Kind, name, request.Actor, channel, request.ExpectedVersion, change =>
            {
                if (hasHistory)
                {
                    foreach (var task in names)
                        restored += ProgressMigration.Restore(Store(name, channel, task).Within(change.Connection, change.Transaction), prefix, $"rollback:{name}@{current}->{target}");
                    _projections.Replace(change.Connection, change.Transaction, name, channel, ProjectionDocuments.RowsOf(back, target.ToString(CultureInfo.InvariantCulture)));
                }
                else ApplyMigration(change, left, back, plans, request.Actor);
            }, hasHistory ? "progress restored from the history of the migration" : null), ct).ConfigureAwait(false);
            return new ActivationOutcome(ActivationState.Activated, rolled.Version, losses, [], hasHistory ? $"Version {rolled.Version} of '{name}' is active again; {restored} value(s) of its progress were put back." : $"Version {rolled.Version} of '{name}' is active again.");
        }
        finally
        {
            ReleaseHolds(name, channel, request.Actor);
        }
    }

    // ---- the transaction

    private static string MigrationPrefix(string package, int from, int to) => $"migration:{package}@{from}->{to}#";

    private ProgressStore Store(string package, string channel, string task) => new(_state, new ProgressKey(package, channel, task));

    /// <summary>The work of an activation that must stand or fall with the pointer: every task's progress migrated, the projection rebuilt for the version now active.</summary>
    private void ApplyMigration(DefinitionChange change, PackageManifest? previous, PackageManifest next, IReadOnlyList<(string Task, MigrationPlan Plan)> plans, string actor)
    {
        if (change.From is { } from)
        {
            var tag = MigrationPrefix(change.Name, from, change.To) + Guid.CreateVersion7().ToString("N");
            foreach (var (task, plan) in plans)
            {
                if (plan.IsEmpty) continue;
                var oldDeclaration = previous?.Tasks.FirstOrDefault(t => t.Name == task)?.Progress ?? ProgressDeclaration.Empty;
                var newDeclaration = next.Tasks.FirstOrDefault(t => t.Name == task)?.Progress ?? ProgressDeclaration.Empty;
                ProgressMigration.Apply(Store(change.Name, change.Channel, task).Within(change.Connection, change.Transaction), oldDeclaration, newDeclaration, tag, confirmLosses: true);
            }
        }

        _projections.Replace(change.Connection, change.Transaction, change.Name, change.Channel, ProjectionDocuments.RowsOf(next, change.To.ToString(CultureInfo.InvariantCulture)));
    }

    // ---- planning

    /// <summary>The plan of every task of either version: a task new in <paramref name="next"/> adds, a task it dropped removes (its progress is a loss).</summary>
    public static IReadOnlyList<(string Task, MigrationPlan Plan)> Plans(PackageManifest? previous, PackageManifest next)
    {
        var names = (previous?.Tasks.Select(t => t.Name) ?? []).Union(next.Tasks.Select(t => t.Name), StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        var plans = new List<(string, MigrationPlan)>();
        foreach (var task in names)
        {
            var oldDeclaration = previous?.Tasks.FirstOrDefault(t => t.Name == task)?.Progress ?? ProgressDeclaration.Empty;
            var newDeclaration = next.Tasks.FirstOrDefault(t => t.Name == task)?.Progress ?? ProgressDeclaration.Empty;
            try { plans.Add((task, ProgressMigration.Plan(oldDeclaration, newDeclaration))); }
            catch (ProgressMigrationException ex) { throw new ProgressMigrationException($"task '{task}': {ex.Message}"); }
        }

        return plans;
    }

    /// <summary>The lines that name what a set of plans loses: <c>task: value: what happens</c>.</summary>
    public static IReadOnlyList<string> LossLines(IReadOnlyList<(string Task, MigrationPlan Plan)> plans) =>
        [.. plans.SelectMany(p => p.Plan.Losses.Select(l => $"{p.Task}: {l.Target}: {l.Detail}"))];

    private PackageManifest Load(string name, int version) =>
        PackageManifestReader.Load(_store.Get(Kind, name, version)?.Content ?? throw new DefinitionNotFoundException($"'{name}' has no version {version}."), $"{Kind}/{name}@{version}").ThrowIfInvalid();

    // ---- waiting for runs

    /// <summary>
    /// The packages whose runs are held and waited for when <paramref name="name"/> changes in <paramref name="channel"/>: the package itself, and, when it is a runtime package (plan 28c E04), every package that has it
    /// active and names it in <c>launch.runtime</c>: a run in flight finishes on the runtime it started with, and nothing of the dependents starts until the pointer has moved.
    /// </summary>
    private IReadOnlyList<string> Affected(string name, string channel)
    {
        var affected = new List<string> { name };
        string? runtimeName = null;   // a runtime is stored per platform (<name>-<platform>), a definition names it by <name>
        foreach (var version in new[] { _publisher.Head(Kind, name, channel)?.Active, _publisher.Head(Kind, name, channel)?.Staged })
            if (version is { } v && _store.Get(Kind, name, v)?.Content is { } text && PackageManifestReader.Load(text, $"{Kind}/{name}@{v}") is { Ok: true, Manifest: { IsRuntime: true } runtimeManifest })
                runtimeName = runtimeManifest.Name;
        if (runtimeName is null) return affected;
        foreach (var other in _publisher.Names(Kind))
        {
            if (other == name || _publisher.Head(Kind, other, channel)?.Active is not { } active) continue;
            if (_store.Get(Kind, other, active)?.Content is { } text && PackageManifestReader.Load(text, $"{Kind}/{other}@{active}") is { Ok: true, Manifest.Launch.Runtime: { } reference } && reference.Name == runtimeName)
                affected.Add(other);
        }

        return affected;
    }

    private void ReleaseHolds(string name, string channel, string actor)
    {
        foreach (var package in Affected(name, channel)) _holds.Release(Kind, package, channel, actor);
    }

    /// <summary>
    /// Holds new runs of the package (and of the definitions that run on it, when it is a runtime) and waits until none of their tasks has a run in flight (an item claimed or running), or the longest wait
    /// (<see cref="ActivationOptions.MaxWait"/>) ran out. The names of the tasks still running then (<c>package/task</c> for a dependent); empty when idle (and the holds stay until the caller releases them).
    /// </summary>
    private async Task<IReadOnlyList<string>> WaitForRunsAsync(string name, string channel, string actor, CancellationToken ct)
    {
        var deadline = _state.Time.GetUtcNow() + _options.MaxWait;
        var affected = Affected(name, channel);
        while (true)
        {
            var busy = new List<string>();
            foreach (var package in affected)
            {
                _holds.Hold(Kind, package, channel, actor, _options.HoldLease);
                busy.AddRange(_queue.ListInFlight($"{package}/{channel}").Where(w => w.Item.Kind == RunnerScheduler.Kind && w.Item.Status is WorkItemStatus.Claimed or WorkItemStatus.Running)
                    .Select(w => package == name ? w.Item.Subject : $"{package}/{w.Item.Subject}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
            }

            if (busy.Count == 0) return [];
            if (_state.Time.GetUtcNow() >= deadline)
            {
                foreach (var package in affected) _holds.Release(Kind, package, channel, actor);
                _options.Logger?.LogWarning("The activation of {Package} on {Channel} gave up waiting after {Wait} for the runs of {Tasks}; it is tried again.", name, channel, _options.MaxWait, string.Join(", ", busy));
                return busy;
            }

            await Task.Delay(_options.Poll, _state.Time, ct).ConfigureAwait(false);
        }
    }
}
