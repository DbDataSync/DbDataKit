using DbDataKit.DataModel.Storage;
using DbDataKit.Lineage;
using DbDataKit.Serving.Notify;
using DbDataKit.Store;
using DbDataKit.Web.Trust;

namespace DbDataStore.Server;

/// <summary>
/// The Store's half of the contract guard (plan 21 CH06, plan 26 Z13b): before the contract step drops a column of a copy of a model, the lineage of every serving group this server is paired with is asked who reads it, in any channel
/// (<see cref="GatewayLineageClient"/>, <see cref="ContractGuard"/>). A column that a dashboard, a report, an app or a semantic model's member reads is refused by name and channel until the consumer is published without it, or the administrator allows it
/// by name (<c>allow_consumer=&lt;name&gt;@&lt;channel&gt;</c>, written in the management audit with who allowed what). With no pairing there is nothing to ask. A group that does not answer refuses the step too (<c>gateway:&lt;name&gt;</c> allows it).
/// </summary>
public sealed class StoreContractGuard(GatewayLineageClient lineage, StoreConnections connections, IAuditLog audit)
{
    public static StoreContractGuard Create(string directory, StoreConnections connections, IAuditLog audit, Func<GatewayPairing, HttpMessageHandler>? handler = null) =>
        new(new GatewayLineageClient(new StoreDataChanges.FromProxies(new StoreProxies(directory)), StoreDataChanges.IdentityOf(directory), product: "store", handler: handler), connections, audit);

    /// <summary>
    /// The refusals for the drop steps of <paramref name="plan"/> in the copy at <paramref name="connection"/>, schema <paramref name="schema"/> (empty when the plan may run). A step that makes a column required reads nothing away and is not asked about.
    /// The allowances that were used are audited here, so a refusal that was allowed leaves who allowed it.
    /// </summary>
    public async Task<IReadOnlyList<string>> CheckAsync(ContractPlan plan, string connection, string schema, IReadOnlyCollection<string> allowed, ValidatedToken caller, CancellationToken ct)
    {
        var drops = plan.Steps.Where(s => s.DroppedColumn is not null && s.Table is not null).ToList();
        if (drops.Count == 0) return [];
        var (address, problem) = await connections.AddressOnlyAsync(connection, ct).ConfigureAwait(false);
        if (address is null) return [$"Cannot check who reads the columns this step drops: {problem} Nothing was changed."];
        var changes = drops.Select(s =>
        {
            var dataset = LineageIdentity.Dataset(address, schema, s.Table!);
            return new ContractedColumn($"drop the column '{s.DroppedColumn}' of '{schema}.{s.Table}'", LineageIdentity.Column(address, dataset, s.DroppedColumn!), dataset.ToString());
        }).ToList();
        var fetched = await lineage.FetchAsync(ct).ConfigureAwait(false);
        var verdict = ContractGuard.Check(changes, fetched.Graphs, fetched.Unreachable, allowed, "allow_consumer=");
        if (verdict.Allowed)
            foreach (var used in verdict.AllowancesUsed)
                audit.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), "command", "dbdatastore server", caller.SubjectId, caller.SubjectName ?? caller.SubjectId, false, "model.contract.allow-consumer", $"{schema}: {used}", "ok", ""));
        return verdict.Refusals;
    }
}
