using System.Text.Json.Nodes;
using DbDataBuild.Core;
using DbDataBuild.Semantic;
using DbDataKit.Config.Yaml;
using DbDataKit.Views;

namespace DbDataBuild.Cli;

/// <summary>
/// The dashboards and reports of a project (plan 32 DBR29): the files of its <c>dashboards/</c> and <c>reports/</c> folders, read by the views loader and checked against the project's own semantic models, which stand in for the
/// catalog a Gateway would hold. <c>project validate</c> (and so <c>project compile --check</c>, the Edit tab's check and the Views tab's) reports what a publish would refuse that can be known offline: the loader's own findings with
/// their file, line and column, and the engine's findings for a field, filter or request the model cannot carry. A saved query, an image and the model versions a Gateway pins are the Gateway's to check at publish.
/// </summary>
internal static class ProjectViews
{
    public const string DashboardsFolder = "dashboards";
    public const string ReportsFolder = "reports";

    private static readonly string[] Extensions = [".yaml", ".yml", ".json"];

    /// <summary>One document file of the project: its kind (from the folder), its name (the file's name without the extension) and its project-relative path.</summary>
    internal sealed record ViewFile(ViewKind Kind, string Name, string Path);

    public static string FolderOf(ViewKind kind) => kind == ViewKind.Report ? ReportsFolder : DashboardsFolder;

    public static ViewKind? KindOfFolder(string folder) => folder switch { DashboardsFolder => ViewKind.Dashboard, ReportsFolder => ViewKind.Report, _ => null };

    /// <summary>Every dashboard and report file of the project (the top of each folder only), in path order.</summary>
    public static IReadOnlyList<ViewFile> Files(string root)
    {
        var found = new List<ViewFile>();
        foreach (var folder in new[] { DashboardsFolder, ReportsFolder })
        {
            var directory = System.IO.Path.Combine(root, folder);
            if (!Directory.Exists(directory)) continue;
            foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
            {
                var extension = System.IO.Path.GetExtension(path);
                if (!Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase)) continue;
                found.Add(new ViewFile(KindOfFolder(folder)!.Value, System.IO.Path.GetFileNameWithoutExtension(path), $"{folder}/{System.IO.Path.GetFileName(path)}"));
            }
        }
        return found;
    }

    /// <summary>The file of a document, whichever extension it has, or null.</summary>
    public static ViewFile? Find(string root, ViewKind kind, string name) => Files(root).FirstOrDefault(f => f.Kind == kind && string.Equals(f.Name, name, StringComparison.Ordinal));

    /// <summary>The loader's findings for a text and then the catalog check against the models of the project; the document when it loaded.</summary>
    public static IReadOnlyList<ConfigDiagnostic> CheckText(ViewKind kind, string file, string text, SemanticProject semantic, out ViewDocument? document)
    {
        var loaded = ViewsLoader.Load(text, file, kind);
        document = loaded.Document;
        if (loaded.Document is not { } doc) return loaded.Diagnostics;
        var bindings = new Dictionary<string, CatalogBinding>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in semantic.Models)
            if (model.Compilation is { } compiled && !bindings.ContainsKey(model.Name)) bindings[model.Name] = new ModelCatalog(model.Name, 0, compiled.Catalog).Binding;
        return [.. loaded.Diagnostics, .. CatalogCheck.Check(doc.Widgets, file, bindings)];
    }

    /// <summary>The findings of every document file of the project, as the diagnostics the other checks give.</summary>
    public static List<Diagnostic> Check(string root, SemanticProject semantic)
    {
        var diagnostics = new List<Diagnostic>();
        foreach (var file in Files(root))
        {
            string text;
            try { text = File.ReadAllText(System.IO.Path.Combine(root, file.Path)).Replace("\r\n", "\n"); }
            catch (IOException ex) { diagnostics.Add(new Diagnostic(DiagnosticCatalog.InvalidValue, new(file.Path, 0, 0), $"The file could not be read: {ex.Message}")); continue; }
            foreach (var d in CheckText(file.Kind, file.Path, text, semantic, out _)) diagnostics.Add(Convert(d, file.Path));
        }
        return diagnostics;
    }

    private static Diagnostic Convert(ConfigDiagnostic d, string file) =>
        new(DiagnosticCatalog.InvalidValue, new(file, d.Line, d.Column), $"{d.Code}: {d.Message}{(d.KeyPath is null ? "" : $" [{d.KeyPath}]")}", Fix: d.Fix,
            SeverityOverride: d.Severity switch { ConfigSeverity.Error => Severity.Error, ConfigSeverity.Warning => Severity.Warning, _ => Severity.Note });

    /// <summary>The name of a document, as the Gateway accepts it: what <see cref="DbDataKit.Views.ViewDefinitions"/> publishes under.</summary>
    public static bool IsValidName(string name) => System.Text.RegularExpressions.Regex.IsMatch(name, "^[A-Za-z0-9][A-Za-z0-9_.-]{0,63}$");

    /// <summary>The catalog of a compiled model as the editor's wells read it: its name with the catalog's measures and dimensions.</summary>
    public static JsonObject CatalogOf(SemanticModelFile model)
    {
        var json = (JsonObject)model.Compilation!.Json.DeepClone();
        json["name"] = model.Name;
        return json;
    }
}
