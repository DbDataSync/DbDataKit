using System.Data.Common;
using DbDataKit.Access;
using DbDataKit.Identity;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataKit.Store;
using DbDataGateway.Server.State;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The row rules of an entity of a published Store model, as the group's reads apply them (plan 22a GP11, QG2): the same resolver, rules-table ledger, include and exclude semantics, strict values and refusal of an expired table as the semantic path
/// (<see cref="GatewayAccess"/>), put in the terms of the Store's own SQL by <see cref="ModelWriter.ReadAccess"/>. The rules table is a table (<c>schema.table</c>) of the model's own connection, so the filter is a semi-join in the database that
/// holds the entity; nobody is exempt (QR3), and a person with no matching rule sees no rows. Rules limit reads only (QR6): a write never asks this.
/// </summary>
public sealed class StoreEntityAccess(IRulesTableLedger ledger, Func<string, IReadOnlyList<string>> groupsOf, IAccessAlerts? alerts = null, TimeProvider? time = null)
{
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<StateDatabase, StoreEntityAccess> PerState = new();

    /// <summary>The one instance of a state in this process (the store module, the apps host, the views module and the tools of an agent all read through it, so they share the resolver's memory of each rules table's names and its alert).</summary>
    public static StoreEntityAccess For(StateDatabase state, TimeProvider? time = null) =>
        PerState.GetValue(state, s => new StoreEntityAccess(RulesTableLedger.For(s, time), userId => [.. new HomeDirectory(s).GroupsOf(userId).Select(g => g.Name)], new Alerts(new StateAuditLog(s), time), time));

    /// <summary>The resolver (it keeps the names of each rules table per generation of the table).</summary>
    public AccessResolver Resolver { get; } = new(ledger, alerts, time);

    /// <summary>The identity of a rules table in the state: the data source and the table as the model names it, the same key the semantic path gives a table of the same name on the same data source.</summary>
    public static string TableKey(string dataSource, string table) => dataSource + "/" + DbDataKit.Semantic.RowAccessPlan.Normalize(table);

    /// <summary>Whether reads of this entity are limited by row rules.</summary>
    public static bool Limits(ServedEntityModel served, string entity) => served.Model.Access?.RowRulesOf(entity) is not null;

    /// <summary>
    /// The row rules of <paramref name="caller"/> on each ruled entity of the model, resolved now (before any transaction of the writer: the names of a rules table are read on <paramref name="writer"/>'s connection, once per generation of the table).
    /// A rules table that cannot be read refuses its entity (D2); it never leaves the entity open.
    /// </summary>
    public Func<string, EntityReadAccess?> For(ServedEntityModel served, StoreCaller caller, ModelWriter writer)
    {
        if (served.Model.Access is not { IsEmpty: false } section) return _ => null;
        var groups = caller.Groups ?? groupsOf(caller.UserId);
        var decision = Resolver.Resolve(new AccessCaller(caller.UserId, caller.Name ?? caller.UserId, groups),
            new AccessScope("entity-model/" + served.Model.Name, section, table => TableKey(served.DataSource, table), new Subjects(writer)));
        var found = new Dictionary<string, EntityReadAccess>(StringComparer.Ordinal);
        foreach (var dataset in decision.Datasets) found[dataset.Dataset] = new EntityReadAccess(RowPredicate.Of(dataset), dataset.Refusal);
        return entity => found.GetValueOrDefault(entity);
    }

    private sealed class Subjects(ModelWriter writer) : IRulesSubjects
    {
        public IReadOnlyCollection<string> Names(string table, string column) => writer.RulesSubjects(table, column);
    }

    private sealed class Alerts(IAuditLog audit, TimeProvider? time) : IAccessAlerts
    {
        public void RulesTableStale(RulesTableStatus status)
        {
            var message = $"The rules table '{status.Table}' ({status.Key}) was last refreshed {status.Age.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} minutes ago, past half of its maximum age of {status.MaxAge.TotalMinutes.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} minutes: the last good rules are served and, past the maximum, the entities they filter are refused.";
            try { audit.Append(new AuditEntry((time ?? TimeProvider.System).GetUtcNow(), "local", "access rules", "access-rules", "access rules", false, "access rules stale", status.Key, message, "")); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { /* an alert that cannot be written is not a reason to refuse a read */ }
        }
    }
}
