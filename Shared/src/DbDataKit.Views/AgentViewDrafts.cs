using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Views;

/// <summary>What the check or the making of an agent's draft says: whether it stands and, in words the model can act on, why not. <see cref="Text"/> is the document as it would be stored (JSON, indented, so its lines are the ones the diagnostics name).</summary>
public sealed record DraftResult(bool Ok, string Message, string? Text = null, string? Kind = null, string? Title = null);

/// <summary>What a host lets an agent's draft do.</summary>
/// <param name="AllowSql">Whether a widget may carry an explicit <c>semantic.sql</c> (the author's right to write SQL, plan 32 section 7); an agent has none unless its host says so.</param>
/// <param name="Catalogs">The models the draft may use, with the engine's validator (the catalog check of plan 32 section 2.3); null skips that check and the draft is checked as a file is by <c>dashboard validate</c> without a project.</param>
/// <param name="Targets">The parameters of the documents a drill or a link may lead to; null skips that check.</param>
public sealed record DraftOptions(bool AllowSql = false, IReadOnlyDictionary<string, CatalogBinding>? Catalogs = null, IReadOnlyDictionary<string, IReadOnlyList<ViewParameter>>? Targets = null);

/// <summary>
/// What an agent's tools do with a dashboard or a report (plan 32 section 7, DBR20), as pure functions over the documents: <b>the same strict loaders and the catalog check</b> a person's file goes through, so a draft with a URL image, raw HTML, a Vega
/// expression or an <c>aggregate</c> transform is refused by the rule that refuses it for a human, and every refusal comes back with the document's path, line and the code, so the draft is corrected in the loop. Nothing here stores anything; the host that owns the user store
/// does that after the person approves (<see cref="UserViewDraftService"/>).
/// </summary>
public static class AgentViewDrafts
{
    private const int MaximumReported = 10;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>Checks a document the agent wrote: the loader of its kind, then the check of what an agent may not use, then the catalog.</summary>
    /// <param name="document">The dashboard or report as a JSON object.</param>
    /// <param name="expected">The kind the tool is for (<c>dashboard_draft</c>, <c>report_draft</c>); null accepts either (<c>views_validate</c>).</param>
    /// <param name="options">What the host lets the draft do.</param>
    public static DraftResult Validate(JsonObject document, ViewKind? expected, DraftOptions options)
    {
        var text = document.ToJsonString(Indented);
        return Check(text, expected, options);
    }

    /// <summary>Checks a document's text (a stored one, or the result of an edit).</summary>
    public static DraftResult Check(string text, ViewKind? expected, DraftOptions options)
    {
        const string file = "draft.json";
        var loaded = ViewsLoader.Load(text, file, expected, options.Targets);
        if (!loaded.Ok || loaded.Document is not { } document)
            return Refused(loaded.Diagnostics);

        var problems = new List<ConfigDiagnostic>();
        if (!options.AllowSql)
            foreach (var widget in document.Widgets)
                if (widget.Query?.Raw["semantic"] is JsonObject { } semantic && semantic["sql"] is not null)
                    problems.Add(new ConfigDiagnostic(ViewsCodes.BadQuery, ConfigSeverity.Error, file, widget.Line, widget.Column,
                        $"The widget `{widget.Id}` writes its own `semantic.sql`, which you may not use here.",
                        "Name the fields in `encodings` and let the query be derived, or write `semantic.request` over the model's members.")
                    { KeyPath = $"widgets.{widget.Id}.query.semantic.sql" });
        if (problems.Count == 0 && options.Catalogs is { } catalogs)
            problems.AddRange(CatalogCheck.Check(document.Widgets, file, catalogs).Where(d => d.Severity == ConfigSeverity.Error));
        if (problems.Count > 0) return Refused(problems);

        var kind = document.Kind == ViewKind.Dashboard ? ViewDefinitions.Dashboard : ViewDefinitions.Report;
        var warnings = loaded.Diagnostics.Where(d => d.Severity != ConfigSeverity.Error).Take(MaximumReported).ToList();
        var note = warnings.Count == 0 ? "" : " Warnings: " + string.Join(" ", warnings.Select(Describe));
        return new DraftResult(true, $"The {kind} is valid: {document.Widgets.Count} widgets, {document.Layouts.Count} layouts.{note}", text, kind, document.Title);
    }

    /// <summary>
    /// A layout for an existing document (a print layout for a dashboard, a phone canvas for a report): the layout is added to the document's own and the whole is checked. It is never the default (the default is the one that places every widget and the document
    /// already has it), so a layout that says <c>default: true</c> is refused in words.
    /// </summary>
    public static DraftResult AddLayout(string existingText, JsonObject layout, DraftOptions options)
    {
        if (Read(existingText) is not { } root) return new(false, "The document to add the layout to could not be read.");
        if (layout["default"] is JsonValue { } d && d.TryGetValue<bool>(out var isDefault) && isDefault)
            return new(false, "A layout added to a document is not the default: the document's default layout stays. Remove `default` (or write `default: false`).");
        if (root["layouts"] is not JsonArray layouts) return new(false, "The document has no `layouts` to add to.");
        layouts.Add(layout.DeepClone());
        return Check(root.ToJsonString(Indented), null, options);
    }

    /// <summary>
    /// A widget pinned to a document (plan 32 section 4, DBR12): the widget is added to the document's <c>widgets</c> and given a tile in its <b>default</b> layout, below what is there, so the default still places every widget. A canvas default has fixed bounds: if the tile
    /// does not fit the check says so and the agent places it with its own tile through a draft.
    /// </summary>
    public static DraftResult Pin(string existingText, string widgetId, JsonObject widget, DraftOptions options)
    {
        if (Read(existingText) is not { } root) return new(false, "The dashboard to pin to could not be read.");
        if (root["widgets"] is not JsonObject widgets) return new(false, "The dashboard has no `widgets` to add to.");
        if (widgets.ContainsKey(widgetId)) return new(false, $"The dashboard already has a widget `{widgetId}`; pin under another id.");
        if (root["layouts"] is not JsonArray layouts || layouts.OfType<JsonObject>().FirstOrDefault(l => l["default"]?.GetValue<bool>() == true) is not { } def)
            return new(false, "The dashboard has no default layout to place the widget in.");
        widgets[widgetId] = widget.DeepClone();
        switch (def["type"]?.GetValue<string>())
        {
            case "reflow" when def["tiles"] is JsonArray tiles:
                var columns = (int)Number(def["grid"]?["columns"], 12);
                var y = tiles.OfType<JsonObject>().Select(t => Number(t["y"], 0) + Number(t["h"], 0)).DefaultIfEmpty(0).Max();
                var h = widget["type"]?.GetValue<string>() is "metric" or "control" ? 1 : 3;
                tiles.Add(new JsonObject { ["widget"] = widgetId, ["x"] = 0, ["y"] = y, ["w"] = Math.Max(1, columns / 2), ["h"] = h });
                if (def["grid"] is JsonObject grid && grid["rows"] is { } rowsNode && Number(rowsNode, 0) < y + h) grid["rows"] = (int)(y + h);
                break;
            case "canvas" when def["tiles"] is JsonArray tiles:
                var canvasWidth = Number(def["canvas"]?["width"], 0);
                var top = tiles.OfType<JsonObject>().Select(t => Number(t["y"], 0) + Number(t["h"], 0)).DefaultIfEmpty(0).Max() + 20;
                tiles.Add(new JsonObject { ["widget"] = widgetId, ["x"] = 20, ["y"] = top, ["w"] = Math.Max(1, canvasWidth / 2 - 20), ["h"] = 200 });
                break;
            case "flow" when def["sections"] is JsonArray sections && sections.Count > 0 && sections[^1] is JsonObject last && last["blocks"] is JsonArray blocks:
                blocks.Add(new JsonObject { ["widget"] = widgetId });
                break;
            default:
                return new(false, "The default layout could not take a tile: add the widget with a draft that places it.");
        }
        return Check(root.ToJsonString(Indented), ViewKind.Dashboard, options);
    }

    private static double Number(JsonNode? node, double fallback) =>
        node is not null && double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : fallback;

    /// <summary>The document as JSON, from stored text that may be JSON or YAML.</summary>
    internal static JsonObject? Read(string text)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var node = JsonInput.ReadByExtension(text, text.TrimStart().StartsWith('{') ? "draft.json" : "draft.yaml", diagnostics);
        return node is null || diagnostics.HasErrors() ? null : ViewsLoader.ToJson(node) as JsonObject;
    }

    private static DraftResult Refused(IEnumerable<ConfigDiagnostic> diagnostics)
    {
        var errors = diagnostics.Where(d => d.Severity == ConfigSeverity.Error).ToList();
        var shown = errors.Take(MaximumReported).Select(Describe).ToList();
        var more = errors.Count > shown.Count ? $" ({errors.Count - shown.Count} more)" : "";
        return new(false, "The document was not accepted and nothing was stored. Fix these and check it again: " + string.Join(" ", shown) + more);
    }

    private static string Describe(ConfigDiagnostic d) =>
        $"[{d.Code}] {(string.IsNullOrEmpty(d.KeyPath) ? "" : d.KeyPath + " ")}{(d.Line > 0 ? $"(line {d.Line}) " : "")}{d.Message}{(d.Fix is null ? "" : " " + d.Fix)}";
}

/// <summary>
/// The tools' own user store (plan 32 DBR12): a draft the person approved is saved as <b>their</b> dashboard or report (a version of it, shareable and promotable like any other), a layout is added to one of theirs as a new version, a pin is a new version with the widget. Each
/// method with <c>commit: false</c> only checks, which is what the runtime runs before it asks the person; with <c>true</c> it stores. A conflict (the name is taken, the document changed since it was read) is a refusal in words, never an exception.
/// </summary>
public sealed class UserViewDraftService(UserViewStore store, string owner, string ownerName, string savedBy, DraftOptions options)
{
    public DraftResult Validate(string? kind, JsonObject document) => AgentViewDrafts.Validate(document, ParseKind(kind), options);

    public DraftResult Draft(string kind, string name, JsonObject document, bool commit)
    {
        if (!UserViewStore.IsName(name)) return new(false, $"`{name}` is not a name a document can have: lower case letters, digits, '_' and '-', starting with a letter, at most {UserViewStore.MaxNameLength} characters.");
        var checkedDraft = AgentViewDrafts.Validate(document, ParseKind(kind), options);
        if (!checkedDraft.Ok || !commit) return checkedDraft;
        return Store(kind, name, checkedDraft, null, "saved as your");
    }

    public DraftResult AddLayout(string kind, string name, JsonObject layout, bool commit)
    {
        if (store.Get(owner, kind, name) is not { } current) return new(false, $"You have no {kind} `{name}` to add a layout to; draft it first.");
        var result = AgentViewDrafts.AddLayout(current.Text, layout, options);
        return !result.Ok || !commit ? result : Store(kind, name, result, current.Version, $"layout `{layout["name"]}` added to your");
    }

    public DraftResult Pin(string name, string widgetId, JsonObject widget, bool commit)
    {
        if (store.Get(owner, ViewDefinitions.Dashboard, name) is not { } current) return new(false, $"You have no dashboard `{name}` to pin to; draft one first.");
        var result = AgentViewDrafts.Pin(current.Text, widgetId, widget, options);
        return !result.Ok || !commit ? result : Store(ViewDefinitions.Dashboard, name, result, current.Version, $"`{widgetId}` pinned to your");
    }

    private DraftResult Store(string kind, string name, DraftResult checkedDraft, int? expected, string what)
    {
        try
        {
            var saved = store.Save(owner, kind, name, checkedDraft.Title ?? name, checkedDraft.Text!, savedBy, expected, ownerName);
            return checkedDraft with { Message = $"The draft is {what} {kind} `{name}`, version {saved.Version}. It is yours; it is published only through the publishing of the suite." };
        }
        catch (UserViewConflictException conflict)
        {
            return new(false, conflict.Message + " Nothing was stored.");
        }
    }

    private static ViewKind? ParseKind(string? kind) => kind switch { ViewDefinitions.Dashboard => ViewKind.Dashboard, ViewDefinitions.Report => ViewKind.Report, _ => null };
}
