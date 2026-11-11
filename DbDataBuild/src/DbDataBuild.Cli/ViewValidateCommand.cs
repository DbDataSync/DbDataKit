using DbDataBuild.Core;
using DbDataKit.Config.Yaml;
using DbDataKit.Views;

namespace DbDataBuild.Cli;

/// <summary>
/// `dbdatabuild dashboard validate` and `report validate` (effect: offline only; plan 32 DBR21): the files of a project's <c>dashboards/</c> or <c>reports/</c> folder (or the files named) read by the views loader as that kind and
/// checked against the project's own semantic models, which stand in for the catalog a Gateway holds. The findings are the ones <c>project validate</c> and <c>project compile</c> report for these folders, with the file, line and
/// column of the entry; a file of the other kind is refused saying so (a report read as a dashboard is <c>DBD-VIEW-001</c>). Nothing is connected to and nothing is written.
/// </summary>
internal static class ViewValidateCommand
{
    public static int Run(CommandSpec spec, ViewKind kind, string root, string[] paths, TextWriter output, TextWriter error)
    {
        output.WriteLine($"{ProductInfo.Cli} {spec.Name}  |  effect: {spec.Effect.Describe()}  |  {spec.Marks}  |  connection: none");
        var word = kind == ViewKind.Report ? "report" : "dashboard";
        var ctx = ProjectContext.Load(root);

        var chosen = new List<(string Path, string Name)>();
        if (paths.Length == 0)
        {
            foreach (var f in ProjectViews.Files(root).Where(f => f.Kind == kind)) chosen.Add((f.Path, f.Name));
        }
        else
        {
            foreach (var given in paths)
            {
                var full = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, given));
                if (!File.Exists(full))
                {
                    error.WriteLine($"No file `{given}` in the project. {(ProjectViews.Files(root).Any(f => f.Kind == kind) ? "The project's " + word + "s: " + string.Join(", ", ProjectViews.Files(root).Where(f => f.Kind == kind).Select(f => f.Path)) + "." : $"The project has no files in {ProjectViews.FolderOf(kind)}/.")}");
                    return CliApp.ExitUsage;
                }
                var relative = System.IO.Path.GetRelativePath(root, full).Replace('\\', '/');
                chosen.Add((relative, System.IO.Path.GetFileNameWithoutExtension(full)));
            }
        }

        var results = new List<object>();
        var errors = 0;
        var warnings = 0;
        foreach (var (path, name) in chosen)
        {
            string text;
            try { text = File.ReadAllText(System.IO.Path.Combine(root, path)).Replace("\r\n", "\n"); }
            catch (IOException ex)
            {
                error.Diag(new Diagnostic(DiagnosticCatalog.InvalidValue, new(path, 0, 0), $"The file could not be read: {ex.Message}"));
                results.Add(new { path, name, kind = word, ok = false, errors = 1, warnings = 0 });
                errors++;
                continue;
            }
            var findings = ProjectViews.CheckText(kind, path, text, ctx.Semantic, out _);
            var e = findings.Count(d => d.Severity == ConfigSeverity.Error);
            var w = findings.Count(d => d.Severity == ConfigSeverity.Warning);
            foreach (var d in findings) error.Diag(ProjectViews.Convert(d, path));
            output.WriteLine(e == 0 ? $"  {path}: ok{(w > 0 ? $" ({w} warning(s))" : "")}" : $"  {path}: {e} error(s){(w > 0 ? $", {w} warning(s)" : "")}");
            results.Add(new { path, name, kind = word, ok = e == 0, errors = e, warnings = w });
            errors += e;
            warnings += w;
        }
        output.Payload("files", results);
        output.Payload("errors", errors);
        output.Payload("warnings", warnings);
        output.WriteLine();
        if (chosen.Count == 0) { output.WriteLine($"No {word}s: the project has no files in {ProjectViews.FolderOf(kind)}/."); return CliApp.ExitOk; }
        output.WriteLine($"{chosen.Count} {word}(s) checked: {errors} error(s), {warnings} warning(s).");
        return errors == 0 ? CliApp.ExitOk : CliApp.ExitFindings;
    }
}
