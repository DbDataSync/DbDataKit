using System.Reflection;
using System.Text;

namespace DbDataBuild.Cli;

/// <summary>One project template: its name, the one-line description in its `.template` file, and its files (not including `.template`), relative paths with `/`.</summary>
internal sealed record ProjectTemplate(string Name, string Description, IReadOnlyList<TemplateFile> Files);

internal sealed record TemplateFile(string Path, string Text);

/// <summary>The project templates built into the executable (`templates/` in the repository, DESIGN.md 15.7).</summary>
internal static class TemplateStore
{
    private const string Prefix = "templates/";
    private const string DescriptionFile = ".template";

    public static IReadOnlyList<ProjectTemplate> All { get; } = Load();

    public static ProjectTemplate? Find(string name) => All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

    private static List<ProjectTemplate> Load()
    {
        var assembly = typeof(TemplateStore).Assembly;
        var files = new Dictionary<string, List<TemplateFile>>(StringComparer.Ordinal);
        var descriptions = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith(Prefix, StringComparison.Ordinal)).OrderBy(n => n, StringComparer.Ordinal))
        {
            var rest = resource[Prefix.Length..];
            var slash = rest.IndexOf('/');
            if (slash < 0) continue;
            var (name, path) = (rest[..slash], rest[(slash + 1)..]);
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            var text = reader.ReadToEnd().Replace("\r\n", "\n");
            if (path == DescriptionFile) { descriptions[name] = text.Trim(); continue; }
            if (!files.TryGetValue(name, out var list)) files[name] = list = [];
            list.Add(new TemplateFile(path, text));
        }
        return files.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => new ProjectTemplate(kv.Key, descriptions.GetValueOrDefault(kv.Key, ""), kv.Value)).ToList();
    }
}
