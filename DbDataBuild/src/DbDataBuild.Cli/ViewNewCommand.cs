using System.Text.RegularExpressions;
using DbDataBuild.Core;
using DbDataKit.Config.Yaml;
using DbDataKit.Views;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild dashboard new` and `report new` (effect: files of the repository only; plan 32 DBR21): a starting document in the project's <c>dashboards/</c> or <c>reports/</c> folder, the Views editor's "New" plus one text widget (the loader refuses a document with no widget or no tile: a dashboard
/// with a <c>reflow</c> layout of twelve columns, a report with an A4 <c>flow</c> layout and one section), which the author replaces in the editor or in the file. The file is checked as `dashboard validate` would check it before the
/// command says it is made; an existing file is never overwritten.
/// </summary>
internal static partial class ViewNewCommand
{
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_-]*$")]
    private static partial Regex NamePattern();

    /// <summary>The text of a new document (also what the tests compare against the editor's).</summary>
    public static string Text(ViewKind kind, string title)
    {
        var quoted = System.Text.Json.JsonSerializer.Serialize(title, new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return kind == ViewKind.Report
            ? $"schemaVersion: 1\nkind: report\ntitle: {quoted}\nwidgets:\n  note:\n    type: text\n    text: Write a note.\nlayouts:\n  - name: paged\n    type: flow\n    default: true\n    page:\n      size: A4\n      orientation: portrait\n      margins:\n        top: 15\n        right: 15\n        bottom: 15\n        left: 15\n    sections:\n      - title: Summary\n        blocks:\n          - {{widget: note}}\n"
            : $"schemaVersion: 1\nkind: dashboard\ntitle: {quoted}\nwidgets:\n  note:\n    type: text\n    text: Write a note.\nlayouts:\n  - name: desk\n    type: reflow\n    default: true\n    grid:\n      columns: 12\n    tiles:\n      - {{widget: note, x: 0, y: 0, w: 12, h: 1}}\n";
    }

    public static int Run(CommandSpec spec, ViewKind kind, string root, string name, string? title, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        var word = kind == ViewKind.Report ? "report" : "dashboard";
        if (!NamePattern().IsMatch(name))
        {
            error.WriteLine($"`{name}` is not a name for a {word}: it starts with a letter and has letters, digits, dashes and underscores.");
            return CliApp.ExitUsage;
        }
        var folder = ProjectViews.FolderOf(kind);
        var path = $"{folder}/{name}.yaml";
        if (ProjectViews.Find(root, kind, name) is { } existing)
        {
            error.WriteLine($"The project has the {word} `{name}` already ({existing.Path}); it is not overwritten.");
            return CliApp.ExitUsage;
        }
        var other = ProjectViews.Files(root).FirstOrDefault(f => f.Kind != kind && string.Equals(f.Name, name, StringComparison.Ordinal));
        var text = Text(kind, string.IsNullOrWhiteSpace(title) ? name : title.Trim());
        var ctx = ProjectContext.Load(root);
        var findings = ProjectViews.CheckText(kind, path, text, ctx.Semantic, out _).Where(d => d.Severity == ConfigSeverity.Error).ToList();
        if (findings.Count > 0)
        {
            foreach (var d in findings) error.Diag(ProjectViews.Convert(d, path));
            return CliApp.ExitFindings;
        }
        var full = System.IO.Path.Combine(root, folder, name + ".yaml");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
        output.WriteLine($"  created {path}");
        if (other is not null) output.WriteLine($"  note: the {(kind == ViewKind.Report ? "dashboard" : "report")} `{name}` ({other.Path}) has the same name; a link or a subscription names its kind.");
        output.Payload("path", path);
        output.Payload("name", name);
        output.Payload("kind", word);
        output.WriteLine();
        output.WriteLine($"Add widgets in the Views editor, or in the file ({word}s are checked by `{ProductInfo.Cli} {word} validate`).");
        return CliApp.ExitOk;
    }
}
