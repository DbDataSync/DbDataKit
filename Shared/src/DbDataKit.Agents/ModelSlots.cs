using System.Globalization;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Agents;

/// <summary>One choice of a slot: a provider and a model of it.</summary>
public sealed record SlotChoice(string Provider, string Model, int Line, int Column);

/// <summary>
/// What each model slot is bound to (plan 27a AG05): per slot and per channel an ordered list of provider and model, the first the primary and the others the fallback in order (AG21). The channel <c>*</c> binds every channel that has no
/// binding of its own. A slot with nothing bound is unbound, and a promotion that needs it is refused naming it.
/// </summary>
public sealed record SlotBindings(IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SlotChoice>>> Slots)
{
    public const string AnyChannel = "*";

    public static readonly SlotBindings None = new(new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SlotChoice>>>());

    /// <summary>The ordered choices for a slot in a channel (a channel's own binding, else the one for every channel); empty when the slot is unbound there.</summary>
    public IReadOnlyList<SlotChoice> For(string slot, string? channel)
    {
        if (!Slots.TryGetValue(slot, out var channels)) return [];
        if (channel is not null && channels.TryGetValue(channel, out var own)) return own;
        return channels.TryGetValue(AnyChannel, out var any) ? any : [];
    }

    /// <summary>The slots among <paramref name="needed"/> that have nothing bound in <paramref name="channel"/>, for a promotion to be refused naming them.</summary>
    public IReadOnlyList<string> Unbound(IEnumerable<string> needed, string? channel) => [.. needed.Distinct().Where(s => For(s, channel).Count == 0).Order()];
}

public sealed class SlotBindingsResult(SlotBindings? bindings, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public SlotBindings? Bindings { get; } = bindings;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Bindings is not null && !Diagnostics.HasErrors();
}

/// <summary>
/// Strict reader of <c>model-slots/1</c> (plan 27a AG05): <c>slots: { default: { "*": [ { provider: anthropic-main, model: ... } ], test: [ ... ] } }</c>. Checked against the providers that exist by <see cref="Check"/>. Codes: <c>DBD-AGT-032</c> a value,
/// <c>-033</c> the format number, <c>-034</c> a provider or model that does not exist or is not for chat.
/// </summary>
public static class SlotBindingsLoader
{
    public const string BadSlots = "DBD-AGT-032";
    public const string FormatNumber = "DBD-AGT-033";
    public const string UnknownChoice = "DBD-AGT-034";
    public const int Version = 1;
    public const string Kind = "model-slots";

    public static string Refusal(string file, int format) => format > Version
        ? $"the {Kind} definition '{file}' is in format {format}, and this program reads format {Version} of {Kind}. Upgrade this program; the file was written by a newer version."
        : $"the {Kind} definition '{file}' is in format {format}, which this program does not read (it reads format {Version} of {Kind}).";

    public static SlotBindingsResult Load(string yaml, string file)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml.TrimStart('﻿'), file, diags);
        if (root is null && !diags.HasErrors()) return new(SlotBindings.None, diags);   // an empty file binds nothing
        if (root is null || diags.HasErrors()) return new(null, diags);
        var bindings = new Reader(file, diags).Read(root);
        return new(diags.HasErrors() ? null : bindings, diags);
    }

    /// <summary>The problems of choosing a provider or model that is not there, with the line where the file names it.</summary>
    public static IReadOnlyList<ConfigDiagnostic> Check(SlotBindings bindings, IReadOnlyDictionary<string, ModelProviderDefinition> providers, string file)
    {
        var problems = new List<ConfigDiagnostic>();
        foreach (var (slot, channels) in bindings.Slots)
            foreach (var (channel, choices) in channels)
                foreach (var c in choices)
                {
                    string? message = null, fix = null;
                    if (!providers.TryGetValue(c.Provider, out var provider)) { message = $"Slot `{slot}` ({channel}) names the provider `{c.Provider}`, which does not exist."; fix = providers.Count > 0 ? $"Providers: {string.Join(", ", providers.Keys.Order())}." : "Add a provider first."; }
                    else if (provider.Purpose != "chat") { message = $"Slot `{slot}` ({channel}) names `{c.Provider}`, which is for {provider.Purpose}, not chat."; }
                    else if (provider.Model(c.Model) is null) { message = $"Slot `{slot}` ({channel}) names the model `{c.Model}`, which `{c.Provider}` does not allow."; fix = $"It allows: {string.Join(", ", provider.Models.Select(m => m.Id))}."; }
                    if (message is not null) problems.Add(DefaultDiagnosticCatalog.Instance.Create(UnknownChoice, file, c.Line, c.Column, message, fix));
                }
        return problems;
    }

    private sealed class Reader(string fileName, List<ConfigDiagnostic> diags) : AgentFileReader(fileName, diags, BadSlots)
    {
        public SlotBindings? Read(YamlNode root)
        {
            if (root is not YamlMapping map) { Bad(root, "The slots file is a mapping with `model-slots: 1` and `slots`."); return null; }
            var format = map.Get("model-slots");
            if (format is null) { Add(FormatNumber, map, $"`model-slots` is missing: the file says which format of {Kind} it is in.", "Add `model-slots: 1`."); return null; }
            if (format is not YamlScalar { Value: var text } || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            { Add(FormatNumber, format, "`model-slots` must be a positive whole number."); return null; }
            if (number != Version) { Add(FormatNumber, format, Refusal(File, number), number > Version ? $"Upgrade the tool, or write the file in format {Version}." : null); return null; }
            CheckKeys(map, ["model-slots", "slots"], "the slots file");

            var slots = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<SlotChoice>>>(StringComparer.Ordinal);
            if (map.Get("slots") is not { } node) return SlotBindings.None;
            if (node is not YamlMapping all) { Bad(node, "`slots` maps a slot name to its channels: `slots: { default: { \"*\": [ { provider: ..., model: ... } ] } }`."); return null; }
            foreach (var slot in all.Entries)
            {
                if (!AgentDefinitionLoader.IsName(slot.Key.Value)) { Bad(slot.Key, $"`{slot.Key.Value}` is not a slot name: lower-case letters, digits and '-'."); continue; }
                if (slot.Value is not YamlMapping channels) { Bad(slot.Value, $"Slot `{slot.Key.Value}` maps each channel (or `*` for every channel) to an ordered list of provider and model."); continue; }
                var perChannel = new Dictionary<string, IReadOnlyList<SlotChoice>>(StringComparer.Ordinal);
                foreach (var channel in channels.Entries)
                {
                    if (channel.Value is not YamlSequence list || list.Items.Count == 0) { Bad(channel.Value, $"Channel `{channel.Key.Value}` is a list of at least one `{{ provider: ..., model: ... }}`; the first is the primary, the others the fallback in order."); continue; }
                    var choices = new List<SlotChoice>();
                    foreach (var item in list.Items)
                    {
                        if (item is not YamlMapping c) { Bad(item, "A choice is `{ provider: <name>, model: <id> }`."); continue; }
                        CheckKeys(c, ["provider", "model"], "a choice");
                        var provider = Scalar(c, "provider", required: true, c);
                        var model = Scalar(c, "model", required: true, c);
                        if (provider is not null && model is not null) choices.Add(new SlotChoice(provider.Value, model.Value, provider.Line, provider.Column));
                    }
                    perChannel[channel.Key.Value] = choices;
                }
                slots[slot.Key.Value] = perChannel;
            }
            return new SlotBindings(slots);
        }
    }
}
