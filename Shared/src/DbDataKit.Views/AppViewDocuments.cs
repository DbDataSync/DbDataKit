using DbDataKit.Apps;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

/// <summary>
/// The dashboards and reports an app lists under <c>views</c>, loaded and checked against its manifest (plan 32 DBR13): the loader's own findings with the file and line, and what only the app can say.
/// A widget's query names a semantic model the manifest lists under <c>semantic</c> (<c>DBD-APP-024</c>), or a Store entity it lists with <c>read</c>; a drill and every link lead to a view of the app, with names
/// and types the target has (<c>DBD-VIEW-039</c>, <c>-044</c>, <c>-045</c>, <c>-037</c>); a custom widget's <c>component</c> is <c>&lt;this app&gt;/&lt;Export&gt;</c> of a widget the manifest declares, and its
/// <c>params</c> are the ones that widget declares, the required ones given and each of its type (<c>DBD-APP-025</c>).
/// </summary>
public sealed class AppViewDocuments : IViewDocumentChecker
{
    public static readonly AppViewDocuments Instance = new();

    private static ViewsLoadResult Load(string text, string file, IReadOnlyDictionary<string, IReadOnlyList<ViewParameter>>? targets = null) => ViewsLoader.Load(text, file, null, targets);

    public ViewDocumentInfo? Describe(string text, string file)
    {
        var load = Load(text, file);
        if (load.Document is not { } d) return null;
        return new ViewDocumentInfo(d.Kind == ViewKind.Dashboard ? "dashboard" : "report",
            [.. d.Parameters.Select(p => new ViewDocumentTarget(p.Name, p.Type, p.Many, p.Required, p.HasDefault))], d.Title);
    }

    public IReadOnlyList<ConfigDiagnostic> Check(string text, string file, ViewDocumentContext context)
    {
        var targets = context.Targets.ToDictionary(t => t.Key,
            t => (IReadOnlyList<ViewParameter>)[.. t.Value.Select(p => new ViewParameter(p.Name, p.Type, null, p.Many, p.Required, p.HasDefault, null, 0))]);
        var load = Load(text, file, targets);
        var found = new List<ConfigDiagnostic>(load.Diagnostics);
        if (load.Document is not { } document) return found;
        var manifest = context.Manifest;

        void Problem(string code, int line, int column, string message, string? fix = null) =>
            found.Add(DefaultDiagnosticCatalog.Instance.Create(code, file, line, column, message, fix));

        foreach (var widget in document.Widgets)
        {
            if (widget.Query is { } query) CheckQuery(manifest, widget, query, Problem);
            if (widget.Raw["drill"] is System.Text.Json.Nodes.JsonObject { } drill && drill["to"] is System.Text.Json.Nodes.JsonObject to)
                foreach (var kind in new[] { "dashboard", "report", "view" })
                    if (to[kind]?.GetValue<string>() is { } name && !context.Targets.ContainsKey($"{kind}:{name}"))
                        Problem(ViewsCodes.LinkTargetUnknown, widget.Line, widget.Column, $"The drill of `{widget.Id}` leads to the {kind} `{name}`, which this app does not have: a drill stays in the app instance.", "Name a view of the app, or add it first.");
            if (widget.Type == "custom") CheckCustom(manifest, document, widget, Problem);
        }
        foreach (var link in document.Links.Values.Where(l => l.Action is { Kind: "change" }))
            CheckChange(manifest, link, Problem);
        return found;
    }

    /// <summary>
    /// An action's <c>change</c> is made as the viewer through the Store, so the manifest must list the entity with <c>write</c> (the change applies, or waits for approval as the model says) or with <c>change request</c>
    /// (it only ever proposes): <c>DBD-VIEW-065</c>. A <c>task</c> is checked by the runner's own tables (the manifest lists no runner packages yet).
    /// </summary>
    private static void CheckChange(AppManifest manifest, ViewLink link, Action<string, int, int, string, string?> problem)
    {
        var action = link.Action!;
        var use = manifest.Models.FirstOrDefault(m => m.Model == action.Model);
        if (use is null || action.Entity is null || !use.Entities.TryGetValue(action.Entity, out var allowed) || !(allowed.HasFlag(DataOperations.Write) || allowed.HasFlag(DataOperations.ChangeRequest)))
            problem(ViewsCodes.ActionNotAllowed, link.Line, link.Column, $"The action `{link.Name}` changes `{action.Model}.{action.Entity}`, which the manifest does not list with `write` or `change request` under `models`.",
                "Add the model and `write` (or `change request`) for the entity to the manifest.");
    }

    private static void CheckQuery(AppManifest manifest, ViewWidget widget, WidgetQuery query, Action<string, int, int, string, string?> problem)
    {
        if (QueryRefusal(manifest, widget, query) is { } refusal) problem(AppManifestLoader.ViewDocumentModel, widget.Line, widget.Column, refusal.Message, refusal.Fix);
    }

    private static (string Message, string Fix)? QueryRefusal(AppManifest manifest, ViewWidget widget, WidgetQuery query)
    {
        switch (query.Form)
        {
            case "derived" or "semantic" or "semantic_sql" when query.Model is { } model && !manifest.SemanticModels.Contains(model):
                return ($"The widget `{widget.Id}` queries the semantic model `{model}`, which the manifest does not list under `semantic`.",
                    manifest.SemanticModels.Count == 0 ? "List the model under `semantic` in the manifest." : $"The manifest's semantic models: {string.Join(", ", manifest.SemanticModels)}.");
            case "store":
                var store = query.Raw["store"] as System.Text.Json.Nodes.JsonObject;
                var storeModel = store?["model"]?.GetValue<string>();
                var entity = store?["entity"]?.GetValue<string>();
                var use = manifest.Models.FirstOrDefault(m => m.Model == storeModel);
                if (use is null || entity is null || !use.Entities.TryGetValue(entity, out var allowed) || !allowed.HasFlag(DataOperations.Read))
                    return ($"The widget `{widget.Id}` reads `{storeModel}.{entity}`, which the manifest does not list with `read` under `models`.", "Add the model and `read` for the entity to the manifest.");
                break;
        }
        return null;
    }

    /// <summary>
    /// What the manifest forbids a document to read, as words (the second lock of the host that renders it, plan 32 DBR13): the publish check says the same at <c>app validate</c>, and a host that renders what was published before the check, or
    /// by another route, still refuses a widget whose query names a semantic model the manifest does not list or a Store entity it does not list with <c>read</c>.
    /// </summary>
    public static IReadOnlyList<string> AccessProblems(AppManifest manifest, ViewDocument document) =>
        [.. document.Widgets.Where(w => w.Query is not null).Select(w => QueryRefusal(manifest, w, w.Query!)).Where(r => r is not null).Select(r => r!.Value.Message)];

    private static void CheckCustom(AppManifest manifest, ViewDocument document, ViewWidget widget, Action<string, int, int, string, string?> problem)
    {
        var component = widget.Raw["component"]?.GetValue<string>() ?? "";
        var slash = component.IndexOf('/');
        var (app, export) = slash < 0 ? (component, "") : (component[..slash], component[(slash + 1)..]);
        if (app != manifest.Name)
        { problem(AppManifestLoader.ViewDocumentComponent, widget.Line, widget.Column, $"The custom widget `{widget.Id}` names the app `{app}`, and this app is `{manifest.Name}`: a component is one of this app's.", $"Write `component: {manifest.Name}/{export}`."); return; }
        var view = manifest.CustomViews.FirstOrDefault(v => v.Kind == CustomViewKind.Widget && v.Export == export);
        if (view is null)
        { problem(AppManifestLoader.ViewDocumentComponent, widget.Line, widget.Column, $"The custom widget `{widget.Id}` names `{component}`, which is not a widget the manifest declares under `views`.", manifest.CustomViews.Count == 0 ? "Declare it in the manifest: `- {name: ..., kind: widget, export: " + export + ", queries: {...}}`." : $"The app's widgets: {string.Join(", ", manifest.CustomViews.Where(v => v.Kind == CustomViewKind.Widget).Select(v => v.Export))}."); return; }
        var given = widget.Raw["params"] as System.Text.Json.Nodes.JsonObject ?? [];
        foreach (var (name, value) in given)
        {
            var declared = view.Params.FirstOrDefault(p => p.Name == name);
            if (declared is null) { problem(AppManifestLoader.ViewDocumentComponent, widget.Line, widget.Column, $"The custom widget `{widget.Id}` gives `{name}`, which `{export}` does not declare.", view.Params.Count == 0 ? $"`{export}` takes no parameters." : $"It declares: {string.Join(", ", view.Params.Select(p => p.Name))}."); continue; }
            string givenType; var givenMany = false;
            var text = value?.GetValueKind() == System.Text.Json.JsonValueKind.String ? value.GetValue<string>() : null;
            if (text is not null && text.StartsWith("$params.", StringComparison.Ordinal))
            {
                var p = document.Parameters.FirstOrDefault(x => x.Name == text[8..]);
                if (p is null) continue; // undeclared: the loader said so
                (givenType, givenMany) = (p.Type, p.Many);
            }
            else givenType = value?.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False => "boolean",
                System.Text.Json.JsonValueKind.Number => value!.ToJsonString().Contains('.') ? "number" : "integer",
                _ => "string",
            };
            if (!ViewsLoader.LinkTypeFits(givenType, givenMany, declared.Type, declared.Many))
                problem(AppManifestLoader.ViewDocumentComponent, widget.Line, widget.Column, $"`{name}` of `{export}` is {(declared.Many ? "a list of " : "")}{declared.Type}, and `{widget.Id}` gives {(givenMany ? "a list of " : "")}{givenType}.", null);
        }
        foreach (var p in view.Params.Where(p => p.Required && !p.HasDefault && !given.ContainsKey(p.Name)))
            problem(AppManifestLoader.ViewDocumentComponent, widget.Line, widget.Column, $"The custom widget `{widget.Id}` does not give the required parameter `{p.Name}` of `{export}`.", $"Add `{p.Name}:` under `params`.");
    }
}
