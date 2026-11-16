using System.Text.Json.Nodes;
using DbDataKit.Connections;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The secret slots of the data sources (plan 30 PB02, owner decision of 2026-11-16; <see cref="DataSourceSlots"/>): the per-kind route refuses a write whose file names a secret production has not bound, in the registry's words, with the slots in the answer
/// (<c>code</c> <c>unboundslot</c>), and its read shows each slot of the file served with its binding; the binding rules of the shared route's <c>PUT /api/v1/bindings/{channel}/data-source/{name}</c>.
/// </summary>
public sealed partial class GatewayDataSources
{
    /// <summary>The channel the per-kind routes publish in.</summary>
    internal const string Channel = DefinitionStore.Production;

    private const string Kind = DbDataGateway.Server.Serving.PublishedDataSources.DefinitionKind;

    private IReadOnlyDictionary<string, DefinitionBinding> BindingsOf(string name, string channel = Channel) =>
        registry?.Bindings(channel, Kind, name).ToDictionary(b => b.Slot, StringComparer.Ordinal) ?? new Dictionary<string, DefinitionBinding>();

    private bool Held(string slot) => ConnectionSecretSlots.ReferenceOf(slot) is not { } reference || secretHeld?.Invoke(reference.ToString()) != false;

    /// <summary>Each secret slot of the file with production's binding of it, who bound it and when, and whether the Gateway holds the secret, for the data source's screen.</summary>
    internal JsonArray SecretSlotsView(string name, string text)
    {
        var bound = BindingsOf(name);
        return new JsonArray([.. ConnectionSecretSlots.OfText(text, name).Select(s => (JsonNode)SlotRow(s, bound.GetValueOrDefault(s.Slot)))]);
    }

    private JsonObject SlotRow(SecretSlot slot, DefinitionBinding? binding) => new()
    {
        ["slot"] = slot.Slot, ["path"] = slot.Path, ["reference"] = slot.Reference, ["channel"] = Channel, ["stored"] = slot.IsStored,
        ["held"] = ConnectionSecretSlots.ReferenceOf(slot.Slot) is null ? null : Held(slot.Slot),
        ["bound_to"] = binding?.BoundTo, ["bound_by"] = binding?.BoundBy, ["bound_at"] = binding?.BoundAt.UtcDateTime.ToString("O"), ["revision"] = binding?.Revision,
    };

    /// <summary>The refusal of a write through the per-kind route whose file names a secret production has not bound; null when every slot is bound (or the file names none).</summary>
    private ManagementResult? UnboundSlots(string name, string text)
    {
        if (registry is null) return null;
        var slots = ConnectionSecretSlots.OfText(text, name);
        if (slots.Count == 0) return null;
        var bound = BindingsOf(name);
        var unbound = slots.Where(s => !bound.ContainsKey(s.Slot)).ToList();
        if (unbound.Count == 0) return null;
        var names = unbound.Select(s => s.Slot).ToList();
        return new ManagementResult(409, new JsonObject
        {
            ["error"] = DataSourceSlots.Refusal(name, Channel, names), ["code"] = "unboundslot", ["channel"] = Channel,
            ["slots"] = new JsonArray([.. unbound.Select(s => (JsonNode)SlotRow(s, null))]), ["bind"] = DataSourceSlots.BindCall(name, Channel, names),
        });
    }

    /// <summary>
    /// The rules of a binding of a data source's slots through the shared route: a slot is bound to the secret of the same name (the file names the secret the Gateway reads, so another name would bind consent to something it does not read; mapping a slot
    /// to a differently named secret is not built); an administrator binds any secret, a publishing client only the secrets it stored itself (<c>PUT /api/v1/secrets/{name}</c> keeps them under <c>dbdatakit:gateway:managed-&lt;client&gt;:</c>); and the Gateway
    /// must hold the secret (a stored secret, a variable of its environment, a file it reads). An inline DPAPI value carries itself in the file, so it is bound without that check.
    /// </summary>
    internal void CheckSharedBind(BindingHookContext context)
    {
        foreach (var (slot, value) in context.Slots.OrderBy(s => s.Key, StringComparer.Ordinal))
        {
            if (!string.Equals(slot, value, StringComparison.Ordinal))
                throw new DefinitionsRefusalException(400, $"The slot '{slot}' of a data source is bound to the secret of the same name: bind it as {{\"slots\": {{\"{slot}\": \"{slot}\"}}}}. The data source's file names the secret the Gateway reads, so a binding to '{value}' would name something it does not read.");
            var reference = ConnectionSecretSlots.ReferenceOf(slot);
            if (!context.Caller.Admin)
            {
                var own = context.Caller.Context is ManagementCaller caller ? $"dbdatakit:gateway:{GatewaySecrets.KindOf(caller)}:" : null;
                if (own is null || reference is not { Kind: SecretRefKind.Secret } || !slot.StartsWith(own, StringComparison.Ordinal))
                    throw new DefinitionsRefusalException(403, $"'{slot}' is not a secret this client stored, and a publishing client binds only its own (the ones it stored with PUT /api/v1/secrets/{{name}}{(own is null ? "" : $", named {own}<name>")}). "
                        + $"An administrator binds it: on the Gateway's Data sources screen (the Secret slots box of '{context.Name}'), or with {DataSourceSlots.BindCall(context.Name, context.Channel, [slot])} with the admin role.");
            }
            if (reference is not null && !Held(slot))
                throw new DefinitionsRefusalException(409, $"The Gateway holds no secret '{slot}' ({reference.ToAuditString()}), so the slot is not bound to it. "
                    + (reference.Kind == SecretRefKind.Secret ? "Store its value first (PUT /api/v1/secrets/{name}, or the Secrets screen), then bind it." : "Set it where the Gateway reads it (its environment, or the file), then bind it."));
        }
    }
}
