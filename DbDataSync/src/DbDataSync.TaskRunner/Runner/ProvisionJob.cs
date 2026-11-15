using System.Data.Common;
using DbDataKit.Runner;
using DbDataSync.Drivers.Generic;
using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Libraries;
using DbDataSync.Provisioning;
using DbDataSync.Scripting;
using DbDataSync.Scripting.Abstractions;

namespace DbDataSync.TaskRunner.Runner;

/// <summary>
/// What a person asks the provisioning task to apply (plan 28c E21): the replication, and either one mapping's action (<see cref="ProvisioningActions"/>) or, with no mapping, the chosen step ids of the
/// replication-wide plan (comma separated).
/// </summary>
public sealed record ProvisionInput(string Replication, string Mapping = "", string Action = "", string Steps = "", string RepoDir = "repo");

/// <summary>What one apply did: the steps it ran, as the Sync server's Apply answers them, a run output the Sync screens read.</summary>
public sealed record ProvisionReport(string Replication, string Mapping, List<ProvisionStepReport> Steps);

/// <summary>
/// One step of an apply: its title, whether it was applied, and the error when it was not; a step of the replication-wide plan also carries its id and outcome (<see cref="ProvisioningStepOutcome"/>), so the page that chose the steps
/// shows each one's result on its row (a step the fresh plan no longer needs is not a failure).
/// </summary>
public sealed record ProvisionStepReport(string Title, bool Succeeded, string? Error, string? Id = null, string? Outcome = null);

public static class ProvisionSteps
{
    /// <summary>The apply's own report, keyed by the mapping (or the replication, for the replication-wide plan).</summary>
    public static readonly ValueStep<ProvisionReport, ProvisionReport> Applied =
        Step.Value<ProvisionReport, ProvisionReport>("provision-applied", r => r.Mapping.Length > 0 ? r.Mapping : r.Replication);

    /// <summary>The renames an apply caught the target up with (<see cref="RunOutputMappingAfterApply"/>).</summary>
    public static readonly ValueStep<AppliedRenamesReport, AppliedRenamesReport> RenamesApplied =
        Step.Value<AppliedRenamesReport, AppliedRenamesReport>("renames-applied", r => r.Mapping);

    /// <summary>The target's columns read back after an apply: the drain's own report, so the Sync screens read both the same way.</summary>
    public static readonly ValueStep<ProvisionedColumnsReport, ProvisionedColumnsReport> ProvisionedColumns = DrainSteps.ProvisionedColumns;
}

/// <summary>
/// A replication's database setup applied on the runner (plan 28c E21, Sync half step c): the manual-only task <c>provision</c> of the replication set's package. It runs the same
/// <see cref="MappingProvisioner"/> the Sync server's Apply runs, over connections opened from the run's slots with the run login (E20), under the name of the person who asked
/// (the run's <c>by</c>); the published definition is read, never written (ED8), so what the apply records about a mapping is reported as outputs of the run (step b).
/// </summary>
[RunnerTask("provision")]
[RunnerDeclarations(typeof(ProvisionSteps))]
public sealed class ProvisionJob : RunnerJob<ProvisionInput, NoProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var package = Environment.GetEnvironmentVariable(RunnerProtocolNames.PackageDirectoryVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.PackageDirectoryVariable} is not set: the runner tells a task where its package's view is.");
        var scratch = Environment.GetEnvironmentVariable(RunnerProtocolNames.ScratchVariable)
            ?? throw new InvalidOperationException($"{RunnerProtocolNames.ScratchVariable} is not set: the runner tells a task where it may write.");
        var repoRoot = Path.GetFullPath(Path.Combine(package, Input.RepoDir));

        var secrets = SecretStore.ForProviders();
        var config = ConfigRepository.ReadOnly(Path.Combine(repoRoot, "config"), secrets);
        var engine = EngineComposition.Compose(config, repoRoot, new ScriptCacheDirectory(Path.Combine(scratch, "script-cache")));
        var connections = new RunLoginConnections(config, engine.Drivers, engine.Libraries, repoRoot, secrets);
        var outputs = new RunOutputMappingAfterApply(
            columns => Record(ProvisionSteps.ProvisionedColumns, columns),
            renames => Record(ProvisionSteps.RenamesApplied, renames));
        var provisioner = new MappingProvisioner(config, connections, outputs,
            new MappingColumnReader(new RunLoginColumnCatalog(connections, new ScriptedMetadata(engine.Scripts, config))), engine.Drivers);

        ProvisionReport report;
        if (Input.Mapping.Length > 0)
        {
            if (Input.Action.Length == 0)
                throw new TaskFailureException(TaskFailureKind.Permanent, $"The provisioning of '{Input.Replication}/{Input.Mapping}' names no action: one of the Setup card's actions is asked for.");
            var result = await provisioner.ApplyAsync(Input.Replication, Input.Mapping, Input.Action, ct).ConfigureAwait(false);
            report = new ProvisionReport(Input.Replication, Input.Mapping, [.. result.Steps.Select(s => new ProvisionStepReport(s.Title, s.Succeeded, s.Error))]);
        }
        else
        {
            var ids = Input.Steps.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (ids.Length == 0)
                throw new TaskFailureException(TaskFailureKind.Permanent, $"The provisioning of '{Input.Replication}' chose no step of the replication's plan.");
            var result = await provisioner.ApplyReplicationPlanAsync(Input.Replication, ids, ct).ConfigureAwait(false);
            report = new ProvisionReport(Input.Replication, "", [.. result.Steps.Select(s => new ProvisionStepReport(s.Title, s.Outcome is ProvisioningStepOutcome.Applied or ProvisioningStepOutcome.NoLongerNeeded, s.Error, s.Id, s.Outcome.ToString()))]);
        }

        Record(ProvisionSteps.Applied, report);
        if (report.Steps.FirstOrDefault(s => !s.Succeeded) is { } failed)
            throw new TaskFailureException(TaskFailureKind.Permanent, $"The step '{failed.Title}' of the provisioning of '{Input.Replication}' failed: {failed.Error}", Input.Mapping.Length > 0 ? Input.Mapping : null);
    }

    private void Record<T>(ValueStep<T, T> step, T value) =>
        Step(step, value).Repeatable().RunAsync(_ => Task.FromResult(value), CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>Every connection with its run login (the slot the runner bound), whatever role the provisioner names: a runner holds no other login.</summary>
    private sealed class RunLoginConnections(ConfigRepository config, DriverRegistry drivers, LibraryRegistry libraries, string repoRoot, SecretStore secrets) : IConnectionFactory
    {
        public async Task<(DbConnection Connection, IDriver Driver)> OpenAsync(string connectionName, CredentialRole role, CancellationToken cancellationToken)
        {
            var connection = config.LoadConnection(connectionName);
            var driver = drivers.Get(connection.DriverType);
            return (await RunExecutor.OpenWithRunLoginAsync(connection, driver, libraries, repoRoot, secrets, cancellationToken).ConfigureAwait(false), driver);
        }
    }

    /// <summary>The catalog a provisioner reads a target back from: the connection's metadata script if one is bound, else the driver, as the Sync server's metadata service reads it.</summary>
    private sealed class RunLoginColumnCatalog(IConnectionFactory connections, ScriptedMetadata metadata) : IColumnCatalog
    {
        public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(string connectionName, string database, string schema, string table, CancellationToken cancellationToken)
        {
            var (connection, driver) = await connections.OpenAsync(connectionName, CredentialRole.Run, cancellationToken).ConfigureAwait(false);
            await using (connection)
                return await metadata.ListColumnsAsync(connectionName, connection, driver, (IScriptDialect?)ScriptDialectAdapter.For(driver) ?? DialectlessScriptDialect.Instance, database, schema, table, cancellationToken).ConfigureAwait(false);
        }
    }
}
