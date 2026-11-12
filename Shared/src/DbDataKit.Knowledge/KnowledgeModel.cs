namespace DbDataKit.Knowledge;

/// <summary>What an item is for (plan 27 section 3.5): found by search when relevant, always given where its scope applies, or an instruction marked as policy.</summary>
public enum KnowledgeRole
{
    Reference,
    Instruction,
    Guardrail,
}

/// <summary>How an item is split before it is embedded (plan 27 section 2.8, point 7).</summary>
public enum ChunkingMode
{
    /// <summary>The install's default: structure first, then size.</summary>
    Default,

    /// <summary>Keep the item whole.</summary>
    None,

    /// <summary>Split only at headings of one level (<see cref="ChunkingSetting.HeadingLevel"/>).</summary>
    HeadingLevel,
}

public sealed record ChunkingSetting(ChunkingMode Mode, int HeadingLevel = 0)
{
    public static readonly ChunkingSetting Default = new(ChunkingMode.Default);

    public static readonly ChunkingSetting None = new(ChunkingMode.None);
}

/// <summary>
/// One thing an item is about, named the way the catalog names it (plan 27 section 3.5c item 3). <see cref="Type"/> is the entry's own key (<c>semantic-model</c>), <see cref="Names"/> what it names (one name, a list for
/// <c>product</c>), <see cref="NarrowingKind"/> and <see cref="NarrowingName"/> the optional narrowing key and its value (<c>measure</c> and <c>revenue</c>), empty when there is none.
/// </summary>
public sealed record KnowledgeSubject(string Type, IReadOnlyList<string> Names, string NarrowingKind = "", string NarrowingName = "", int Line = 0, int Column = 0)
{
    /// <summary>The name, for the types that take one.</summary>
    public string Name => Names.Count > 0 ? Names[0] : "";

    /// <summary>The stable key of the subject, as lineage identities spell it: <c>semantic-model:Sales/measure:revenue</c>; a product list is <c>product:build,sync</c>.</summary>
    public string Key => $"{Type}:{string.Join(',', Names)}" + (NarrowingKind.Length > 0 ? $"/{NarrowingKind}:{NarrowingName}" : "");

    public override string ToString() => Key;

    /// <summary>The subject a key names (<c>semantic-model:Sales/measure:revenue</c>, as <see cref="Key"/> writes it), or null when it is not a key.</summary>
    public static KnowledgeSubject? TryParse(string key)
    {
        var parts = key.Split('/', 2);
        var head = parts[0].Split(':', 2);
        if (head.Length != 2 || head[0].Length == 0 || head[1].Length == 0) return null;
        var names = head[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (names.Length == 0) return null;
        if (parts.Length == 1) return new KnowledgeSubject(head[0], names);
        var narrow = parts[1].Split(':', 2);
        return narrow.Length == 2 && narrow[0].Length > 0 && narrow[1].Length > 0 ? new KnowledgeSubject(head[0], names, narrow[0], narrow[1]) : null;
    }

    /// <summary>The same subject with no position, so two items about the same thing compare equal.</summary>
    public KnowledgeSubject Unpositioned() => this with { Line = 0, Column = 0 };
}

/// <summary>Who an item is for: each key narrows, a key left out means all (plan 27 section 3.5c item 1).</summary>
public sealed record KnowledgeAudience(IReadOnlyList<string> Audience, IReadOnlyList<string> Agents, IReadOnlyList<string> Channels)
{
    public static readonly KnowledgeAudience Everyone = new([], [], []);

    public bool IsEveryone => Audience.Count == 0 && Agents.Count == 0 && Channels.Count == 0;

    /// <summary>Whether this audience includes the one asking (an empty key matches everyone; a null argument is "not asked about").</summary>
    public bool Includes(string? audience, string? agent, string? channel) =>
        Matches(Audience, audience) && Matches(Agents, agent) && Matches(Channels, channel);

    private static bool Matches(IReadOnlyList<string> allowed, string? value) => allowed.Count == 0 || value is null || allowed.Contains(value, StringComparer.Ordinal);
}

/// <summary>One knowledge item as read: the fields of its front matter (after the folder defaults), its text and where it came from.</summary>
/// <param name="Id">Where the item sits in its project: the relative path with forward slashes (<c>naming.md</c>).</param>
/// <param name="Title">The title from the front matter, or the first heading of the text.</param>
/// <param name="Role">Reference, instruction or guardrail.</param>
/// <param name="About">What the item is about; any one puts it in scope (an item about nothing in particular has none).</param>
/// <param name="File">The path diagnostics name.</param>
/// <param name="AboutImplied">The item has no <c>about</c> of its own and takes the project it is in.</param>
/// <param name="For">Who the item is for.</param>
/// <param name="Owner">The group that approves changes to it, or null.</param>
/// <param name="Chunking">How the item is split before it is embedded.</param>
/// <param name="Body">The text after the front matter.</param>
/// <param name="BodyLine">The line of the file where the text starts, so a section can be cited.</param>
/// <param name="Hash">sha256 of the file's text: the item's version for the record of a turn and for refresh.</param>
public sealed record KnowledgeItem(
    string Id,
    string File,
    string Title,
    KnowledgeRole Role,
    IReadOnlyList<KnowledgeSubject> About,
    bool AboutImplied,
    KnowledgeAudience For,
    string? Owner,
    ChunkingSetting Chunking,
    string Body,
    int BodyLine,
    string Hash,
    VerifiedQuery? Verified = null);
