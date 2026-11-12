using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The slots of the serving kinds (plan 33 SW01, plan 30 PB02): a model names the data sources it needs in its manifest (<c>slots</c>), the group binds each slot once in a channel, and that binding takes the place of the per-publish <c>adopt</c>
/// (SC21f) for a data source the publishing client did not make. The binding is a row of the shared API's bindings (who bound it and when are recorded, and the shared API audits it as <c>definition.bind</c>), so it is the group's recorded consent,
/// not the publisher's. Rules: a slot is bound to the data source of the same name (the model serves the data source its source names, so another name would bind to something the model does not read; mapping a slot to a differently named data source
/// is not built); the data source must be one of this Gateway's; a publishing client binds only a data source it made, and a data source made by hand (or by an administrator) is bound by an administrator; a data source another client made is bound by nobody
/// (an administrator hands it over first, <c>POST definitions/data-source/{name}/adopt</c>).
/// </summary>
internal static class ServingSlots
{
    /// <summary>The slots a version declares must be data sources it uses: a slot nothing reads would bind consent for nothing, and a typo would let the real data source through unbound.</summary>
    public static void CheckDeclared(IReadOnlyList<string>? slots, IEnumerable<string> used)
    {
        if (slots is null) return;
        var known = used.ToHashSet(StringComparer.Ordinal);
        foreach (var slot in slots)
            if (!known.Contains(slot))
                throw new DefinitionsRefusalException(400, $"The manifest declares the slot '{slot}', and nothing in the version reads that data source ({(known.Count == 0 ? "it names none" : string.Join(", ", known.Order(StringComparer.Ordinal).Select(k => "'" + k + "'")))}): a slot is named by a data source the version uses.");
    }

    /// <summary>Whether the version declares the data source as a slot.</summary>
    public static bool Declared(IReadOnlyList<string>? slots, string dataSource) => slots?.Contains(dataSource, StringComparer.Ordinal) == true;

    /// <summary>Whether the version declares the data source as a slot and the channel binds it (a binding to anything is the group's consent: the binding rules allow only the data source of the same name).</summary>
    public static bool Bound(IReadOnlyList<string>? slots, PublishHookContext context, string dataSource) => Declared(slots, dataSource) && context.BoundTo?.Invoke(dataSource) is not null;

    /// <summary>The checks around binding slots of a model, for a binding through the shared API.</summary>
    public static void CheckBind(BindingHookContext context, Func<GatewayOptions> options, Func<string, string?> ownerOfDataSource)
    {
        foreach (var (slot, value) in context.Slots.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (!string.Equals(slot, value, StringComparison.Ordinal))
                throw new DefinitionsRefusalException(400, $"The slot '{slot}' of a {context.Kind} is bound to the data source of the same name: bind it as {{\"slots\": {{\"{slot}\": \"{slot}\"}}}}. The model serves the data source its source names, so a binding to '{value}' would name something the model does not read.");
            if (!options().DataSources.ContainsKey(value))
                throw new DefinitionsRefusalException(400, $"'{value}' is not a data source of this server; a slot is bound to a data source the Gateway has.");
            var owner = ownerOfDataSource(value);
            var own = !string.IsNullOrEmpty(owner) && owner == context.Caller.ClientId;
            if (own) continue;
            if (!string.IsNullOrEmpty(owner))
                throw new DefinitionsRefusalException(403, $"'{value}' was made by another server client ({owner}), and a slot is not bound to another client's data source: an administrator hands it over first (POST definitions/data-source/{value}/adopt).");
            if (!context.Caller.Admin)
                throw new DefinitionsRefusalException(403, $"'{value}' was made by hand or by an administrator, not by this client, and a publishing client binds only the data sources it made. An administrator binds it once, for this {context.Kind}: "
                    + $"PUT /api/v1/bindings/{context.Channel}/{context.Kind}/{context.Name} {{\"slots\": {{\"{slot}\": \"{slot}\"}}}} with the admin role; the Gateway records who bound it, and a version that names the slot is then served over it without adopt.");
        }
    }

    /// <summary>The audit text of a data source used through a slot binding.</summary>
    public static string UseNote(string client, string person, string model) => $"used by {client} ({person}) for the model {model} through its slot binding";
}
