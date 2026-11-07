using System.Globalization;
using DbDataKit.Runner.Scheduler;
using DbDataKit.State;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Runner.Staging;

/// <summary>A staged version cannot be prepared on this instance; the message is what the instance's status says.</summary>
public sealed class StagingFailedException(string message) : InvalidOperationException(message);

/// <summary>Settings of one instance's staging follower.</summary>
public sealed class StagingOptions
{
    /// <summary>This instance's id (the runner module's instance row); null while the engine has not registered. Nothing is prepared before there is one.</summary>
    public required Func<Guid?> Instance { get; init; }

    /// <summary>How long an instance may be silent before it is not live to the group (the group's lease).</summary>
    public TimeSpan Lease { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>How often the follower looks for staged versions.</summary>
    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>What is wrong with the contracts a package was built against and the newer manifest keys it uses, in words (null: this runner can serve it). Null: nothing is checked here.</summary>
    public Func<PackageManifest, string?>? Contracts { get; init; }

    public ILogger? Logger { get; init; }
}

/// <summary>What one pass of the follower did.</summary>
public sealed record StagingPass(IReadOnlyList<string> Prepared, IReadOnlyList<string> Failed, IReadOnlyList<string> Activated);

/// <summary>
/// Staging on the runner (plan 30 section 3.1, plan 28a R17): one per instance. For every STAGED version of an <c>integration</c> it prepares the version here and reports <c>preparing</c>, then <c>ready</c> or <c>failed</c>
/// through the <see cref="InstanceStatusStore"/>: it materializes the files through the artifact cache (every hash verified), checks the contracts the package was built against, validates the manifest again, asks
/// <see cref="RoutingCheck.CanActivate"/> for every task's required labels, and dry-runs the progress migration against the progress stored now, putting the plan and its losses into the status. Whichever instance finds every live
/// instance ready activates the version (<see cref="PackageActivator"/>), unless the dry run lists losses: those wait for an administrator's confirmation. It also keeps this instance's <c>active</c> rows true.
/// </summary>
public sealed class StagingFollower
{
    public const string Kind = PackageActivator.Kind;

    private readonly StateDatabase _state;
    private readonly DefinitionPublisher _publisher;
    private readonly DefinitionStore _store;
    private readonly ArtifactCache _cache;
    private readonly PackageActivator _activator;
    private readonly StagingOptions _options;
    private readonly RunnerInstances _instances;

    public StagingFollower(StateDatabase state, DefinitionPublisher publisher, ArtifactCache cache, PackageActivator activator, StagingOptions options)
    {
        _state = state;
        _publisher = publisher;
        _store = new DefinitionStore(state);
        _cache = cache;
        _activator = activator;
        _options = options;
        _instances = new RunnerInstances(state);
    }

    /// <summary>Looks for staged versions until <paramref name="ct"/> ends.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await PassAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _options.Logger?.LogWarning("Staging pass failed: {Message}", ex.Message);
            }

            try { await Task.Delay(_options.Poll, _state.Time, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One look: prepares what is staged and not yet prepared here, activates what every live instance has ready, and reports the versions this instance serves as active.</summary>
    public async Task<StagingPass> PassAsync(CancellationToken ct = default)
    {
        var prepared = new List<string>();
        var failed = new List<string>();
        var activated = new List<string>();
        if (_options.Instance() is not { } me) return new StagingPass(prepared, failed, activated);
        var live = _instances.LiveInstances(_options.Lease);
        if (live.All(l => l.Instance.Id != me)) return new StagingPass(prepared, failed, activated);   // not live yet: the group does not count it, and nothing here would be seen
        var id = me.ToString("D");
        var liveIds = live.Select(l => l.Instance.Id.ToString("D")).ToList();

        foreach (var name in _publisher.Names(Kind))
            foreach (var head in _publisher.HeadsOf(Kind, name))
            {
                ct.ThrowIfCancellationRequested();
                var mine = _publisher.Status.List(Kind, name, head.Channel).FirstOrDefault(s => s.Instance == id && !s.Gone);
                if (head.Staged is { } staged)
                {
                    if (mine is null || mine.Version != staged || mine.State is not (InstanceStates.Ready or InstanceStates.Failed))
                    {
                        if (await PrepareAsync(name, head, staged, id, live, ct).ConfigureAwait(false)) prepared.Add($"{name}/{head.Channel}@{staged}");
                        else failed.Add($"{name}/{head.Channel}@{staged}");
                    }

                    try
                    {
                        var outcome = await _activator.ActivateAsync(name, head.Channel, new ActivationRequest(ActivationMode.Automatic, $"runner:{id}", liveIds), ct).ConfigureAwait(false);
                        if (outcome.Activated) activated.Add($"{name}/{head.Channel}@{outcome.Version}");
                        else if (outcome.State is ActivationState.WaitingForRuns or ActivationState.LossesNotConfirmed)
                            _options.Logger?.LogDebug("{Message}", outcome.Message);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // one package that cannot be activated (a manifest that does not read, a migration that fails) does not stop the others
                        _options.Logger?.LogWarning("Version {Version} of {Package} on {Channel} was not activated: {Message}", staged, name, head.Channel, ex.Message);
                    }
                }
                else if (head.Active is { } active && (mine is null || mine.Version != active || mine.State != InstanceStates.Active) && !(mine is { State: InstanceStates.Failed } && mine.Version == active))
                {
                    _publisher.Status.Report(id, Kind, name, head.Channel, active, InstanceStates.Active);
                }
            }

        return new StagingPass(prepared, failed, activated);
    }

    /// <summary>Prepares one staged version here and reports the result. True when it is ready.</summary>
    private async Task<bool> PrepareAsync(string name, DefinitionHead head, int version, string id, IReadOnlyList<LiveInstance> live, CancellationToken ct)
    {
        var channel = head.Channel;
        _publisher.Status.Report(id, Kind, name, channel, version, InstanceStates.Preparing);
        try
        {
            var found = await CheckAsync(name, channel, head.Active, version, live, ct).ConfigureAwait(false);
            _publisher.Status.Report(id, Kind, name, channel, version, InstanceStates.Ready, note: found.Note, hasLosses: found.Losses.Count > 0);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var message = ex is StagingFailedException or ProgressMigrationException or PackageManifestException or ArtifactException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            _options.Logger?.LogWarning("Version {Version} of {Package} on {Channel} failed staging on this instance: {Message}", version, name, channel, message);
            _publisher.Status.Report(id, Kind, name, channel, version, InstanceStates.Failed, message);
            return false;
        }
    }

    /// <summary>What a successful preparation found: the words for the status and the progress it would lose.</summary>
    public sealed record Found(string Note, IReadOnlyList<string> Losses);

    /// <summary>The checks of staging, in order; throws <see cref="StagingFailedException"/> (or the migration's exception) naming what is wrong.</summary>
    public async Task<Found> CheckAsync(string name, string channel, int? active, int version, IReadOnlyList<LiveInstance> live, CancellationToken ct)
    {
        var definition = _store.Get(Kind, name, version) ?? throw new StagingFailedException($"'{name}' has no version {version}.");

        // the manifest again, as this runner reads it (R01's reader and validator)
        var load = PackageManifestReader.Load(definition.Content, $"{Kind}/{name}@{version}");
        if (!load.Ok) throw new StagingFailedException($"The manifest of version {version} does not validate on this runner: {string.Join(" ", load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.ToString()))}");
        var manifest = load.Manifest!;

        // compiled code depends on the contracts it was built against (plan 30 section 6.1)
        if (_options.Contracts?.Invoke(manifest) is { } contract)
            throw new StagingFailedException($"Version {version} of '{name}' cannot run on this runner: {contract}");

        // a definition run by a runtime package (plan 28c E04): the runtime has to be active in this channel and in the range, and both views are prepared here
        if (manifest.Launch.Runtime is { } reference)
        {
            ResolvedRuntime runtime;
            try { runtime = RuntimeResolution.Resolve(_store, _publisher, reference, channel, $"{name}@{version}"); }
            catch (RuntimeUnavailableException ex) { throw new StagingFailedException($"Version {version} of '{name}' cannot be staged: {ex.Message}"); }
            try
            {
                using var runtimeView = await _cache.MaterializeAsync($"{runtime.Package}@{runtime.Version}", runtime.Content.Files, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new StagingFailedException($"The files of the runtime '{runtime.Package}' (version {runtime.RuntimeVersion}) could not be materialized here: {ex.Message}");
            }
        }

        // a task that requires a label no live runner has can never run: the version does not become active, and the message names the label
        foreach (var task in manifest.Tasks)
            if (RoutingCheck.CanActivate(task.Requires, live) is { Count: > 0 } missing)
                throw new StagingFailedException($"Task '{task.Name}': {RoutingCheck.ActivationRefusal(name, version.ToString(CultureInfo.InvariantCulture), missing)}");

        // every file in the cache, every hash verified; the view is not kept (the objects are, and a run makes its own view)
        var files = _publisher.Artifacts(Kind, name, version).Select(a => new ArtifactFile(a.Path, a.Sha256, a.Size, a.MediaType, a.PayloadId)).ToList();
        try
        {
            using var view = await _cache.MaterializeAsync($"{name}@{version}", files, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new StagingFailedException($"The files of version {version} could not be materialized here: {ex.Message}");
        }

        // the dry run of the progress migration, per task, against the progress stored now
        var previous = active is { } a ? PackageManifestReader.Load(_store.Get(Kind, name, a)?.Content ?? "", $"{Kind}/{name}@{a}").ThrowIfInvalid() : null;
        var losses = new List<string>();
        var changes = new List<string>();
        foreach (var task in manifest.Tasks.Select(t => t.Name).Union(previous?.Tasks.Select(t => t.Name) ?? [], StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var oldDeclaration = previous?.Tasks.FirstOrDefault(t => t.Name == task)?.Progress ?? ProgressDeclaration.Empty;
            var newDeclaration = manifest.Tasks.FirstOrDefault(t => t.Name == task)?.Progress ?? ProgressDeclaration.Empty;
            MigrationPlan plan;
            try { plan = ProgressMigration.DryRun(new ProgressStore(_state, new ProgressKey(name, channel, task)), oldDeclaration, newDeclaration); }
            catch (ProgressMigrationException ex) { throw new StagingFailedException($"The progress of task '{task}' cannot be migrated to version {version}: {ex.Message}"); }
            losses.AddRange(plan.Losses.Select(l => $"{task}: {l.Target}: {l.Detail}"));
            changes.AddRange(plan.Steps.Where(s => !s.LosesProgress).Select(s => $"{task}: {s.Target}: {s.Detail}"));
        }

        var note = losses.Count > 0
            ? $"LOSSES: {string.Join("; ", losses)}" + (changes.Count > 0 ? $" | other changes: {string.Join("; ", changes)}" : "")
            : changes.Count > 0 ? $"migration: {string.Join("; ", changes)}" : "no change to the progress";
        return new Found(note, losses);
    }
}
