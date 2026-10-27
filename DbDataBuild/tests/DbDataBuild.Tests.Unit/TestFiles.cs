using System.Text;
using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Models.Yaml;

namespace DbDataBuild.Tests;

/// <summary>
/// Writes a project's files for a test. The tests describe a project in one piece of text, as they always have: a `dbdatabuild.yml` with a `connections:` block. Connections are `connections/&lt;name&gt;.yaml` files now
/// (plan 13, E03), so this writer takes the block out of the text and writes one `connection/1` file per entry, so the tests read their projects through the real loader and the real file format. A text with no `connections:`
/// block, and every other file, is written as it is. The new format itself is tested by `ConnectionFilesTests`; this is only how the older fixtures say it.
/// <para>Used in the place of <see cref="File.WriteAllText(string, string)"/> in the test files that write a project (the compiled file is linked into the conformance project too).</para>
/// </summary>
public static class TestFiles
{
    public static void WriteAllText(string path, string? contents)
    {
        if (contents != null && Path.GetFileName(path) == ProductInfo.ConfigFile && contents.Contains("connections:", StringComparison.Ordinal) && Split(contents) is { } split)
        {
            File.WriteAllText(path, split.Remaining);
            var folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, ProjectConnectionFiles.Folder);
            Directory.CreateDirectory(folder);
            foreach (var (name, text) in split.Files) File.WriteAllText(Path.Combine(folder, name + ".yaml"), text);
            return;
        }
        File.WriteAllText(path, contents);
    }

    public static void WriteAllText(string path, string? contents, Encoding encoding) => File.WriteAllText(path, contents, encoding);

    /// <summary>The text without its `connections:` block, and the connection files that block stands for; null when the text has no such block that can be read (it is then written as it is, so a test of a damaged file sees the damage).</summary>
    public static (string Remaining, IReadOnlyList<(string Name, string Text)> Files)? Split(string text)
    {
        var diags = new List<Diagnostic>();
        if (StrictYamlReader.Read(text, ProductInfo.ConfigFile, diags) is not YamlMapping top || diags.Count > 0) return null;
        var entry = top.Entries.FirstOrDefault(e => e.Key.Value == "connections");
        if (entry is null || entry.Value is not YamlMapping connections) return null;
        var dialects = (top.Get("dialects") as YamlMapping)?.Entries.Select(e => e.Key.Value).ToHashSet(StringComparer.Ordinal) ?? [];

        var files = new List<(string, string)>();
        foreach (var e in connections.Entries)
        {
            var name = e.Key.Value;
            if (e.Value is not YamlMapping and not YamlScalar { Value.Length: 0 }) return null;      // `sqlserver: 16`: a test of that refusal, which stays in the project file's own words
            var settings = e.Value as YamlMapping;
            var words = settings?.Get("engine") switch
            {
                YamlScalar s => ConnectionKeywords.Words(s.Value).ToList(),
                YamlSequence q => q.Items.OfType<YamlScalar>().SelectMany(i => ConnectionKeywords.Words(i.Value)).ToList(),
                _ => [name],
            };
            var (kind, _, _) = ConnectionKeywords.Parse(words, dialects);
            if (kind is null) return null;      // a keyword list the tool refuses: the test is about that refusal, so it stays in the project file's own words
            var sb = new StringBuilder();
            sb.Append("schema: connection/1\nname: ").Append(Scalar(name)).Append("\nengine: ").Append(kind.Driver).Append('\n');
            if (kind.DialectDeclared && !ConnectionKeywords.BringsOwnDialect(kind.Driver) && kind.Dialect is { Length: > 0 } dialect) sb.Append("dialect: ").Append(dialect).Append('\n');
            if (kind.ReadOnly) sb.Append("access: readonly\n");
            var rest = settings?.Entries.Where(x => x.Key.Value != "engine").ToList() ?? [];
            if (rest.Count > 0)
            {
                sb.Append("extensions:\n  build:\n");
                foreach (var x in rest) sb.Append("    ").Append(Scalar(x.Key.Value)).Append(": ").Append(Flow(x.Value)).Append('\n');
            }
            files.Add((name, sb.ToString()));
        }

        var from = text.LastIndexOf('\n', Math.Max(0, entry.Key.Start - 1)) + 1;
        var to = entry.Value.End;
        while (to < text.Length && text[to] is ' ' or '\r') to++;
        if (to < text.Length && text[to] == '\n') to++;
        return (text[..from] + text[to..], files);
    }

    private static string Scalar(string value) => "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal) + "\"";

    private static string Flow(YamlNode node) => node switch
    {
        YamlScalar s => Scalar(s.Value),
        YamlSequence q => "[" + string.Join(", ", q.Items.Select(Flow)) + "]",
        YamlMapping m => "{" + string.Join(", ", m.Entries.Select(x => Scalar(x.Key.Value) + ": " + Flow(x.Value))) + "}",
        _ => "\"\"",
    };
}
