using DbDataKit.Connections;
using DbDataKit.State;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// The secret slots of a <c>data-source</c> (plan 30 PB02, owner decision of 2026-11-16): one per secret reference the file names (<see cref="ConnectionSecretSlots"/>), for every data source whoever made it (by hand, by the per-kind route or by the shared one).
/// A channel binds each slot to the secret of the same name the Gateway holds (a binding names a secret, never its value; the value is still pushed with <c>PUT /api/v1/secrets/{name}</c>), and a version is refused while its channel has no binding
/// for a slot: a publish, a promotion, an activation, and a write through the per-kind route. What a channel serves already keeps serving. A slot is a write slot: a channel never borrows production's binding of a secret.
/// </summary>
public static class DataSourceSlots
{
    /// <summary>The slots of a data source's file, for the registry.</summary>
    public static IReadOnlyList<DefinitionSlot> Of(string text) =>
        [.. ConnectionSecretSlots.OfText(text).Select(s => new DefinitionSlot(s.Slot, $"the secret its {s.Path} names ({s.Reference})", SlotAccess.Write))];

    /// <summary>The call that binds the slots, as a refusal names it.</summary>
    public static string BindCall(string name, string channel, IReadOnlyList<string> slots) =>
        $"PUT /api/v1/bindings/{channel}/data-source/{name} {{\"slots\": {{{string.Join(", ", slots.Select(s => $"\"{s}\": \"{s}\""))}}}}}";

    /// <summary>What a refusal for unbound slots says after naming them: how to bind them, where, and who may.</summary>
    public static string HowToBind(string name, string channel, IReadOnlyList<string> slots) =>
        $"Every data source binds the secrets its file names, in each channel, before a version of it is served there (one made by hand or published before included). Bind {(slots.Count == 1 ? "it" : "them")} to the secret of the same name the Gateway holds: "
        + $"on the Gateway's Data sources screen (the Secret slots box of the data source, an administrator), or with {BindCall(name, channel, slots)} (an administrator binds any secret, a publishing client the secrets it stored). "
        + "A secret the Gateway does not hold yet is stored first (PUT /api/v1/secrets/{name}, or the Secrets screen). What the channel serves now keeps serving.";

    /// <summary>The refusal of a data source whose file names secrets its channel has not bound, in the registry's words (the per-kind route says it without a version number).</summary>
    public static string Refusal(string name, string channel, IReadOnlyList<string> unbound) =>
        $"The data-source '{name}' needs {(unbound.Count == 1 ? "the slot" : "the slots")} {string.Join(", ", unbound.Select(s => $"'{s}'"))} bound in channel '{channel}', and {(unbound.Count == 1 ? "it has" : "they have")} no binding there. "
        + HowToBind(name, channel, unbound);
}
