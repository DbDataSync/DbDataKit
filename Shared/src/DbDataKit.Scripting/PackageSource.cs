namespace DbDataKit.Scripting;

/// <summary>
/// Where packages come from: a NuGet v3 index URL or a folder (a folder in the global-packages layout, <c>id/version/id.version.nupkg</c>, works as an offline feed). <see cref="Ids"/> is package source mapping: when any source
/// of a resolver has patterns, a package comes only from the sources whose patterns match its id (the longest pattern wins), which keeps a private id from being served by a public feed. A pattern is an id or a prefix ending in <c>*</c>.
/// </summary>
public sealed record PackageSource(string Location, IReadOnlyList<string>? Ids = null)
{
    public static bool Matches(string pattern, string id) =>
        pattern.EndsWith('*') ? id.StartsWith(pattern[..^1], StringComparison.OrdinalIgnoreCase) : string.Equals(pattern, id, StringComparison.OrdinalIgnoreCase);

    /// <summary>The sources a package may come from, in the order given. Without any mapping, every source; with some, only the ones whose longest matching pattern is the longest of all.</summary>
    public static IReadOnlyList<PackageSource> For(IReadOnlyList<PackageSource> sources, string id)
    {
        if (!sources.Any(s => s.Ids is { Count: > 0 })) return sources;
        var matches = sources
            .Select(s => (Source: s, Length: (s.Ids ?? []).Where(p => Matches(p, id)).Select(p => p.TrimEnd('*').Length).DefaultIfEmpty(-1).Max()))
            .Where(m => m.Length >= 0).ToList();
        if (matches.Count == 0) return [];
        var longest = matches.Max(m => m.Length);
        return matches.Where(m => m.Length == longest).Select(m => m.Source).ToList();
    }
}
