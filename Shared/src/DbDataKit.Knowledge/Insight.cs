using DbDataKit.Config.Yaml;

namespace DbDataKit.Knowledge;

/// <summary>A diagnostic as a screen shows it.</summary>
public sealed record DiagnosticView(string Code, string Severity, string File, int Line, int Column, string Message, string? Fix)
{
    public static DiagnosticView Of(ConfigDiagnostic d) => new(d.Code, d.Severity.ToString().ToLowerInvariant(), d.Path, d.Line, d.Column, d.Message, d.Fix);
}

/// <summary>One chunk of an item as it will be split: where it sits (heading path), its size and whether it differs from the published version (plan 27a KN38).</summary>
public sealed record ChunkView(string Id, IReadOnlyList<string> HeadingPath, int Tokens, int StartLine, bool Changed, string Text);

/// <summary>An item as typed, read strictly, with how it would be chunked.</summary>
public sealed record ChunkPreview(string? Title, string? Role, string? Owner, IReadOnlyList<DiagnosticView> Diagnostics, IReadOnlyList<ChunkView> Chunks, int ChangedChunks);

/// <summary>An item that applies, in the order it would be given (plan 27a KN38), and why it does.</summary>
public sealed record ScopedItem(string Id, string Title, string Role, int Rank, string Scope, string? Owner);

/// <summary>An item that does not apply, and why.</summary>
public sealed record SkippedItem(string Id, string Title, string Role, string Reason);

/// <summary>What would be given for what is in play: the instructions and the guardrails in order, and what is left out and why (the test of KN03 as a screen).</summary>
public sealed record ScopePreview(IReadOnlyList<ScopedItem> Instructions, IReadOnlyList<ScopedItem> Guardrails, IReadOnlyList<SkippedItem> Skipped);

public static class KnowledgeInsight
{
    /// <summary>
    /// Reads <paramref name="text"/> as the item <paramref name="id"/> and splits it as it would be embedded. With <paramref name="publishedText"/> the chunks whose hash is not among the published version's are marked changed: editing one section changes one chunk.
    /// </summary>
    public static ChunkPreview Chunks(string text, string id, string file, string? publishedText = null, ChunkerOptions? options = null, IReadOnlyList<FolderDefaults>? defaults = null, KnowledgeSubject? impliedAbout = null)
    {
        options ??= new ChunkerOptions();
        var result = KnowledgeItemLoader.Load(text, id, file, defaults, impliedAbout);
        var diagnostics = result.Diagnostics.Select(DiagnosticView.Of).ToList();
        if (result.Item is not { } item) return new ChunkPreview(null, null, null, diagnostics, [], 0);

        var known = new HashSet<string>(StringComparer.Ordinal);
        if (publishedText is not null && KnowledgeItemLoader.Load(publishedText, id, file, defaults, impliedAbout).Item is { } published)
            foreach (var chunk in Chunker.Split(published, options)) known.Add(chunk.Hash);
        var chunks = Chunker.Split(item, options)
            .Select(c => new ChunkView(c.Id, c.HeadingPath, options.Tokens(c.EmbeddedText), c.StartLine, publishedText is not null && !known.Contains(c.Hash), c.Text)).ToList();
        return new ChunkPreview(item.Title, item.Role.ToString().ToLowerInvariant(), item.Owner, diagnostics, chunks, chunks.Count(c => c.Changed));
    }

    /// <summary>What <see cref="ScopeMatcher"/> gives for what is in play, with the items it leaves out and the reason of each.</summary>
    public static ScopePreview Scope(IReadOnlyList<KnowledgeItem> items, IReadOnlyList<InPlay> inPlay, ScopeAudience audience, ISubjectCatalog? catalog = null)
    {
        var bound = ScopeMatcher.Match(items, inPlay, audience, catalog);
        ScopedItem Of(KnowledgeItem i) => new(i.Id, i.Title, i.Role.ToString().ToLowerInvariant(), ScopeMatcher.RankOf(i, inPlay) ?? 0, ScopeName(ScopeMatcher.RankOf(i, inPlay)), i.Owner);
        var applied = bound.All.Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var skipped = items.Where(i => !applied.Contains(i.Id)).Select(i => new SkippedItem(i.Id, i.Title, i.Role.ToString().ToLowerInvariant(), WhySkipped(i, inPlay, audience, catalog))).OrderBy(s => s.Id, StringComparer.Ordinal).ToList();
        return new ScopePreview([.. bound.Instructions.Select(Of)], [.. bound.Guardrails.Select(Of)], skipped);
    }

    private static string ScopeName(int? rank) => rank switch
    {
        null => "",
        0 => "the install",
        1 => "a product",
        2 => "a project",
        3 => "a folder",
        4 => "a model",
        _ => "a member",
    };

    private static string WhySkipped(KnowledgeItem item, IReadOnlyList<InPlay> inPlay, ScopeAudience audience, ISubjectCatalog? catalog)
    {
        if (item.Role == KnowledgeRole.Reference) return "A reference is found by search; it is never given by scope.";
        if (catalog is not null && SubjectResolver.IsOrphaned(item, catalog)) return "Orphaned: the object it is about is gone, so it reaches nobody.";
        if (!item.For.Includes(audience.Audience, audience.Agent, audience.Channel)) return "Not for this audience, agent or channel.";
        return "What it is about is not in play.";
    }

    /// <summary>The scope's rank names in order, for a legend.</summary>
    public static IReadOnlyList<string> ScopeNames { get; } = ["the install", "a product", "a project", "a folder", "a model", "a member"];

    /// <summary>
    /// Re-points an item's subject to another object by rewriting the name where it is written (the subject carries its line), so the rest of the file is untouched. Returns null with <paramref name="problem"/> when the line is not where the subject says
    /// or the result does not read as an item that is about the new name.
    /// </summary>
    public static string? Repoint(string text, KnowledgeSubject subject, string newName, out string? problem)
    {
        problem = null;
        if (subject.Line < 1 || string.IsNullOrWhiteSpace(newName)) { problem = "The subject has no position in the file."; return null; }
        var eol = text.Contains("\r\n") ? "\r\n" : "\n";
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (subject.Line > lines.Length) { problem = "The subject's line is beyond the end of the file."; return null; }
        var line = lines[subject.Line - 1];
        var colon = line.IndexOf(':');
        var at = colon < 0 ? -1 : line.IndexOf(subject.Name, colon + 1, StringComparison.Ordinal);
        if (at < 0) { problem = $"The name '{subject.Name}' is not on line {subject.Line}; the file changed since it was read."; return null; }
        lines[subject.Line - 1] = line[..at] + newName.Trim() + line[(at + subject.Name.Length)..];
        return string.Join(eol, lines);
    }
}

/// <summary>One thing an owner has to look at, and what can be done about it. <see cref="Kind"/> is <c>orphaned</c>, <c>over-budget</c> or <c>needs-look</c>; for an orphan <see cref="Subject"/> is the key of the subject that is gone.</summary>
public sealed record WorkItem(string Kind, string ItemId, string Title, string Message, string? Subject, KnowledgeSubject? SubjectDetail);

/// <summary>The worklist of one owner (a group of the identity home), plan 27a KN37.</summary>
public sealed record OwnerWorklist(string Owner, IReadOnlyList<WorkItem> Work);

public static class Worklists
{
    /// <summary>
    /// The worklists per owner: orphaned items (with the subjects that are gone), items over a budget (<paramref name="overBudget"/> says for an item), items marked for another look (<paramref name="needsLook"/>, ids). An item with no owner is listed under <c>(no owner)</c>.
    /// </summary>
    public static IReadOnlyList<OwnerWorklist> Build(IEnumerable<KnowledgeItem> items, ISubjectCatalog catalog, Func<KnowledgeItem, string?>? overBudget = null, IReadOnlySet<string>? needsLook = null)
    {
        var work = new List<(string Owner, WorkItem Item)>();
        foreach (var item in items)
        {
            var owner = item.Owner ?? "(no owner)";
            if (SubjectResolver.IsOrphaned(item, catalog))
            {
                var gone = item.About.Where(s => SubjectResolver.Status(catalog, s) == SubjectStatus.Gone).ToList();
                work.Add((owner, new WorkItem("orphaned", item.Id, item.Title, $"Everything it is about is gone ({string.Join(", ", gone.Select(s => s.Key))}); it reaches nobody. Point it at another object, or retire it.", gone.FirstOrDefault()?.Key, gone.FirstOrDefault())));
            }
            if (overBudget?.Invoke(item) is { } over) work.Add((owner, new WorkItem("over-budget", item.Id, item.Title, over, null, null)));
            if (needsLook is not null && needsLook.Contains(item.Id)) work.Add((owner, new WorkItem("needs-look", item.Id, item.Title, "Something it depends on changed; read it again.", null, null)));
        }
        return [.. work.GroupBy(w => w.Owner, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new OwnerWorklist(g.Key, [.. g.Select(w => w.Item).OrderBy(w => w.Kind, StringComparer.Ordinal).ThenBy(w => w.ItemId, StringComparer.Ordinal)]))];
    }
}
