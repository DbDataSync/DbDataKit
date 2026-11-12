using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>How a publish takes effect (plan 30 section 3.1): <see cref="Serve"/> makes the version active at once; <see cref="Stage"/> stores it as the channel's staged version until it is activated.</summary>
public enum PublishMode
{
    Serve,
    Stage,
}

/// <summary>
/// What a slot is used for (plan 21 CH03): <see cref="Write"/> (the default, and what a slot that says nothing is) needs a binding in every channel it runs in; <see cref="Read"/> is only read from, so a channel without a binding
/// of its own reads production's (writes are never inherited, plan 21 decision 5).
/// </summary>
public enum SlotAccess
{
    Write,
    Read,
}

/// <summary>A named place a version needs filled in a channel before it can run there: a connection, a secret, a data source. Every declared slot is required; a <see cref="SlotAccess.Read"/> slot is satisfied by production's binding when the channel has none.</summary>
public sealed record DefinitionSlot(string Name, string? Description = null, SlotAccess Access = SlotAccess.Write)
{
    /// <summary>The access a manifest wrote as <c>access: read|write</c>; null when it wrote nothing, and a value that is neither is a mistake the caller reports (it gets <see langword="false"/>).</summary>
    public static bool TryParseAccess(string? text, out SlotAccess access)
    {
        access = SlotAccess.Write;
        if (text is null || text.Length == 0) return true;
        switch (text.Trim().ToLowerInvariant())
        {
            case "write": return true;
            case "read": access = SlotAccess.Read; return true;
            default: return false;
        }
    }
}

/// <summary>What a validator is shown: the version a publish is about to store.</summary>
public sealed record DefinitionCandidate(string Kind, string Name, string Manifest, IReadOnlyList<ArtifactRef> Artifacts, IReadOnlyList<string> DependsOn, int Format = DefinitionFormat.First);

/// <summary>
/// One kind of definition in the registry (plan 30 section 4): what it checks, which slots a version declares, which target types accept it, how it is published by default, and whether it is
/// trusted code that an administrator must approve before it becomes active.
/// </summary>
/// <param name="Name">Lowercase letters, digits, dashes and underscores (<c>semantic-model</c>, <c>integration</c>).</param>
/// <param name="TargetTypes">The target types that accept the kind (<c>serving</c>, <c>runner</c>); a publisher built for a target type refuses a kind that does not list it.</param>
/// <param name="DefaultMode">What a publish does when the publisher does not say: <c>serve</c> for serving kinds, <c>stage</c> for runner kinds.</param>
/// <param name="RequiresApproval">Trusted code: only an approved version can be activated, and publishing it with <c>serve</c> is refused.</param>
public sealed record DefinitionKind(string Name, IReadOnlyList<string> TargetTypes, PublishMode DefaultMode, bool RequiresApproval = false)
{
    /// <summary>The formats of the kind this program reads: the current one and the one before it (plan 24a UC01). A publish in another format is refused, and so is the reading of a version in one.</summary>
    public DefinitionFormats Formats { get; init; } = DefinitionFormats.One;

    /// <summary>The format a manifest is written in, when the manifest says (its <c>schemaVersion</c>); null when it does not, which is the request's format or 1.</summary>
    public Func<string, int?>? FormatOf { get; init; }

    /// <summary>What the kind checks beyond the common rules; returns what is wrong in words, or null. Null here accepts everything.</summary>
    public Func<DefinitionCandidate, string?>? Validator { get; init; }

    /// <summary>Slots every version of the kind has.</summary>
    public IReadOnlyList<DefinitionSlot> Slots { get; init; } = [];

    /// <summary>Slots a version declares in its manifest, read from the manifest text; added to <see cref="Slots"/>.</summary>
    public Func<string, IReadOnlyList<DefinitionSlot>>? SlotsOf { get; init; }

    /// <summary>The slots a version with this manifest needs bound in a channel.</summary>
    public IReadOnlyList<DefinitionSlot> SlotsFor(string manifest)
    {
        if (SlotsOf is null) return Slots;
        var declared = SlotsOf(manifest);
        return [.. Slots, .. declared.Where(d => Slots.All(s => s.Name != d.Name))];
    }
}

/// <summary>The registry of kinds (plan 30 section 4). A target registers the kinds it accepts; publishing a kind that is not registered is refused, naming the kinds that are.</summary>
public sealed partial class DefinitionKinds
{
    private readonly Dictionary<string, DefinitionKind> _kinds = new(StringComparer.Ordinal);

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex NamePattern();

    public DefinitionKinds Register(DefinitionKind kind)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (!NamePattern().IsMatch(kind.Name)) throw new ArgumentException($"A kind's name is lowercase letters, digits, dashes and underscores; '{kind.Name}' is not.", nameof(kind));
        if (kind.TargetTypes.Count == 0) throw new ArgumentException($"The kind '{kind.Name}' names no target type that accepts it.", nameof(kind));
        if (!_kinds.TryAdd(kind.Name, kind)) throw new InvalidOperationException($"The kind '{kind.Name}' is registered already.");
        return this;
    }

    public DefinitionKind? Find(string name) => _kinds.GetValueOrDefault(name);

    /// <summary>What each registered kind reads, as an instance row records it (<see cref="DefinitionFormatText"/>).</summary>
    public string FormatText => DefinitionFormatText.Format(_kinds.Values.Select(k => (k.Name, k.Formats)));

    public IReadOnlyList<DefinitionKind> All => [.. _kinds.Values.OrderBy(k => k.Name, StringComparer.Ordinal)];

    /// <summary>The kind, or the refusal that names it and lists the registered ones.</summary>
    public DefinitionKind Require(string name) =>
        Find(name) ?? throw new PublishRefusedException(PublishRefusal.UnknownKind,
            $"The kind '{name}' is not registered; the registered kinds are {(_kinds.Count == 0 ? "none" : string.Join(", ", _kinds.Keys.Order(StringComparer.Ordinal)))}.");
}
