using System.Globalization;
using System.Text;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections.Bindings;

/// <summary>
/// The strict reader, the checker and the writer of <c>bindings/1</c> (<c>bindings.yml</c> in the console's directory; plan 27a IX23, <c>docs/channel-bindings.md</c>): which connection each named slot uses in each release channel, for model slots and delivery channels.
/// <code>
/// bindings: 1
/// models:                                   # slot name -> release channel -> an ordered list of { connection, model }
///   default:
///     prod:  [ { connection: anthropic-main, model: claude-sonnet }, { connection: ollama-local, model: llama3.1:8b } ]   # primary, then fallback
///     "*":   [ { connection: ollama-local, model: llama3.1:8b } ]                                                       # every release channel without its own, on purpose
///   embeddings:
///     prod: { connection: ollama-local, model: nomic-embed-text }
/// delivery:                                 # delivery channel name -> release channel -> one connection
///   mail: smtp-main                         # a name alone binds prod only
///   alerts: { prod: slack-ops, test: slack-sandbox }
/// </code>
/// One rule for every slot: a release channel's own entry, else the <c>*</c> entry if one is written, else unbound; never another release channel's entry. Codes: <c>DBD-BIND-001</c> a value, <c>-002</c> the format number,
/// <c>-003</c> a key of an earlier form, <c>-004</c> a connection or model that does not exist or does not fit the slot (<see cref="Check"/>).
/// </summary>
public static class BindingsLoader
{
    public const string BadValue = "DBD-BIND-001";
    public const string FormatNumber = "DBD-BIND-002";
    public const string EarlierForm = "DBD-BIND-003";
    public const string UnknownChoice = "DBD-BIND-004";
    public const int Version = 1;
    public const string Kind = "bindings";
    public const string FileName = "bindings.yml";

    /// <summary>The model slot that binds a channel's knowledge index to an embedding model (plan 27a KN05, KN08). Every other slot of <c>models:</c> is for chat.</summary>
    public const string EmbeddingsSlot = "embeddings";

    public static SlotKind ModelKindOf(string slot) => slot == EmbeddingsSlot ? SlotKind.ModelEmbeddings : SlotKind.ModelChat;

    /// <summary>What the connections of a slot are for: <c>embeddings</c>, <c>chat</c>, or a delivery channel's <c>send</c>.</summary>
    public static string PurposeOf(SlotKind kind) => kind switch { SlotKind.ModelEmbeddings => "embeddings", SlotKind.ModelChat => "chat", _ => "send" };

    public static string Refusal(string file, int format) => format > Version
        ? $"the {Kind} definition '{file}' is in format {format}, and this program reads format {Version} of {Kind}. Upgrade this program; the file was written by a newer version."
        : $"the {Kind} definition '{file}' is in format {format}, which this program does not read (it reads format {Version} of {Kind}).";

    /// <summary>Whether a name is one a slot or a release channel can have: letters, digits, <c>-</c> and <c>_</c>, 1 to 64 (a release channel may also be <c>*</c>).</summary>
    public static bool NameOk(string name) => name.Length is >= 1 and <= 64 && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static BindingsResult Load(string yaml, string file)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml.TrimStart('﻿'), file, diags);
        if (root is null && !diags.HasErrors()) return new(BindingSet.Empty, diags);   // an empty file binds nothing
        if (root is null || diags.HasErrors()) return new(null, diags);
        var set = new Reader(file, diags).Read(root);
        return new(diags.HasErrors() ? null : set, diags);
    }

    /// <summary>The problems of naming a connection or model that is not there, or does not fit the slot, with the line where the file names it.</summary>
    public static IReadOnlyList<ConfigDiagnostic> Check(BindingSet set, IBindingTargets targets, string file)
    {
        var problems = new List<ConfigDiagnostic>();
        foreach (var slot in set.Slots)
            foreach (var (channel, entries) in slot.PerChannel)
                foreach (var e in entries)
                    if (Why(slot, channel, e, targets) is { } why)
                        problems.Add(DefaultDiagnosticCatalog.Instance.Create(UnknownChoice, file, e.Line, e.Column, why.Message, why.Fix));
        return problems;
    }

    /// <summary>Whether a connection (and model) fits the slot, as the reason in words when it does not; null when it does.</summary>
    public static (string Message, string? Fix)? Why(SlotBinding slot, string channel, BindingEntry e, IBindingTargets targets)
    {
        var where = $"{SlotBinding.Describe(slot.Kind)} `{slot.Name}` ({channel})";
        if (targets.Find(e.Connection) is not { } t)
        {
            var wanted = targets.All.Where(x => Fits(slot.Kind, x)).Select(x => x.Name).Order(StringComparer.Ordinal).ToList();
            return ($"The {where} names the connection `{e.Connection}`, which does not exist.", wanted.Count > 0 ? $"Connections that fit: {string.Join(", ", wanted)}." : "Add a connection first.");
        }

        switch (slot.Kind)
        {
            case SlotKind.ModelChat or SlotKind.ModelEmbeddings:
                var purpose = PurposeOf(slot.Kind);
                if (t.Kind != ConnectionKinds.ModelProvider) return ($"The {where} names `{e.Connection}`, which is a connection of kind `{t.Kind}`, not a model provider.", null);
                if ((t.Purpose ?? "chat") != purpose) return ($"The {where} names `{e.Connection}`, which is for {t.Purpose ?? "chat"}, not {purpose}.", null);
                if (e.Model is not null && t.Models is { } models && !models.Contains(e.Model, StringComparer.Ordinal)) return ($"The {where} names the model `{e.Model}`, which `{e.Connection}` does not allow.", $"It allows: {string.Join(", ", models)}.");
                return null;
            case SlotKind.Delivery:
                return t.CanSend ? null : ($"The {where} names `{e.Connection}`, which cannot send a message ({t.Kind}).", "A delivery channel sends through `kind: smtp`, or `kind: http` with a `webhook:` section.");
            default:
                return null;
        }
    }

    private static bool Fits(SlotKind kind, BindingTarget t) => kind switch
    {
        SlotKind.Delivery => t.CanSend,
        SlotKind.ModelChat or SlotKind.ModelEmbeddings => t.Kind == ConnectionKinds.ModelProvider && (t.Purpose ?? "chat") == PurposeOf(kind),
        _ => true,
    };

    private sealed class Reader(string fileName, List<ConfigDiagnostic> diags) : YamlFieldReader(fileName, diags)
    {
        private void Bad(YamlNode at, string message, string? fix = null) => Add(BadValue, at, message, fix);

        public BindingSet? Read(YamlNode root)
        {
            if (root is not YamlMapping map) { Bad(root, "The bindings file is a mapping with `bindings: 1`, `models` and `delivery`."); return null; }
            foreach (var old in new[] { "model-slots", "slots" })
                if (map.GetEntry(old) is { } entry)
                {
                    Add(EarlierForm, entry.Key, $"`{old}` is the earlier form of the model slots (models/_slots.yml, `model-slots: 1`). They are bindings now.",
                        "Write `bindings: 1` and put each slot under `models:`: `models: { default: { prod: [ { connection: <connection>, model: <id> } ] } }` (`provider:` is `connection:`).");
                    return null;
                }

            var format = map.Get("bindings");
            if (format is null) { Add(FormatNumber, map, $"`bindings` is missing: the file says which format of {Kind} it is in.", "Add `bindings: 1`."); return null; }
            if (format is not YamlScalar { Value: var text } || !int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number < 1)
            { Add(FormatNumber, format, "`bindings` must be a positive whole number."); return null; }
            if (number != Version) { Add(FormatNumber, format, Refusal(File, number), number > Version ? $"Upgrade the tool, or write the file in format {Version}." : null); return null; }
            CheckKeys(map, ["bindings", "models", "delivery"], "the bindings file");

            var slots = new List<SlotBinding>();
            ReadSection(map, "models", null, slots);
            ReadSection(map, "delivery", SlotKind.Delivery, slots);
            return new BindingSet(slots);
        }

        private void ReadSection(YamlMapping root, string section, SlotKind? fixedKind, List<SlotBinding> slots)
        {
            if (root.Get(section) is not { } node || node is YamlScalar { Value.Length: 0 }) return;
            if (node is not YamlMapping all) { Bad(node, $"`{section}` maps a slot name to what it is bound to: `{section}: {{ <slot>: {{ prod: ... }} }}`."); return; }
            foreach (var slot in all.Entries)
            {
                var name = slot.Key.Value;
                if (!NameOk(name)) { Bad(slot.Key, $"`{name}` is not a slot name: letters, digits, '-' and '_', from 1 to 64."); continue; }
                var kind = fixedKind ?? ModelKindOf(name);
                if (slots.Any(s => s.Kind == kind && s.Name == name)) { Bad(slot.Key, $"The slot `{name}` is named twice."); continue; }
                if (ReadSlot(kind, slot.Key, slot.Value) is { } read) slots.Add(read);
            }
        }

        private SlotBinding? ReadSlot(SlotKind kind, YamlScalar key, YamlNode value)
        {
            var name = key.Value;
            var perChannel = new Dictionary<string, IReadOnlyList<BindingEntry>>(StringComparer.Ordinal);
            if (value is YamlMapping channels)
            {
                if (channels.Entries.Count == 0) { Bad(value, $"The slot `{name}` is bound in no release channel; remove it, or write `<release channel>: <connection>`."); return null; }
                foreach (var channel in channels.Entries)
                {
                    var release = channel.Key.Value;
                    if (release != BindingSet.AnyReleaseChannel && !NameOk(release)) { Bad(channel.Key, $"`{release}` is not a release channel's name: letters, digits, '-' and '_' (or `*` for every release channel)."); continue; }
                    if (ReadEntries(kind, name, release, channel.Value) is { } entries) perChannel[release] = entries;
                }
            }
            else if (ReadEntries(kind, name, BindingSet.Production, value) is { } bare) perChannel[BindingSet.Production] = bare;   // a value alone binds prod only
            return perChannel.Count == 0 ? null : new SlotBinding(kind, name, perChannel, key.Line);
        }

        private List<BindingEntry>? ReadEntries(SlotKind kind, string slot, string release, YamlNode node)
        {
            var items = node is YamlSequence seq ? seq.Items : [node];
            var what = $"{SlotBinding.Describe(kind)} `{slot}` ({release})";
            var entries = new List<BindingEntry>();
            foreach (var item in items)
                if (ReadEntry(kind, what, item) is { } e) entries.Add(e);
            if (items.Count == 0) { Bad(node, $"The {what} is a list of at least one connection."); return null; }
            if (entries.Count != items.Count) return null;
            if (kind is SlotKind.Delivery or SlotKind.ModelEmbeddings && entries.Count > 1)
            { Bad(node, kind == SlotKind.Delivery ? $"The {what} binds one connection: a message is sent through one channel." : $"The {what} binds one model: an index is made with one model, so there is no fallback."); return null; }
            return entries;
        }

        private BindingEntry? ReadEntry(SlotKind kind, string what, YamlNode item)
        {
            var wantsModel = kind is SlotKind.ModelChat or SlotKind.ModelEmbeddings;
            var form = wantsModel ? "`{ connection: <name>, model: <id> }`" : "a connection's name";
            if (item is YamlScalar { Value.Length: > 0 } s)
            {
                if (wantsModel) { Bad(item, $"The {what} needs a model as well as a connection: {form}."); return null; }
                if (!ConnectionNameOk(s.Value)) { Bad(item, $"`{s.Value}` is not a connection's name (a letter, then letters, digits, - _ .)."); return null; }
                return new BindingEntry(s.Value, null, s.Line, s.Column);
            }

            if (item is not YamlMapping c) { Bad(item, $"A choice of the {what} is {form}."); return null; }
            if (c.GetEntry("provider") is { } old)
            {
                Add(EarlierForm, old.Key, "`provider` is `connection` now: a model provider is a connection of kind `model-provider`.", "Write `{ connection: <name>, model: <id> }`.");
                return null;
            }

            CheckKeys(c, wantsModel ? ["connection", "model"] : ["connection"], "a choice");
            var connection = Scalar(c, "connection", required: true, c);
            var model = wantsModel ? Scalar(c, "model", required: true, c) : null;
            if (connection is null || (wantsModel && model is null)) return null;
            if (!ConnectionNameOk(connection.Value)) { Bad(connection, $"`{connection.Value}` is not a connection's name (a letter, then letters, digits, - _ .)."); return null; }
            return new BindingEntry(connection.Value, model?.Value, connection.Line, connection.Column);
        }
    }

    /// <summary>A connection's name, as the console keeps it: a letter, then letters, digits, <c>- _ .</c>, up to 64.</summary>
    public static bool ConnectionNameOk(string name) => name.Length is >= 1 and <= 64 && char.IsAsciiLetter(name[0]) && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
}

/// <summary>Writes a <see cref="BindingSet"/> as <c>bindings.yml</c>: every name and model quoted, so a model id such as <c>llama3.1:8b</c> or a connection called <c>null</c> reads back as it is; slots and release channels in order, <c>*</c> last.</summary>
public static class BindingsWriter
{
    public static string Write(BindingSet set)
    {
        static string Q(string v) => "\"" + v.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
        static string Entry(BindingEntry e) => e.Model is null ? Q(e.Connection) : $"{{ connection: {Q(e.Connection)}, model: {Q(e.Model)} }}";
        var text = new StringBuilder($"bindings: {BindingsLoader.Version}\n");
        foreach (var (section, kinds) in new[] { ("models", new[] { SlotKind.ModelChat, SlotKind.ModelEmbeddings }), ("delivery", new[] { SlotKind.Delivery }) })
        {
            var slots = set.Slots.Where(s => kinds.Contains(s.Kind)).OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
            if (slots.Count == 0) continue;
            text.Append(section).Append(":\n");
            foreach (var slot in slots)
            {
                if (slot.Kind == SlotKind.Delivery && slot.PerChannel.Count == 1 && slot.PerChannel.TryGetValue(BindingSet.Production, out var only))
                {
                    text.Append("  ").Append(Q(slot.Name)).Append(": ").Append(Entry(only[0])).Append('\n');
                    continue;
                }

                text.Append("  ").Append(Q(slot.Name)).Append(":\n");
                foreach (var channel in slot.ReleaseChannels)
                {
                    var entries = slot.PerChannel[channel];
                    text.Append("    ").Append(Q(channel)).Append(": ");
                    text.Append(slot.Kind == SlotKind.Delivery || slot.Kind == SlotKind.ModelEmbeddings ? Entry(entries[0]) : "[ " + string.Join(", ", entries.Select(Entry)) + " ]").Append('\n');
                }
            }
        }

        return text.ToString();
    }
}
