using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Knowledge;

namespace DbDataKit.Server;

/// <summary>A knowledge project the console reads: its name (<c>install</c> or <c>project</c>), its folder and what was read from it.</summary>
public sealed record KnowledgeFolder(string Name, string Path, KnowledgeProject Project);

/// <summary>The context an agent would be given for what is in play, with each part's tokens and share of the budget (plan 27a KN28, KN17's <c>knowledge context</c>).</summary>
public sealed record ContextReport(string Agent, IReadOnlyList<ContextPartRecord> Parts, int TotalTokens, int Window, int BudgetTokens, bool OverBudget, string Basis, IReadOnlyList<string> InPlay, IReadOnlyList<ContextPartShare> Shares);

public sealed record ContextPartShare(string Layer, string Id, string Version, int Tokens, double ShareOfBudget);

/// <summary>
/// The knowledge the console reads (plan 27a KN28, KN36, KN37, KN38): two projects in its directory, <c>install-knowledge/</c> (the template of KN35: the install's guardrails in <c>policy/</c>, owned by the administrators) and <c>knowledge/</c> (items about the install's objects),
/// each read with the strict reader. What exists to be about is <c>knowledge-subjects.json</c>: what an administrator says exists or is gone, and what the joined lineage graph of the servers says (<see cref="RefreshFromLineage"/>, plan 27a KN02), each entry saying which. There is no index here (KN07 is not built): the screens show what the files
/// say and what they would be split into, never what a search finds.
/// </summary>
public sealed class ConsoleKnowledge(ConsoleSettings settings)
{
    public string InstallFolder => Path.Combine(settings.Directory, "install-knowledge");

    public string ProjectFolder => Path.Combine(settings.Directory, "knowledge");

    public string SubjectsFile => Path.Combine(settings.Directory, "knowledge-subjects.json");

    public ISubjectCatalog Catalog => ReadCatalog();

    public bool HasCatalog => File.Exists(SubjectsFile);

    private ISubjectCatalog ReadCatalog()
    {
        var catalog = new SubjectCatalog();
        if (!File.Exists(SubjectsFile)) return catalog;
        foreach (var entry in Subjects())
        {
            catalog.Add(entry.Type, entry.Name, entry.NarrowingKind, entry.NarrowingName);
            if (entry.Status == "gone") catalog.Retire(entry.Type, entry.Name);
        }
        return catalog;
    }

    public sealed record SubjectEntry(string Type, string Name, string NarrowingKind, string NarrowingName, string Status, string Source = "manual");

    public IReadOnlyList<SubjectEntry> Subjects()
    {
        if (!File.Exists(SubjectsFile)) return [];
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(SubjectsFile));
            return [.. document.RootElement.GetProperty("subjects").EnumerateArray().Select(e => new SubjectEntry(e.GetProperty("type").GetString()!, e.GetProperty("name").GetString()!,
                e.TryGetProperty("narrowingKind", out var k) ? k.GetString() ?? "" : "", e.TryGetProperty("narrowingName", out var n) ? n.GetString() ?? "" : "", e.TryGetProperty("status", out var s) ? s.GetString() ?? "exists" : "exists", e.TryGetProperty("source", out var src) ? src.GetString() ?? "manual" : "manual"))];
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { return []; }
    }

    private void WriteSubjects(IEnumerable<SubjectEntry> all)
    {
        Directory.CreateDirectory(settings.Directory);
        File.WriteAllText(SubjectsFile, new JsonObject { ["subjects"] = new JsonArray(all.OrderBy(s => s.Type, StringComparer.Ordinal).ThenBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.NarrowingKind, StringComparer.Ordinal).ThenBy(s => s.NarrowingName, StringComparer.Ordinal).Select(s => (JsonNode)new JsonObject
        { ["type"] = s.Type, ["name"] = s.Name, ["narrowingKind"] = s.NarrowingKind, ["narrowingName"] = s.NarrowingName, ["status"] = s.Status, ["source"] = s.Source }).ToArray()) }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    /// <summary>The result of reading the subjects from the lineage graph: how many it shows, how many it showed before and no longer does (now gone), and whether every server answered (when not, nothing is marked gone: a server that did not answer is not a model that was dropped).</summary>
    public sealed record SubjectRefresh(int Found, int NowGone, bool Complete, int Total);

    /// <summary>
    /// Reads what exists to be about from the lineage graph (plan 27a KN02): every subject the graph has a node for is recorded as existing, by the graph; one the graph showed before and does not show now is recorded as gone, but only when every server answered
    /// (<paramref name="complete"/>) and only for a whole object (a member that vanished from a model that is still there is simply not found any more). What an administrator said by hand is never changed by it.
    /// </summary>
    public SubjectRefresh RefreshFromLineage(DbDataKit.Lineage.LineageGraph graph, bool complete)
    {
        var current = Subjects();
        var manual = current.Where(e => e.Source != "lineage").ToList();
        var earlier = current.Where(e => e.Source == "lineage").ToList();
        var catalog = new LineageSubjectCatalog(graph);
        var keyOf = (SubjectEntry e) => new KnowledgeSubject(e.Type, [e.Name], e.NarrowingKind, e.NarrowingName).Key;
        var manualKeys = manual.Select(keyOf).ToHashSet(StringComparer.Ordinal);
        var next = new List<SubjectEntry>(manual);
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var subject in catalog.Subjects)
        {
            present.Add(subject.Key);
            if (!manualKeys.Contains(subject.Key)) next.Add(new SubjectEntry(subject.Type, subject.Name, subject.NarrowingKind, subject.NarrowingName, "exists", "lineage"));
        }
        var nowGone = 0;
        foreach (var e in earlier.Where(e => e.NarrowingKind.Length == 0))
        {
            var key = keyOf(e);
            if (present.Contains(key) || manualKeys.Contains(key)) continue;
            // gone stays gone; a subject that existed and is missing from a complete answer is newly gone; from an incomplete one it is left as it was
            if (e.Status == "gone") next.Add(e);
            else if (complete) { next.Add(e with { Status = "gone" }); nowGone++; }
            else next.Add(e);
        }
        WriteSubjects(next);
        return new SubjectRefresh(catalog.Subjects.Count, nowGone, complete, next.Count);
    }

    /// <summary>Says an object exists or is gone (a model was dropped). Administrators do it; the worklists follow.</summary>
    public void SetSubject(string type, string name, string status, string narrowingKind = "", string narrowingName = "")
    {
        if (status is not ("exists" or "gone")) throw new ArgumentException("A subject exists or is gone.", nameof(status));
        var all = Subjects().Where(s => !(s.Type == type && s.Name == name && s.NarrowingKind == narrowingKind && s.NarrowingName == narrowingName)).ToList();
        all.Add(new SubjectEntry(type, name, narrowingKind, narrowingName, status));
        WriteSubjects(all);
    }

    public KnowledgeFolder? Folder(string name) => name switch
    {
        "install" => new KnowledgeFolder("install", InstallFolder, KnowledgeProject.Load(InstallFolder)),
        "project" => new KnowledgeFolder("project", ProjectFolder, KnowledgeProject.Load(ProjectFolder)),
        _ => null,
    };

    /// <summary>The items an agent can draw on, by source name (<c>install</c>, <c>project</c>); a folder that is not there is an empty source.</summary>
    public IReadOnlyList<KnowledgeSource> Sources() =>
        [.. new[] { "install", "project" }.Select(Folder).Where(f => f is not null && Directory.Exists(f.Path)).Select(f => new KnowledgeSource(f!.Name, f.Project.Items))];

    /// <summary>The install's guardrail text: the items of the install project's <c>policy/</c> folder, given to every agent.</summary>
    public IReadOnlyList<KnowledgeItem> PolicyItems() =>
        Directory.Exists(InstallFolder) ? [.. Folder("install")!.Project.Items.Where(i => i.Id.StartsWith(InstallKnowledge.PolicyFolder + "/", StringComparison.Ordinal))] : [];

    // ---- KN36: the install project ----

    /// <summary>Makes the install knowledge project from the template of KN35 in an empty <c>install-knowledge/</c>; refuses when it is there.</summary>
    public void CreateInstallProject() => InstallKnowledge.Write(InstallFolder);

    // ---- reading and saving an item ----

    public string? ReadItem(string project, string id)
    {
        if (ItemPath(project, id) is not { } path || !File.Exists(path)) return null;
        return File.ReadAllText(path);
    }

    private string? ItemPath(string project, string id)
    {
        var root = project switch { "install" => InstallFolder, "project" => ProjectFolder, _ => null };
        if (root is null || !id.EndsWith(".md", StringComparison.Ordinal) || id.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(id)) return null;
        var full = Path.GetFullPath(Path.Combine(root, id.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && !Path.GetFileName(full).StartsWith('_') ? full : null;
    }

    /// <summary>
    /// Saves an item when it reads without an error (strict reader, the policy folder's rule, and, when a subject catalog exists, subjects that are known); otherwise the file is left as it was and the diagnostics say why.
    /// </summary>
    public (bool Saved, IReadOnlyList<DiagnosticView> Diagnostics, string? Problem) SaveItem(string project, string id, string text)
    {
        if (ItemPath(project, id) is not { } path) return (false, [], $"'{id}' is not a knowledge file of the {project} project (a Markdown file inside its folder, not starting with an underscore).");
        if (project == "install" && !Directory.Exists(InstallFolder)) return (false, [], "The install knowledge project does not exist yet; make it from the template first.");
        var before = File.Exists(path) ? File.ReadAllText(path) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        var folder = Folder(project)!;
        var relative = id;
        var diagnostics = folder.Project.Diagnostics.Where(d => string.Equals(d.Path, relative, StringComparison.Ordinal)).Select(DiagnosticView.Of).ToList();
        if (folder.Project.Find(id) is { } item)
        {
            if (project == "install") diagnostics.AddRange(InstallKnowledge.CheckPolicyFolder(folder.Project).Where(d => d.Path == item.File).Select(DiagnosticView.Of));
            if (HasCatalog) diagnostics.AddRange(SubjectResolver.CheckOnSave(item, Catalog).Select(DiagnosticView.Of));
        }
        if (diagnostics.Any(d => d.Severity == "error") || folder.Project.Find(id) is null)
        {
            if (before is null) File.Delete(path); else File.WriteAllText(path, before);
            return (false, diagnostics, null);
        }
        return (true, diagnostics, null);
    }

    public bool Retire(string project, string id)
    {
        if (ItemPath(project, id) is not { } path || !File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    // ---- KN28 and KN37: the overview and the worklists ----

    public JsonObject Overview()
    {
        var catalog = Catalog;
        var projects = new JsonArray();
        var allItems = new List<KnowledgeItem>();
        var options = new ChunkerOptions();
        foreach (var name in new[] { "install", "project" })
        {
            var folder = Folder(name)!;
            var exists = Directory.Exists(folder.Path);
            var items = exists ? folder.Project.Items : [];
            allItems.AddRange(items);
            var failed = exists ? folder.Project.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(DiagnosticView.Of).ToList() : [];
            projects.Add(new JsonObject
            {
                ["name"] = name, ["exists"] = exists, ["items"] = items.Count, ["chunks"] = items.Sum(i => Chunker.Split(i, options).Count),
                ["instructions"] = items.Count(i => i.Role == KnowledgeRole.Instruction), ["guardrails"] = items.Count(i => i.Role == KnowledgeRole.Guardrail), ["references"] = items.Count(i => i.Role == KnowledgeRole.Reference),
                ["failed"] = JsonSerializer.SerializeToNode(failed, Web),
                ["orphans"] = new JsonArray(items.Where(i => SubjectResolver.IsOrphaned(i, catalog)).Select(i => (JsonNode)i.Id).ToArray()),
                ["files"] = new JsonArray(items.Select(i => (JsonNode)new JsonObject { ["id"] = i.Id, ["title"] = i.Title, ["role"] = i.Role.ToString().ToLowerInvariant(), ["owner"] = i.Owner }).ToArray()),
            });
        }
        return new JsonObject
        {
            ["projects"] = projects,
            ["hasSubjectCatalog"] = HasCatalog,
            ["subjects"] = JsonSerializer.SerializeToNode(Subjects(), Web),
            ["index"] = new JsonObject { ["built"] = false, ["message"] = "No index is built: the knowledge module and its embedding model are not part of this install yet. What is shown is what the files say and what they would be split into." },
            ["worklists"] = JsonSerializer.SerializeToNode(Worklists.Build(allItems, catalog), Web),
        };
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Re-points an item's gone subject to another object.</summary>
    public (bool Done, string? Problem) Repoint(string project, string id, string subjectKey, string newName)
    {
        if (ReadItem(project, id) is not { } text) return (false, $"There is no item '{id}' in the {project} project.");
        var folder = Folder(project)!;
        var item = folder.Project.Find(id);
        var subject = item?.About.FirstOrDefault(s => s.Key == subjectKey);
        if (subject is null) return (false, $"The item is not about '{subjectKey}' (any more).");
        var changed = KnowledgeInsight.Repoint(text, subject, newName, out var problem);
        if (changed is null) return (false, problem);
        var (saved, diagnostics, why) = SaveItem(project, id, changed);
        return saved ? (true, null) : (false, why ?? diagnostics.FirstOrDefault(d => d.Severity == "error")?.Message ?? "The change did not read as an item.");
    }

    // ---- KN38: previews ----

    public static KnowledgeSubject? ParseSubject(string key)
    {
        var parts = key.Split('/', 2);
        var head = parts[0].Split(':', 2);
        if (head.Length != 2 || head[0].Length == 0 || head[1].Length == 0) return null;
        var names = head[1].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1) return new KnowledgeSubject(head[0], names);
        var narrow = parts[1].Split(':', 2);
        return narrow.Length == 2 ? new KnowledgeSubject(head[0], names, narrow[0], narrow[1]) : null;
    }

    public ScopePreview PreviewScope(IEnumerable<string> inPlay, string? audience, string? agent, string? channel)
    {
        var subjects = inPlay.Select(ParseSubject).Where(s => s is not null).Select(s => new InPlay(s!, Whole: false)).ToList();
        var items = Sources().SelectMany(s => s.Items).ToList();
        return KnowledgeInsight.Scope(items, subjects, new ScopeAudience(audience, agent, channel), HasCatalog ? Catalog : null);
    }

    public ChunkPreview PreviewChunks(string project, string id, string text)
    {
        var published = ReadItem(project, id);
        return KnowledgeInsight.Chunks(text, id, id, published);
    }

    /// <summary>Which agents an item would reach (its audience against each agent's audience and name), for the preview of a guardrail or an instruction (plan 27a KN36, KN38).</summary>
    public static IReadOnlyList<string> AgentsReached(KnowledgeItem item, IEnumerable<(string Name, bool BackEnd)> agents) =>
        [.. agents.Where(a => item.Role != KnowledgeRole.Reference && item.For.Includes(a.BackEnd ? "back-end" : "front-end", a.Name, null)).Select(a => a.Name)];
}
