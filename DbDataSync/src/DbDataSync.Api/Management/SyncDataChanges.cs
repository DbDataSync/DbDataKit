using DbDataKit.Connections;
using DbDataKit.Lineage;
using DbDataKit.Secrets;
using DbDataKit.Serving.Notify;
using DbDataKit.Web.Trust;
using DbDataSync.Core.Config;
using DbDataSync.Core.Lineage;
using DbDataSync.State;
using Microsoft.Extensions.Logging;

namespace DbDataSync.Api.Management;

/// <summary>
/// Sync's producer of <c>data.changed/1</c> (plan 24a UC13): when a run of a replication ends having written rows to its target, the target tables of the mapping are datasets that changed, and the serving groups this server is paired with
/// (<c>gateways.json</c> in the repository root; <c>gateway add</c> or the console's Link) are told. The server signs as itself (<see cref="DataChangeNotifier"/>). The tables are named as <see cref="SyncLineage"/> names them in the graph Sync sends,
/// so they are the names a Gateway's lineage graph holds. A run that wrote nothing, a verification and a run that was cancelled tell no one; a failed run that wrote rows does (stale rows are worse than a cold cache); no pairing sends nothing.
/// </summary>
public sealed class SyncDataChanges(ConfigRepository config, GatewayPairingsFile pairings, IDataChangeNotifier notifier, ILogger<SyncDataChanges> log)
{
    public const string Product = "sync";
    public const string FileName = "gateways.json";

    public GatewayPairingsFile Pairings => pairings;

    public IDataChangeNotifier Notifier => notifier;

    public static GatewayPairingsFile PairingsIn(string repoRoot) => new(Path.Combine(repoRoot, FileName));

    /// <summary>The server's own identity in its secret store, the key the pairings were made with.</summary>
    public static ConsoleKeyStore IdentityIn(DbDataSync.Core.Secrets.SecretStore secrets) =>
        secrets.Writable is { } store && store is ISecretProvider provider ? new ConsoleKeyStore(store, provider) : throw new InvalidOperationException("This server has no secret store to keep its own key in, so it cannot pair with a gateway.");

    /// <summary>Whether a finished run changed rows at its target (the kinds that write, ended in success or in failure after rows went in).</summary>
    internal static bool Changed(TaskRunRecord run) =>
        run.RunKind is RunKind.Primary or RunKind.BulkLoad or RunKind.ReconcileDeletes && run.Status is RunStatus.Succeeded or RunStatus.Failed && run.RowsWritten > 0;

    /// <summary>The datasets (lineage identities of the target tables) of the mapping a run belongs to.</summary>
    internal IReadOnlyList<string> TargetsOf(string replication, string mapping)
    {
        var task = config.LoadReplicationTask(replication);
        var table = config.LoadTableMapping(replication, mapping);
        var connections = config.LoadAllConnections().ToDictionary(c => c.Name, StringComparer.Ordinal);
        var datasets = new List<string>();
        foreach (var target in table.Targets)
        {
            var name = target.ConnectionName ?? task.Endpoints.Target?.ConnectionName;
            if (name is null || !connections.TryGetValue(name, out var connection)) continue;
            datasets.Add(LineageIdentity.Dataset(SyncLineage.DefinitionOf(connection, target.Database ?? task.Endpoints.Target?.Database), target.Schema, target.Table).ToString());
        }

        return datasets;
    }

    /// <summary>A run has ended. Never throws and never waits for the network.</summary>
    public void RunCompleted(TaskRunRecord run)
    {
        try
        {
            if (!Changed(run) || pairings.All().Count == 0) return;
            var datasets = TargetsOf(run.TaskName, run.MappingName);
            if (datasets.Count == 0) return;
            notifier.Publish(datasets.Select(d => new DataChange(d, run.RunId.ToString(), run.EndedAtUtc)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // the run is over and its result stands: not being able to name its tables is a line in the log
            log.LogWarning("The serving groups were not told what run {Run} of {Replication} changed ({Type}).", run.RunId, run.TaskName, ex.GetType().Name);
        }
    }
}
