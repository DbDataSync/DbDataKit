namespace DbDataKit.Cli;

/// <summary>
/// What a command does, as flags (plan 27a AG02, decision SD3). The six <see cref="EffectClass"/> names stay what a person sees; each maps to traits by <see cref="TraitTable"/>, and a verb may declare more
/// (<see cref="Verb.Traits"/>). The agent runtime and the MCP server decide what to offer and when to ask from these, never from the effect class alone.
/// </summary>
[Flags]
public enum Traits
{
    None = 0,

    /// <summary>Reads definitions, files, metadata or the tool's own state.</summary>
    ReadsDefinitions = 1,

    /// <summary>Reads rows or values of someone's data (what a policy that keeps data from a cloud model refuses).</summary>
    ReadsData = 2,

    /// <summary>Writes definitions or files.</summary>
    WritesDefinitions = 4,

    /// <summary>Writes data (rows, values, bookkeeping tables).</summary>
    WritesData = 8,

    /// <summary>Changes the structure of a database or of a deployment.</summary>
    ChangesStructure = 16,

    /// <summary>Deletes something that cannot be had back by a command.</summary>
    Deletes = 32,

    /// <summary>Sends something outside the suite (mail, a webhook, a file to a share).</summary>
    Outward = 64,

    /// <summary>Runs with permissions the caller does not hold (an elevated action).</summary>
    Elevated = 128,
}

public static class TraitTable
{
    /// <summary>The traits that make a command a change: not offered by default, asked about by default.</summary>
    public const Traits Changing = Traits.WritesDefinitions | Traits.WritesData | Traits.ChangesStructure | Traits.Deletes | Traits.Outward | Traits.Elevated;

    /// <summary>The fixed mapping of an effect class to the traits it implies. A new effect class fails here until it is mapped.</summary>
    public static Traits For(EffectClass effect) => effect switch
    {
        EffectClass.OfflineOnly => Traits.ReadsDefinitions,
        EffectClass.RepoFilesOnly => Traits.ReadsDefinitions | Traits.WritesDefinitions,
        EffectClass.TargetReadOnly => Traits.ReadsDefinitions | Traits.ReadsData,
        EffectClass.TargetWrites => Traits.ReadsDefinitions | Traits.ReadsData | Traits.WritesData | Traits.ChangesStructure,
        EffectClass.TargetDataWrites => Traits.ReadsData | Traits.WritesData,
        EffectClass.TrackingTablesOnly => Traits.ReadsData | Traits.WritesData,
        _ => throw new ArgumentOutOfRangeException(nameof(effect), effect, "This effect class has no traits; add it to TraitTable.For."),
    };

    /// <summary>True when the traits include any that change something.</summary>
    public static bool Changes(this Traits traits) => (traits & Changing) != Traits.None;

    /// <summary>The names as a tool list prints them (<c>reads-definitions</c>), in the order of the flags.</summary>
    public static IReadOnlyList<string> Names(this Traits traits) => Enum.GetValues<Traits>()
        .Where(t => t != Traits.None && traits.HasFlag(t))
        .Select(Name)
        .ToList();

    private static string Name(Traits trait) => string.Concat(trait.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));

    /// <summary>The single trait a name stands for, or null.</summary>
    public static Traits? Parse(string name) => Enum.GetValues<Traits>().Where(t => t != Traits.None).Cast<Traits?>().FirstOrDefault(t => Name(t!.Value) == name);
}
