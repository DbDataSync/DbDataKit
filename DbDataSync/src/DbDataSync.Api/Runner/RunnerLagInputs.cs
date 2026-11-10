using System.Collections.Concurrent;
using System.Globalization;
using DbDataSync.State;

namespace DbDataSync.Api.Runner;

/// <summary>
/// What the lag of a mapping is measured from, wherever the replication runs (plan 28c E13): the mapping's applied position and its commit time, and the polling gate's checks of the source. The API's own path reads
/// them from its state (<see cref="StoreLagInputs"/>); a replication the runner owns has them as the runner's checkpoint <c>watermark</c> and the <c>change-checks</c> its drains record (<see cref="RunnerLagInputs"/>). One
/// computation (<see cref="DbDataSync.Api.Services.ReaderLagService"/>) reads either, so the same data gives the same figure on both paths.
/// </summary>
public interface ILagInputs
{
    AppliedPosition? GetAppliedPosition(string taskName, string mappingName, string watermarkKey);

    ChangeCheck? GetLatestCheck(string connectionName, string sourceDatabase, string sourceKind, bool requireSourceTime = false);

    ChangeCheck? FindEarliestCheck(string connectionName, string sourceDatabase, string sourceKind, Func<string, bool> matches);
}

/// <summary>The inputs in the API's own state: the stores the supervisor path writes.</summary>
public sealed class StoreLagInputs(ChangeWatermarkStore watermarks, ChangeCheckStore checks) : ILagInputs
{
    public AppliedPosition? GetAppliedPosition(string taskName, string mappingName, string watermarkKey) => watermarks.GetAppliedPosition(taskName, mappingName, watermarkKey);

    public ChangeCheck? GetLatestCheck(string connectionName, string sourceDatabase, string sourceKind, bool requireSourceTime = false) => checks.GetLatestCheck(connectionName, sourceDatabase, sourceKind, requireSourceTime);

    public ChangeCheck? FindEarliestCheck(string connectionName, string sourceDatabase, string sourceKind, Func<string, bool> matches) => checks.FindEarliestCheck(connectionName, sourceDatabase, sourceKind, matches);
}

/// <summary>What the runner held of one replication when it was last asked: where each mapping's position is, and the checks of its drains, oldest first.</summary>
public sealed record RunnerLagSnapshot(IReadOnlyDictionary<string, AppliedPosition> Positions, IReadOnlyList<ChangeCheck> Checks, DateTimeOffset TakenAt);

/// <summary>
/// The inputs of a replication the runner owns, read from the runner and kept (plan 28c E13). The position of a mapping is the item of the checkpoint <c>watermark</c> whose key is the engine's watermark key (the parts
/// <c>position</c> and <c>position_time</c>); the source's current position is the newest <c>change-checks</c> a drain recorded, which is what the polling gate wrote to <c>change_check</c> on the API's path. Describing lag
/// never asks a runner (the property <c>ReaderLagService</c> keeps: a status screen reads cached figures): <see cref="RefreshAsync"/> asks, from the hosted refresher, and the readings are the last answer. A runner that
/// could not be asked leaves the answer as it was and says so in <see cref="Problem"/>; a replication never read has no figures, which is "no data yet", never a guess.
/// </summary>
public sealed class RunnerLagInputs(RunnerAccess runner, TimeProvider? clock = null)
{
    /// <summary>How many checks of the drains are read: a cycle of a drain writes one batch and each batch has one check per source group, so this is hours of a continuous drain.</summary>
    public const int ChecksRead = 500;

    private readonly ConcurrentDictionary<string, RunnerLagSnapshot> _snapshots = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _problems = new(StringComparer.Ordinal);

    public bool Owns(string replication) => runner.File.OwnerOf(replication) == DbDataKit.State.TaskOwners.Runner;

    /// <summary>Why the last attempt to read the replication from the runner failed, or null when it did not (or none was made).</summary>
    public string? Problem(string replication) => _problems.GetValueOrDefault(replication);

    /// <summary>The inputs of one replication: what the runner said the last time it was asked.</summary>
    public ILagInputs For(string replication) => new Reading(_snapshots.GetValueOrDefault(replication));

    /// <summary>Asks the runner for the replication's checkpoint and checks and keeps the answer. False, with <see cref="Problem"/> set, when the runner could not be asked.</summary>
    public async Task<bool> RefreshAsync(string replication, CancellationToken ct)
    {
        try
        {
            var sync = runner.Require();
            var runs = runner.OpenRuns() ?? throw new RunnerUnavailableException("The linked runner cannot be asked for its runs.");
            var task = RunnerAccess.TaskOf(replication);
            var items = await sync.CheckpointAsync(task, WatermarkCheckpoint.Name, ct).ConfigureAwait(false);
            var positions = new Dictionary<string, AppliedPosition>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                item.Parts.TryGetValue(WatermarkCheckpoint.Position, out var position);
                DateTimeOffset? time = item.Parts.TryGetValue(WatermarkCheckpoint.PositionTime, out var text) && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) ? parsed : null;
                positions[item.Key] = new AppliedPosition(position, time);
            }

            var steps = await runs.StepsAsync(task, ChangeCheckText.StepName, 100, null, null, ct).ConfigureAwait(false);
            var checks = steps.SelectMany(s => ChangeCheckText.TryParse(s.Result)?.Checks ?? []).Select(ChangeCheckText.ToCheck)
                .OrderBy(c => c.CheckedAtUtc).TakeLast(ChecksRead).ToList();
            _snapshots[replication] = new RunnerLagSnapshot(positions, checks, (clock ?? TimeProvider.System).GetUtcNow());
            _problems.TryRemove(replication, out _);
            return true;
        }
        catch (Exception ex) when (ex is DbDataKit.Runner.Client.RunnerClientException or RunnerUnavailableException or OwnershipException)
        {
            _problems[replication] = ex.Message;
            return false;
        }
    }

    private sealed class Reading(RunnerLagSnapshot? snapshot) : ILagInputs
    {
        public AppliedPosition? GetAppliedPosition(string taskName, string mappingName, string watermarkKey) =>
            snapshot is not null && snapshot.Positions.TryGetValue(WatermarkCheckpoint.ItemKey(mappingName, watermarkKey), out var position) ? position : null;

        /// <summary>The same choice the store makes: the newest check of the group (by when it was made), the newest one that carries a source time when one is asked for.</summary>
        public ChangeCheck? GetLatestCheck(string connectionName, string sourceDatabase, string sourceKind, bool requireSourceTime = false) =>
            Group(connectionName, sourceDatabase, sourceKind).Where(c => !requireSourceTime || c.SourceTimeUtc is not null).OrderByDescending(c => c.CheckedAtUtc).FirstOrDefault();

        public ChangeCheck? FindEarliestCheck(string connectionName, string sourceDatabase, string sourceKind, Func<string, bool> matches) =>
            Group(connectionName, sourceDatabase, sourceKind).Where(c => c.Value is not null).OrderBy(c => c.CheckedAtUtc).FirstOrDefault(c => matches(c.Value!));

        private IEnumerable<ChangeCheck> Group(string connectionName, string sourceDatabase, string sourceKind) =>
            (snapshot?.Checks ?? []).Where(c => c.ConnectionName == connectionName && c.SourceDatabase == sourceDatabase && c.SourceKind == sourceKind);
    }
}

/// <summary>Keeps the lag inputs of the replications the runner owns fresh (plan 28c E13), so that describing lag reads figures and asks nobody.</summary>
public sealed class RunnerLagRefresher(RunnerLagInputs inputs, RunnerOwnerFile file, Microsoft.Extensions.Logging.ILogger<RunnerLagRefresher> logger) : Microsoft.Extensions.Hosting.BackgroundService
{
    /// <summary>The cadence of the refresh: the polling gate's own tick is five seconds, so a faster read would only see the same checks.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                foreach (var (replication, owner) in file.Read().Owners)
                    if (owner == DbDataKit.State.TaskOwners.Runner && !await inputs.RefreshAsync(replication, stoppingToken).ConfigureAwait(false))
                        logger.LogWarning("The runner could not be read for the lag of '{Replication}': {Problem}", replication, inputs.Problem(replication));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Reading the runner for lag failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
