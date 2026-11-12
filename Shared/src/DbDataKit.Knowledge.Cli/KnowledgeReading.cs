using DbDataKit.State;

namespace DbDataKit.Knowledge.Cli;

/// <summary>An item as <c>knowledge show</c> gives it: what it is, where it came from and its text (the whole item, or one section).</summary>
public sealed record ShownItem(string Id, string Title, string Role, string? Owner, string Source, string Version, IReadOnlyList<string> About, string Text, string? Section, IReadOnlyList<string> Sections);

/// <summary>An item as <c>knowledge about</c> lists it.</summary>
public sealed record AboutItem(string Id, string Title, string Role, string Source, string Version);

/// <summary>What the read commands ask of a host: the knowledge a caller may see (the access filter is the host's: the caller is the signed-in person or the agent's user, never the server).</summary>
public interface IKnowledgeReader
{
    Task<KnowledgeSearchResult> SearchAsync(string text, string channel, string? about, string? role, CancellationToken cancellation);

    /// <summary>The item, or null when there is none or the caller may not see it (the two are not told apart).</summary>
    ShownItem? Show(string id, string channel, string? section, string? version);

    IReadOnlyList<AboutItem> About(string subjectKey, string channel);
}

/// <summary>The reading commands over a <see cref="StateKnowledgeIndex"/> per channel, as the caller sees it (plan 27a KN16).</summary>
public sealed class StateKnowledgeReader(Func<string, StateKnowledgeIndex> index, IKnowledgeCaller caller, ISubjectCatalog catalog, EmbeddingResolver? embeddings = null, KnowledgeSearchMode mode = KnowledgeSearchMode.Both) : IKnowledgeReader
{
    public async Task<KnowledgeSearchResult> SearchAsync(string text, string channel, string? about, string? role, CancellationToken cancellation)
    {
        var ix = index(channel);
        var found = await ix.FindAsync(text, caller, catalog, mode, embeddings, 25, cancellation).ConfigureAwait(false);
        if (about is null && role is null) return found with { Hits = [.. found.Hits.Take(5)] };
        var about_ = about is null ? null : ix.Store.ItemsAbout(channel, about).ToHashSet(StringComparer.Ordinal);
        bool Keep(SearchHit hit) =>
            (about_ is null || about_.Contains(hit.ItemId)) && (role is null || string.Equals(ix.Store.Item(channel, hit.ItemId)?.Role, role, StringComparison.OrdinalIgnoreCase));
        return found with { Hits = [.. found.Hits.Where(Keep).Take(5)] };
    }

    public ShownItem? Show(string id, string channel, string? section, string? version)
    {
        var store = index(channel).Store;
        if (store.Item(channel, id) is not { } stored) return null;
        if (!KnowledgeAccess.Visible(StateKnowledgeIndex.ItemOf(stored), caller, catalog)) return null;
        if (version is not null && !string.Equals(stored.Version, version, StringComparison.Ordinal) && !stored.Hash.StartsWith(version, StringComparison.Ordinal)) return null;
        var text = string.Join("\n\n", store.Chunks(channel, id).Select(c => c.Text));
        var sections = Headings(text).Select(h => h.Title).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (section is not null)
        {
            if (Section(text, section) is not { } found) return null;
            text = found;
        }
        return new ShownItem(stored.Id, stored.Title, stored.Role, stored.Owner, stored.Source, stored.Version, stored.Subjects, text, section, sections);
    }

    private sealed record Heading(int Level, string Title, int Line);

    private static List<Heading> Headings(string text)
    {
        var headings = new List<Heading>();
        var lines = text.Split('\n');
        var fenced = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimEnd('\r');
            if (line.StartsWith("```", StringComparison.Ordinal) || line.StartsWith("~~~", StringComparison.Ordinal)) { fenced = !fenced; continue; }
            if (fenced || !line.StartsWith('#')) continue;
            var level = line.TakeWhile(c => c == '#').Count();
            if (level is >= 1 and <= 6 && line.Length > level && line[level] == ' ') headings.Add(new Heading(level, line[(level + 1)..].Trim().TrimEnd('#').Trim(), i));
        }
        return headings;
    }

    /// <summary>The whole section under a heading: from the heading line to the next heading of the same level or above (its sub-sections are part of it); null when there is no such heading.</summary>
    private static string? Section(string text, string section)
    {
        var headings = Headings(text);
        var start = headings.FirstOrDefault(h => string.Equals(h.Title, section, StringComparison.OrdinalIgnoreCase));
        if (start is null) return null;
        var lines = text.Split('\n');
        var end = headings.FirstOrDefault(h => h.Line > start.Line && h.Level <= start.Level)?.Line ?? lines.Length;
        return string.Join("\n", lines[start.Line..end]).Trim();
    }

    public IReadOnlyList<AboutItem> About(string subjectKey, string channel)
    {
        // an item about the subject itself or about anything inside it (a measure of the model)
        var store = index(channel).Store;
        return [.. store.Items(channel).Where(s => s.Subjects.Any(k => k == subjectKey || k.StartsWith(subjectKey + "/", StringComparison.Ordinal)))
            .Where(s => KnowledgeAccess.Visible(StateKnowledgeIndex.ItemOf(s), caller, catalog))
            .Select(s => new AboutItem(s.Id, s.Title, s.Role, s.Source, s.Version))];
    }
}
