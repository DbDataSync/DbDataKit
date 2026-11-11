using DbDataKit.Config.Yaml;

namespace DbDataKit.Knowledge;

/// <summary>
/// A folder of knowledge read as a project (plan 27a KN01): every Markdown file is an item, <c>_knowledge.yml</c> files give the folders beneath them their defaults (nearest wins), and a file in a project's <c>knowledge/</c> folder
/// is about that project when it says nothing else. <c>README.md</c> and files starting with <c>_</c> are not items; a folder starting with <c>.</c> is not read.
/// </summary>
public sealed class KnowledgeProject
{
    private KnowledgeProject(string root, IReadOnlyList<KnowledgeItem> items, IReadOnlyList<ConfigDiagnostic> diagnostics)
    {
        Root = root;
        Items = items;
        Diagnostics = diagnostics;
    }

    public string Root { get; }

    /// <summary>The items that were read without an error, in path order.</summary>
    public IReadOnlyList<KnowledgeItem> Items { get; }

    /// <summary>Every diagnostic of every file, in path order: errors (the file is left out of <see cref="Items"/>) and warnings.</summary>
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; }

    public bool Ok => !Diagnostics.HasErrors();

    public KnowledgeItem? Find(string id) => Items.FirstOrDefault(i => string.Equals(i.Id, id, StringComparison.Ordinal));

    /// <param name="root">The folder.</param>
    /// <param name="impliedAbout">The subject of the project the folder belongs to, or null (the install's knowledge project has none: its folder files say it).</param>
    public static KnowledgeProject Load(string root, KnowledgeSubject? impliedAbout = null)
    {
        root = Path.GetFullPath(root);
        var items = new List<KnowledgeItem>();
        var diags = new List<ConfigDiagnostic>();
        if (!Directory.Exists(root))
        {
            diags.Add(DefaultDiagnosticCatalog.Instance.Create(SharedCodes.FileNotFound, root, 0, 0, $"The knowledge folder '{root}' does not exist."));
            return new(root, items, diags);
        }
        Walk(root, root, [], impliedAbout, items, diags);
        return new(root, [.. items.OrderBy(i => i.Id, StringComparer.Ordinal)], diags);
    }

    private static void Walk(string root, string folder, List<FolderDefaults> above, KnowledgeSubject? implied, List<KnowledgeItem> items, List<ConfigDiagnostic> diags)
    {
        var chain = above;
        var folderFile = Path.Combine(folder, KnowledgeItemLoader.FolderFileName);
        if (File.Exists(folderFile))
        {
            var result = KnowledgeItemLoader.LoadFolderFile(File.ReadAllText(folderFile), Relative(root, folderFile));
            diags.AddRange(result.Diagnostics);
            if (result.Defaults is { } defaults) chain = [defaults, .. above];   // nearest first
        }

        foreach (var path in Directory.EnumerateFiles(folder, "*.md").OrderBy(p => p, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith('_') || string.Equals(name, "README.md", StringComparison.OrdinalIgnoreCase)) continue;
            var relative = Relative(root, path);
            var result = KnowledgeItemLoader.Load(File.ReadAllText(path), relative, relative, chain, implied);
            diags.AddRange(result.Diagnostics);
            if (result.Item is not null && !result.Diagnostics.HasErrors()) items.Add(result.Item);
        }

        foreach (var sub in Directory.EnumerateDirectories(folder).OrderBy(p => p, StringComparer.Ordinal))
            if (!Path.GetFileName(sub).StartsWith('.'))
                Walk(root, sub, chain, implied, items, diags);
    }

    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
