using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Secrets;
using DbDataSync.Libraries;
using DbDataKit.Sync.Provisioning;
using DbDataSync.Scripting;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>What the set's preflight is started with: the replication to check (empty: every enabled one, which is what a control service starts it with) and where the definition is in the package.</summary>
public sealed record SetPreflightInput(string Replication = "", string RepoDir = "repo");

public static class SetPreflightSteps
{
    /// <summary>Each part's document (<see cref="Preflight.Document"/>), keyed by the part.</summary>
    public static readonly ValueStep<JsonObject, JsonObject> Findings = Step.Value<JsonObject, JsonObject>(Preflight.StepName, r => (string?)r["part"] ?? Preflight.TaskName);

    /// <summary>The task's inputs as the manifest declares them, each with a default, since a control service starts the preflight with none.</summary>
    public static IReadOnlyList<TaskInput> Inputs(string repoDir) =>
    [
        new("replication", DbDataKit.State.ProgressType.Text, Default: "", Description: "The replication to check; empty checks every enabled replication of the set"),
        new("repo_dir", DbDataKit.State.ProgressType.Text, Default: repoDir, Description: "Where the definition is in the package"),
    ];
}

/// <summary>
/// The replication set's preflight on a runner (Design: docs/authoring-metadata.md#a-sync-project): the manual, read-only task <c>preflight</c> of the set's package, which a control service runs on a version staged
/// in production before it activates it. It runs <see cref="SetPreflight"/> over connections opened from the run's slots with the run login, and records each part's findings as the step
/// <see cref="Preflight.StepName"/>; a finding is the document's to say, not a failure of the run.
/// </summary>
[RunnerTask(Preflight.TaskName)]
[RunnerDeclarations(typeof(SetPreflightSteps))]
public sealed class SetPreflightJob : RunnerJob<SetPreflightInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var package = Environment.GetEnvironmentVariable(RunnerProtocolNames.PackageDirectoryVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.PackageDirectoryVariable} is not set: the runner tells a task where its package's view is.");
        var scratch = Environment.GetEnvironmentVariable(RunnerProtocolNames.ScratchVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.ScratchVariable} is not set: the runner tells a task where it may write.");
        var repoRoot = Path.GetFullPath(Path.Combine(package, Input.RepoDir));
        if (Run.DryRun)
        {
            Run.WouldDo($"read the source and target of {(Input.Replication.Length > 0 ? $"the replication '{Input.Replication}'" : "every enabled replication")} with the run logins; nothing is written");
            return;
        }

        var secrets = SecretStore.ForProviders();
        var config = ConfigRepository.ReadOnly(Path.Combine(repoRoot, "config"), secrets);
        var engine = EngineComposition.Compose(config, repoRoot, new ScriptCacheDirectory(Path.Combine(scratch, "script-cache")));
        var connections = new RunLoginConnections(config, engine.Drivers, engine.Libraries, repoRoot, secrets);
        var provisioner = new MappingProvisioner(config, connections, new NothingAfterApply(),
            new MappingColumnReader(new RunLoginColumnCatalog(connections, new ScriptedMetadata(engine.Scripts, config))), engine.Drivers);

        foreach (var part in await new SetPreflight(config, connections, provisioner).CheckAsync(Input.Replication, ct).ConfigureAwait(false))
        {
            Status($"{Preflight.TaskName} {part.Part}");
            var document = Preflight.Document(part.Part, part.Findings);
            await Step(SetPreflightSteps.Findings, document).Repeatable().RunAsync(_ => Task.FromResult(document), ct).ConfigureAwait(false);
        }
    }
}
