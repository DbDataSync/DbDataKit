using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections.Bindings;

/// <summary>
/// What a slot is for (plan 27a IX23, <c>docs/channel-bindings.md</c>). A slot is a name a program or a person uses ("the default model", "alerts"); a binding says which connection fills it in each release channel. The kinds are the places the suite
/// binds a name to a connection: a chat model, an embedding model, a delivery channel and, read from the runner, a package's connection slot.
/// </summary>
public enum SlotKind
{
    /// <summary>A model slot an agent asks for (<c>models:</c> section): a connection of kind <c>model-provider</c> with purpose <c>chat</c> and one of its models; an ordered list, the first the primary and the others the fallback.</summary>
    ModelChat,

    /// <summary>The <c>embeddings</c> model slot: purpose <c>embeddings</c>; exactly one choice (an index has one model).</summary>
    ModelEmbeddings,

    /// <summary>A delivery channel name (<c>delivery:</c> section): a connection that can send (<c>kind: smtp</c>, or <c>kind: http</c> with a <c>webhook:</c> section); exactly one.</summary>
    Delivery,

    /// <summary>A connection slot of a runner package (<c>package/1</c>, frozen): read from the runner and shown in the same shape; the runner keeps its own store and rules, so this kind is never in the file.</summary>
    Runner,
}

/// <summary>The choice a slot makes in one release channel: a connection and, for a model slot, which of its models. <see cref="Line"/> and <see cref="Column"/> are where the file names it (0 when it was not read from a file).</summary>
public sealed record BindingEntry(string Connection, string? Model = null, int Line = 0, int Column = 0);

/// <summary>What a slot is bound to in one release channel, or why it is not, in words that name the slot, the release channel and where it is bound.</summary>
public sealed record BindingResolution(string Slot, string ReleaseChannel, IReadOnlyList<BindingEntry> Entries, string? From, string? Unbound)
{
    /// <summary>True when the slot has a connection in this release channel.</summary>
    public bool IsBound => Entries.Count > 0;

    /// <summary>True when the entry is the <c>*</c> one, written on purpose for every release channel without its own.</summary>
    public bool IsWildcard => From == BindingSet.AnyReleaseChannel;

    /// <summary>The first choice (the primary); null when unbound.</summary>
    public BindingEntry? Primary => Entries.Count > 0 ? Entries[0] : null;
}

/// <summary>One slot and what it is bound to in each release channel (<c>*</c> included, as written).</summary>
public sealed record SlotBinding(SlotKind Kind, string Name, IReadOnlyDictionary<string, IReadOnlyList<BindingEntry>> PerChannel, int Line = 0)
{
    /// <summary>
    /// The one rule (<c>docs/channel-bindings.md</c>): the release channel's own entry; else the <c>*</c> entry when the file has one (written on purpose); else <b>unbound</b>. A release channel never takes another's entry, production's included, and an
    /// absent release channel is production (<see cref="BindingSet.Production"/>).
    /// </summary>
    public BindingResolution Resolve(string? releaseChannel)
    {
        var release = string.IsNullOrWhiteSpace(releaseChannel) ? BindingSet.Production : releaseChannel.Trim();
        if (PerChannel.TryGetValue(release, out var own) && own.Count > 0) return new(Name, release, own, release, null);
        if (PerChannel.TryGetValue(BindingSet.AnyReleaseChannel, out var any) && any.Count > 0) return new(Name, release, any, BindingSet.AnyReleaseChannel, null);
        var bound = PerChannel.Where(c => c.Value.Count > 0).Select(c => c.Key).Order(StringComparer.Ordinal).ToList();
        var where = bound.Count == 0 ? "it is bound nowhere" : $"it is bound in: {string.Join(", ", bound)}";
        return new(Name, release, [], null, $"The {Describe(Kind)} '{Name}' is not bound in the release channel '{release}' ({where}). A release channel never uses another's connection: bind it there (or bind `*` on purpose for every release channel).");
    }

    /// <summary>The words for a kind of slot, as an unbound message says them.</summary>
    public static string Describe(SlotKind kind) => kind switch
    {
        SlotKind.ModelChat => "model slot",
        SlotKind.ModelEmbeddings => "embeddings slot",
        SlotKind.Delivery => "delivery channel",
        _ => "connection slot",
    };

    /// <summary>The release channels this slot names, <c>*</c> last.</summary>
    public IReadOnlyList<string> ReleaseChannels => [.. PerChannel.Keys.Where(k => k != BindingSet.AnyReleaseChannel).Order(StringComparer.Ordinal), .. PerChannel.Keys.Where(k => k == BindingSet.AnyReleaseChannel)];
}

/// <summary>Every slot of a bindings file. Read by <see cref="BindingsLoader"/>, written by <see cref="BindingsWriter"/>, resolved by <see cref="Resolve"/>.</summary>
public sealed record BindingSet(IReadOnlyList<SlotBinding> Slots)
{
    public const string AnyReleaseChannel = "*";
    public const string Production = "prod";

    public static readonly BindingSet Empty = new([]);

    public SlotBinding? Find(SlotKind kind, string name) => Slots.FirstOrDefault(s => s.Kind == kind && string.Equals(s.Name, name, StringComparison.Ordinal));

    public IEnumerable<SlotBinding> OfKind(SlotKind kind) => Slots.Where(s => s.Kind == kind);

    /// <summary>What a slot is bound to in a release channel; a slot that is not in the file is unbound there (with the words saying so).</summary>
    public BindingResolution Resolve(SlotKind kind, string name, string? releaseChannel) =>
        Find(kind, name)?.Resolve(releaseChannel)
        ?? new SlotBinding(kind, name, new Dictionary<string, IReadOnlyList<BindingEntry>>()).Resolve(releaseChannel);

    /// <summary>The slots among <paramref name="needed"/> of a kind that are unbound in a release channel, ordered, for a promotion to be refused naming them.</summary>
    public IReadOnlyList<string> Unbound(SlotKind kind, IEnumerable<string> needed, string? releaseChannel) =>
        [.. needed.Distinct().Where(n => !Resolve(kind, n, releaseChannel).IsBound).Order(StringComparer.Ordinal)];

    /// <summary>The set with one slot replaced (or added, or removed when <paramref name="slot"/> has no entries).</summary>
    public BindingSet With(SlotBinding slot)
    {
        var rest = Slots.Where(s => !(s.Kind == slot.Kind && s.Name == slot.Name)).ToList();
        if (slot.PerChannel.Values.Any(v => v.Count > 0)) rest.Add(slot);
        return new BindingSet(rest);
    }

    public BindingSet Without(SlotKind kind, string name) => new([.. Slots.Where(s => !(s.Kind == kind && s.Name == name))]);
}

/// <summary>What the checker needs to know about a connection that a binding names: its kind and, by kind, what it is for. Built from a <see cref="ConnectionDefinition"/> by <see cref="From"/>.</summary>
public sealed record BindingTarget(string Name, string Kind, string? Purpose = null, IReadOnlyList<string>? Models = null, bool CanSend = false)
{
    public static BindingTarget From(ConnectionDefinition d) => new(d.Name, d.KindName, d.ModelProvider?.Purpose, d.ModelProvider?.Models.Select(m => m.Id).ToList(),
        d.KindName == ConnectionKinds.Smtp || (d.KindName == ConnectionKinds.Http && d.Webhook is not null));
}

/// <summary>Where the checker finds the connections that exist (the console's files, a test's list).</summary>
public interface IBindingTargets
{
    BindingTarget? Find(string connection);

    IEnumerable<BindingTarget> All { get; }
}

public sealed class FixedBindingTargets(IEnumerable<BindingTarget> targets) : IBindingTargets
{
    private readonly Dictionary<string, BindingTarget> _by = targets.ToDictionary(t => t.Name, StringComparer.Ordinal);

    public BindingTarget? Find(string connection) => _by.GetValueOrDefault(connection);

    public IEnumerable<BindingTarget> All => _by.Values;
}

public sealed class BindingsResult(BindingSet? bindings, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public BindingSet? Bindings { get; } = bindings;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public bool Ok => Bindings is not null && !Diagnostics.HasErrors();
}
