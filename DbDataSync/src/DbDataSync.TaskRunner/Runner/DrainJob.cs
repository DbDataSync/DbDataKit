using DbDataKit.Config.Git;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;
using DbDataKit.Secrets;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Scripting;
using DbDataSync.State;
using Microsoft.Extensions.Logging;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// What the manifest's <c>inputs</c> say for the task <c>drain</c>: which replication of the definition to drain, how many passes at once, and where in the package the definition is (<c>repo_dir</c>, relative to the
/// package's root: the directory that holds <c>config/</c> and <c>libraries/</c>).
/// </summary>
public sealed record DrainInput(string Replication, int DegreeOfParallelism = 4, string RepoDir = "repo");

/// <summary>One mapping's position: where its incremental read got to (<c>ChangeWatermarks.Watermark</c> and its time).</summary>
public sealed class WatermarkProgress
{
    /// <summary>Opaque to everything but the reader: a hex LSN, a Change Tracking version in decimal, a time.</summary>
    [Checkpoint]
    public string? Position { get; set; }

    [Checkpoint]
    public DateTimeOffset? PositionTime { get; set; }
}

/// <summary>The checkpoint <c>watermark</c> per mapping and source table (plan 28c section 5.1, reduced to two parts); the key is the engine's watermark key.</summary>
public sealed class DrainProgress : Progress
{
    public Keyed<WatermarkProgress> Watermark { get; } = new();
}

public static class DrainCounters
{
    /// <summary>Rows written to the targets: what the runner records as the rows the run moved.</summary>
    public static readonly Counter Rows = new("rows");

    public static readonly Counter RowsRead = new("rows_read");
}

/// <summary>One pass over one mapping, planned by the drain.</summary>
public sealed record PlannedPass(string Mapping, WorkItem Item);

public static class DrainUnits
{
    /// <summary>A mapping's pass; the key is the mapping, so a retry of the attempt chain skips the mappings that finished.</summary>
    public static readonly Unit<PlannedPass> Mapping = Unit.Of<PlannedPass>("mapping", p => p.Mapping);
}

/// <summary>
/// One drain of one replication, as a task of the runner (plan 28c E03, decision ED4: the unit of a run is what runs in one process today). The engine is <see cref="RunExecutor"/> unchanged; what differs from the
/// worker the API spawns is who plans the passes (this job: one primary pass per mapping, each a unit of the run), where state lives (<see cref="ProtocolRunnerState"/>: the position is the run's checkpoint, saved in the
/// protocol call that follows the target's commit), where the definition is read from (the package's view) and where the engine may write (the run's scratch, never the view).
/// </summary>
[RunnerTask("drain")]
public sealed class DrainJob : RunnerJob<DrainInput, DrainProgress, NoSlots>
{
    private readonly SemaphoreSlim _save = new(1, 1);

    protected override async Task RunAsync(CancellationToken ct)
    {
        var package = Environment.GetEnvironmentVariable(RunnerProtocolNames.PackageDirectoryVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.PackageDirectoryVariable} is not set: the runner tells a task where its package's view is.");
        var scratch = Environment.GetEnvironmentVariable(RunnerProtocolNames.ScratchVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.ScratchVariable} is not set: the runner tells a task where it may write.");
        var repoRoot = Path.GetFullPath(Path.Combine(package, Input.RepoDir));

        // The definition is read, never written: logins come from the environment the runner set (DBDATA_SECRET_..., ED11), the engine's own caches go to the scratch directory, and the git service the config
        // repository wants for its writes (which a drain never makes) is given a repository of its own in the scratch directory, since the view is read-only (E06 removes the need).
        var secrets = SecretStore.ForProviders();
        var config = new ConfigRepository(Path.Combine(repoRoot, "config"), new GitCommitService(Path.Combine(scratch, "git")), secrets);
        var engine = EngineComposition.Compose(config, repoRoot, new ScriptCacheDirectory(Path.Combine(scratch, "script-cache")));

        var task = config.LoadReplicationTask(Input.Replication);
        var state = new ProtocolRunnerState(new Checkpoints(this), Run.Log);
        var executor = new RunExecutor(
            config, engine.Drivers, secrets, engine.Libraries, repoRoot, state, new LoggedRunnerConfig(Run.Log), engine.Scripts, Path.Combine(scratch, "state.db"));

        var passes = Plan(config, task.Name);
        Status($"{passes.Count} mapping(s) of '{task.Name}'");
        await ForEachAsync(DrainUnits.Mapping, passes, Math.Max(1, Input.DegreeOfParallelism), async (pass, token) =>
        {
            await executor.RunPlannedItemAsync(pass.Item, token).ConfigureAwait(false);
            var outcome = state.TakeOutcome(pass.Item.RunId)
                ?? throw new TaskFailureException(TaskFailureKind.Permanent, $"The pass over '{pass.Mapping}' ended without saying how.", pass.Mapping);
            Count(DrainCounters.RowsRead, outcome.RowsRead);
            Count(DrainCounters.Rows, outcome.RowsWritten);
            if (outcome.Status != RunStatus.Succeeded)
            {
                var kind = outcome.Status == RunStatus.Cancelled ? TaskFailureKind.Cancelled
                    : outcome.FailureKind is RunFailureKinds.MappingStillLoading or RunFailureKinds.ConcurrentLoadInProgress ? TaskFailureKind.Temporary
                    : TaskFailureKind.Permanent;
                throw new TaskFailureException(kind, $"The pass over '{pass.Mapping}' ended {outcome.Status}: {outcome.Summary}", pass.Mapping);
            }
        }, ct).ConfigureAwait(false);
    }

    /// <summary>One primary pass per mapping of the replication, in name order: what a periodic replication's tick enqueues (the planner as a library is E11).</summary>
    private static List<PlannedPass> Plan(ConfigRepository config, string replication)
    {
        var id = 0L;
        return [.. config.ListTableMappings(replication).Order(StringComparer.Ordinal).Select(mapping =>
            new PlannedPass(mapping, new WorkItem(++id, replication, RunKind.Primary, mapping, WorkQueueStore.NoSegment, null, Guid.CreateVersion7(), WorkItemStatus.Claimed, WorkItemKinds.FromConfig)))];
    }

    /// <summary>The position of a mapping as the run's checkpoint: read from the progress the run started with, written and saved at once, one save at a time (a save writes everything changed, and two at once would conflict with each other).</summary>
    private sealed class Checkpoints(DrainJob job) : IDrainCheckpoints
    {
        public (string Position, DateTimeOffset? Time)? Read(string key)
        {
            var item = job.Progress.Watermark[key];
            return item.Position is { } position ? (position, item.PositionTime) : null;
        }

        public void Write(string key, string position, DateTimeOffset? time)
        {
            job._save.Wait();
            try
            {
                var item = job.Progress.Watermark[key];
                item.Position = position;
                item.PositionTime = time;
                job.SaveProgressAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                job._save.Release();
            }
        }
    }

    /// <summary>The one config write a pass can make, its provisioning report, is a log line for now (ED9: a run output, committed by the authoring side later); the pass offers it again next time.</summary>
    private sealed class LoggedRunnerConfig(ILogger log) : IRunnerConfig
    {
        public void ReportProvisionedTargetColumns(string replicationName, string mappingName, IReadOnlyList<CachedColumn> columns) =>
            log.LogInformation("Provisioned target of '{Mapping}' on '{Replication}' has {Count} column(s); the report is not kept yet (plan 28c ED9).", mappingName, replicationName, columns.Count);
    }
}
