using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Apps;

public static partial class AppChecks
{
    /// <summary>
    /// The custom views the manifest declares (plan 32 DBR30), checked against what the app is allowed: a widget declares at least one query; a <c>semantic</c> query names a model the manifest lists under <c>semantic</c> and, when
    /// <paramref name="semanticMembers"/> is given (model, then its measure and attribute names), only members the model has; a <c>store</c> query reads an entity of a model the manifest lists with <c>read</c>; and the code
    /// registers the export (a <c>defineWidget('Export', ...)</c> or <c>definePage('Export', ...)</c> in a file of <paramref name="sources"/>, the app's source). A problem names the manifest and the entry's line
    /// (<c>DBD-APP-020</c> to <c>022</c>); the shape of an entry is the loader's (<c>DBD-APP-019</c>). Imports that are not in the default library or the administrator's list are the build's, with the file and line.
    /// </summary>
    public static IReadOnlyList<ConfigDiagnostic> CustomViews(AppManifest manifest, IEnumerable<(string Path, string Text)>? sources = null, IReadOnlyDictionary<string, IReadOnlySet<string>>? semanticMembers = null, string manifestFile = "app.yaml")
    {
        var found = new List<ConfigDiagnostic>();
        void Problem(string code, SpecPos at, string message, string? fix = null) => found.Add(DefaultDiagnosticCatalog.Instance.Create(code, manifestFile, at.Line, at.Column, message, fix));
        var code = sources?.Select(s => s.Text).ToList();
        foreach (var view in manifest.CustomViews)
        {
            if (view.Kind == CustomViewKind.Widget && view.Queries.Count == 0)
                Problem(AppManifestLoader.CustomViewNoQuery, view.At, $"The custom widget `{view.Name}` declares no query: the host runs the queries as the viewer and hands the rows to the code, which fetches no data of its own on the paths that print or email.",
                    "Add `queries: {rows: {semantic: {model: ..., request: {...}}}}`.");
            foreach (var q in view.Queries)
            {
                if (q.Form == "semantic")
                {
                    if (!manifest.SemanticModels.Contains(q.Model))
                    { Problem(AppManifestLoader.CustomViewQueryModel, q.ModelAt, $"The query `{q.Name}` of `{view.Name}` asks the semantic model `{q.Model}`, which the manifest does not list under `semantic`.", manifest.SemanticModels.Count == 0 ? "List the model under `semantic`." : $"The manifest's semantic models: {string.Join(", ", manifest.SemanticModels)}."); continue; }
                    if (semanticMembers is not null && semanticMembers.TryGetValue(q.Model, out var members))
                        foreach (var member in q.Members.Where(m => !members.Contains(m)))
                            Problem(AppManifestLoader.CustomViewQueryModel, q.At, $"The query `{q.Name}` of `{view.Name}` names `{member}`, which the model `{q.Model}` does not have.");
                }
                else
                {
                    var use = manifest.Models.FirstOrDefault(m => m.Model == q.Model);
                    if (use is null) { Problem(AppManifestLoader.CustomViewQueryModel, q.ModelAt, $"The query `{q.Name}` of `{view.Name}` reads the Store model `{q.Model}`, which the manifest does not list under `models`."); continue; }
                    if (!use.Entities.TryGetValue(q.Entity!, out var allowed) || !allowed.HasFlag(DataOperations.Read))
                        Problem(AppManifestLoader.CustomViewQueryModel, q.ModelAt, $"The query `{q.Name}` of `{view.Name}` reads `{q.Model}.{q.Entity}`, which the manifest does not list with `read`.", $"Add `read` to `{q.Entity}` under `models`.");
                }
            }
            if (code is not null)
            {
                var define = view.Kind == CustomViewKind.Widget ? "defineWidget" : "definePage";
                var registered = new Regex($@"\b{define}\s*\(\s*[""']{Regex.Escape(view.Export)}[""']", RegexOptions.CultureInvariant);
                if (!code.Any(registered.IsMatch))
                    Problem(AppManifestLoader.CustomViewNotRegistered, view.ExportAt, $"The export `{view.Export}` of `{view.Name}` is not registered: no `{define}('{view.Export}', ...)` in the app's source.", $"Register the component: `{define}('{view.Export}', Component)` from `@dbdatakit/views-sdk`.");
            }
        }
        return found;
    }

    /// <summary>The script files of an app's source folder that can register a component: <c>.ts</c>, <c>.tsx</c>, <c>.js</c>, <c>.jsx</c> under the folder, leaving out <c>node_modules</c> and hidden folders.</summary>
    public static IEnumerable<(string Path, string Text)> SourceFiles(string sourceDirectory)
    {
        if (!Directory.Exists(sourceDirectory)) yield break;
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/');
            if (relative.Split('/').Any(part => part == "node_modules" || part.StartsWith('.'))) continue;
            if (!(relative.EndsWith(".ts", StringComparison.Ordinal) || relative.EndsWith(".tsx", StringComparison.Ordinal) || relative.EndsWith(".js", StringComparison.Ordinal) || relative.EndsWith(".jsx", StringComparison.Ordinal))) continue;
            yield return (relative, File.ReadAllText(file));
        }
    }
}
