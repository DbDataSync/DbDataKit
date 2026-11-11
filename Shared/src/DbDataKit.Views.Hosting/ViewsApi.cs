using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Hosting;
using DbDataKit.Serving;
using DbDataKit.State;
using DbDataKit.Views.Export;

namespace DbDataKit.Views.Hosting;

/// <summary>A body that is not JSON: a download (a PDF) or a page (the print page). <see cref="FileName"/> makes it an attachment.</summary>
public sealed record ViewsFile(byte[] Content, string ContentType, string? FileName = null);

/// <summary>An answer of the module with no transport in it: the status, the JSON body and any headers.</summary>
public sealed record ViewsAnswer(int Status, JsonNode Body, IReadOnlyDictionary<string, string>? Headers = null, ViewsFile? File = null)
{
    public static ViewsAnswer Error(int status, string code, string message, IReadOnlyDictionary<string, string>? headers = null) =>
        new(status, new JsonObject { ["code"] = code, ["message"] = message }, headers);
}

/// <summary>
/// What the routes of the <c>views</c> module do (plan 32 DBR11), with no web framework in it, so the apps host can call it in process with an app's narrowed principal as the listener calls it for a person:
/// <list type="bullet">
/// <item><b>list</b> and <b>open</b>: the published document of a kind (<c>text</c> as published, the document as JSON, the layouts, the version) as the channel serves it on this instance, for a document the caller may open;</item>
/// <item><b>render</b>: the layout in play (named, or chosen for the viewport), resolved by <see cref="WidgetResolver"/> through the serving core's <see cref="IServingQueries"/> <b>as the caller</b> (their roles and attributes, the channel,
/// a trace id), with the module's result cache under <see cref="WidgetCacheKey"/>;</item>
/// <item><b>export</b>: a PDF of the layout the print medium selects (or the one named), drawn from the module's print page by <see cref="ViewsOptions.Exporter"/>; without an exporter, or without a browser, <c>export_unavailable</c> in words that name the setting, and the health says it too. The same route gives the rows of a widget as CSV or xlsx (no browser) and a chart as SVG (the browser's drawing). <b>print</b>: the same page as HTML, for the browser's own print.</item>
/// </list>
/// Every call is audited (<c>view.list</c>, <c>view.open</c>, <c>view.render</c>, <c>view.export</c>: who, through what, which document and version, the outcome, never a parameter's value), and the answer is held back when the entry cannot be written.
/// While draining, new calls are refused with 503 and the ones in flight finish.
/// </summary>
public sealed partial class ViewsApi(ViewsPublications published, IServingServices services, ViewsOptions options, IViewsAudit audit, IDrainCoordinator drain)
{
    public const string ExportUnavailable = "export_unavailable";

    private readonly WidgetResultCache? cache = options.CacheTtl > TimeSpan.Zero ? new WidgetResultCache(options.CacheTtl, options.CacheEntries, options.Time) : null;
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> unexpected = new();
    private long requests;
    private long refused;

    public WidgetResultCache? Cache => cache;

    public long Requests => Interlocked.Read(ref requests);

    public long Refused => Interlocked.Read(ref refused);

    /// <summary>Widgets that failed in a way nobody classified, as <c>id: kind</c> (the last ones): the entry shows only the kind and the host's log has the rest.</summary>
    public IReadOnlyCollection<string> Unexpected => [.. unexpected];

    public static string Collection(string kind) => kind == ViewDefinitions.Report ? "reports" : "dashboards";

    // ---- list ----

    public ViewsAnswer List(ViewsPrincipal who, string kind, string? traceId = null)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var items = new JsonArray();
            foreach (var view in published.For(kind).All.Where(v => Narrowing(who, v) is null))
                items.Add(new JsonObject { ["name"] = view.Name, ["title"] = view.Document.Title, ["version"] = view.Version.ToString(CultureInfo.InvariantCulture) });
            return Finish(who, ViewsAuditVerbs.List, kind, null, null, AuditEvent.Ok, Detail(traceId, ("count", items.Count)), new ViewsAnswer(200, new JsonObject { ["items"] = items }));
        }
    }

    // ---- open ----

    public ViewsAnswer Open(ViewsPrincipal who, string kind, string name, string? traceId = null)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var found = Find(who, kind, name, ViewsAuditVerbs.Open, traceId);
            if (found.View is not { } view) return found.Refusal!;
            var layouts = new JsonArray();
            foreach (var layout in view.Document.Layouts)
                layouts.Add(new JsonObject { ["name"] = layout.Name, ["type"] = layout.Type.ToString().ToLowerInvariant(), ["default"] = layout.IsDefault });
            var body = new JsonObject
            {
                ["kind"] = kind,
                ["name"] = view.Name,
                ["title"] = view.Document.Title,
                ["version"] = view.Version.ToString(CultureInfo.InvariantCulture),
                ["as_of"] = Stamp(view.PublishedAt),
                ["text"] = view.Text,
                ["document"] = view.DocumentJson(),
                ["layouts"] = layouts,
            };
            return Finish(who, ViewsAuditVerbs.Open, kind, view.Name, view.Version, AuditEvent.Ok, Detail(traceId), new ViewsAnswer(200, body, Definition(view)));
        }
    }

    // ---- render ----

    /// <param name="who">The caller, narrowed when the token is an app's.</param>
    /// <param name="kind"><c>dashboard</c> or <c>report</c>.</param>
    /// <param name="name">The document's name.</param>
    /// <param name="clientAddress">Where the call came from, carried to the serving core.</param>
    /// <param name="traceId">The trace the call belongs to, carried to the serving core and the audit.</param>
    /// <param name="cancellationToken">Stops the resolution (a client that went away).</param>
    /// <param name="body">The request: <c>parameters</c> (by document parameter name), <c>layout</c> (omitted: chosen for <c>viewport</c> and <c>medium</c>, else the default), <c>only</c> (widget ids to resolve: the ones a changed parameter touches), <c>order</c> (a viewer's sort of a table the row limit cut, by widget id: <c>{"sales": {"by": "orders.revenue", "dir": "desc"}}</c>, DBR45).</param>
    public async Task<ViewsAnswer> RenderAsync(ViewsPrincipal who, string kind, string name, JsonObject? body, string? clientAddress = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var found = Find(who, kind, name, ViewsAuditVerbs.Render, traceId);
            if (found.View is not { } view) return found.Refusal!;
            return await RenderViewAsync(who, kind, view, body, ViewsAuditVerbs.Render, clientAddress, traceId, cancellationToken, isPublished: true).ConfigureAwait(false);
        }
    }

    /// <summary>A render request's <c>order</c>: an object of widget id to <c>{by, dir}</c> (<c>dir</c> is <c>asc</c> or <c>desc</c>, default <c>asc</c>).</summary>
    private static Dictionary<string, WidgetOrder> ReadOrders(JsonNode node)
    {
        const string Shape = "`order` must be an object of widget ids to `{\"by\": field, \"dir\": \"asc\" | \"desc\"}`.";
        var result = new Dictionary<string, WidgetOrder>(StringComparer.Ordinal);
        foreach (var (id, value) in node as JsonObject ?? throw new ArgumentException(Shape))
        {
            if (value is not JsonObject o || o["by"] is not JsonValue by || !by.TryGetValue<string>(out var field) || field.Length == 0) throw new ArgumentException(Shape);
            var dir = o["dir"] is JsonValue d && d.TryGetValue<string>(out var text) ? text : "asc";
            if (dir is not ("asc" or "desc")) throw new ArgumentException(Shape);
            result[id] = new WidgetOrder(field, dir == "desc");
        }
        return result;
    }

    /// <summary>The render of a document already found: a published one (the module's cache, the definition header) or a person's own (<paramref name="isPublished"/> false: no cache, no header, the audit says whose it is).</summary>
    private async Task<ViewsAnswer> RenderViewAsync(ViewsPrincipal who, string kind, PublishedView view, JsonObject? body, string verb, string? clientAddress, string? traceId, CancellationToken cancellationToken, bool isPublished)
    {
        Dictionary<string, JsonNode?>? parameters = null;
        List<string>? only = null;
        string? layout = null;
        Dictionary<string, WidgetOrder>? orders = null;
        LayoutContext viewport = new();
        try
        {
            foreach (var key in body?.Select(p => p.Key) ?? [])
                if (key is not ("parameters" or "layout" or "only" or "viewport" or "medium" or "order"))
                    throw new ArgumentException($"Unknown key `{key}`: a render request has `parameters`, `layout`, `only`, `order`, `viewport` and `medium`.");
            if (body?["order"] is { } ord) orders = ReadOrders(ord);
            if (body?["parameters"] is { } p) parameters = (p as JsonObject ?? throw new ArgumentException("`parameters` must be an object of values by parameter name.")).ToDictionary(x => x.Key, x => x.Value?.DeepClone());
            if (body?["only"] is { } o) only = (o as JsonArray ?? throw new ArgumentException("`only` must be a list of widget ids.")).Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("`only` must be a list of widget ids.")).ToList();
            if (body?["layout"] is { } l) layout = l is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : throw new ArgumentException("`layout` must be a layout's name.");
            var medium = body?["medium"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : "screen";
            double? Dimension(string key) => body?["viewport"] is JsonObject v && v[key] is JsonValue d && d.TryGetValue<double>(out var n) ? n : null;
            viewport = new LayoutContext(Dimension("width"), Dimension("height"), medium, layout);
        }
        catch (ArgumentException e)
        {
            return Refuse(who, verb, kind, view, 400, "invalid", e.Message, traceId);
        }

        var chosen = LayoutSelector.Select(view.Document, viewport);
        if (chosen.Layout is not { } picked)
            return Refuse(who, verb, kind, view, 400, ResolveCodes.UnknownLayout, chosen.Refusal ?? "No layout fits.", traceId);

        var context = ContextOf(who, clientAddress, traceId);
        try
        {
            var resolver = view.Resolver(services.Queries, Basis(cached: isPublished), services.Entities);
            var rendered = await resolver.ResolveAsync(context, view.Document, picked.Name, parameters, only, cancellationToken, orders).ConfigureAwait(false);
            var errors = rendered.Widgets.Where(w => rendered.Failed(w.Key)).Select(w => (w.Value["code"]?.GetValue<string>() ?? "error")).ToList();
            var detail = Detail(traceId, ("layout", picked.Name), ("widgets", rendered.Widgets.Count), ("errors", errors.Count), ("error_codes", errors.Distinct().Order(StringComparer.Ordinal).ToList()),
                ("parameters", parameters?.Keys.Order(StringComparer.Ordinal).ToList()), ("only", only));
            return Finish(who, verb, kind, view.Name, view.Version, AuditEvent.Ok, detail, new ViewsAnswer(200, rendered.ToJson(), isPublished ? Definition(view) : null));
        }
        catch (ViewResolutionException e)
        {
            return Refuse(who, verb, kind, view, 400, e.Code, e.Message, traceId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AuditFailure))
        {
            // only the kind of an unexpected failure leaves the module: its words can name tables, servers and files
            Record(who, verb, kind, view.Name, view.Version, AuditEvent.Failed, Detail(traceId, ("error", e.GetType().Name)));
            Interlocked.Increment(ref refused);
            return ViewsAnswer.Error(500, "error", $"The render failed ({e.GetType().Name}).");
        }
    }

    // ---- preview ----

    /// <summary>The name a draft has in the audit and in the checks: it is nobody's document, and it is never stored.</summary>
    public const string DraftName = "draft";

    private static readonly System.Text.RegularExpressions.Regex WidgetIdPattern = new("^[A-Za-z][A-Za-z0-9_-]*$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The live preview of an unsaved document or widget (plan 32 DBR28): what the editor holds, resolved **as the caller** with the module's limits, and stored nowhere (the registry, the files and the result cache are not touched).
    /// <para>
    /// The body is one of two shapes. <b>A document</b>: <c>document</c> (the document as a JSON object) or <c>text</c> (its YAML or JSON), with <c>layout</c> (omitted: chosen for <c>viewport</c> and <c>medium</c>, else the default),
    /// <c>only</c> and <c>parameters</c> as a render takes them; the answer is the layout's rendered widgets. <b>A widget</b>: <c>widget</c> (a widget as a JSON object) with an optional <c>widget_id</c> (default <c>preview</c>), an optional <c>document</c>
    /// whose parameters the widget may use, and <c>parameters</c>; the widget is placed alone on a layout of its own and the answer holds its entry. <c>kind</c> is <c>dashboard</c> (the default) or <c>report</c>.
    /// </para>
    /// <para>
    /// It <b>refuses what a publish would refuse</b>: the draft is checked by the registry's validator for the kind (the loader, then the catalog check over the model versions the channel serves, saved queries, control
    /// attributes, pictures) and a document with a problem is a 400 <c>invalid</c> in the publish's words with the loader's diagnostics placed in the text. An app's narrowed token is held to its manifest's models as for a render.
    /// What the caller may not read is not a refusal of the request: that widget is an error entry, as in a render. Every call is audited (<c>view.preview</c>: who, through what, the shape, the layout, the counts; never the text or a value).
    /// </para>
    /// </summary>
    public async Task<ViewsAnswer> PreviewAsync(ViewsPrincipal who, JsonObject? body, string? clientAddress = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var kind = ViewDefinitions.Dashboard;
            string shape = "document", text;
            string? layout = null, widgetId = null;
            List<string>? only = null;
            Dictionary<string, JsonNode?>? parameters = null;
            LayoutContext viewport = new();
            try
            {
                if (body is null) throw new ArgumentException("A preview request has a `document` (or its `text`), or a `widget`.");
                foreach (var key in body.Select(p => p.Key))
                    if (key is not ("kind" or "text" or "document" or "widget" or "widget_id" or "layout" or "only" or "parameters" or "viewport" or "medium"))
                        throw new ArgumentException($"Unknown key `{key}`: a preview request has `kind`, `document` or `text`, `widget`, `widget_id`, `layout`, `only`, `parameters`, `viewport` and `medium`.");
                var baseDocument = body["document"] switch
                {
                    null => null,
                    JsonObject o => o,
                    _ => throw new ArgumentException("`document` must be the document as an object; send its text as `text`."),
                };
                if (body["kind"] is { } k) kind = k is JsonValue kv && kv.TryGetValue<string>(out var ks) && ViewDefinitions.Kinds.Contains(ks) ? ks : throw new ArgumentException("`kind` is `dashboard` or `report`.");
                else if (baseDocument?["kind"] is JsonValue dk && dk.TryGetValue<string>(out var documentKind) && ViewDefinitions.Kinds.Contains(documentKind)) kind = documentKind;
                if (body["parameters"] is { } p) parameters = (p as JsonObject ?? throw new ArgumentException("`parameters` must be an object of values by parameter name.")).ToDictionary(x => x.Key, x => x.Value?.DeepClone());
                if (body["only"] is { } o2) only = (o2 as JsonArray ?? throw new ArgumentException("`only` must be a list of widget ids.")).Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("`only` must be a list of widget ids.")).ToList();
                if (body["layout"] is { } l) layout = l is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : throw new ArgumentException("`layout` must be a layout's name.");
                var medium = body["medium"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : "screen";
                double? Dimension(string key) => body["viewport"] is JsonObject v && v[key] is JsonValue d && d.TryGetValue<double>(out var n) ? n : null;
                viewport = new LayoutContext(Dimension("width"), Dimension("height"), medium, layout);

                if (body["widget"] is { } w)
                {
                    shape = "widget";
                    if (body["text"] is not null || body["only"] is not null || body["layout"] is not null) throw new ArgumentException("A `widget` preview takes `widget`, `widget_id`, `document` (for its parameters) and `parameters` only; to preview a document, send `document` or `text` and no `widget`.");
                    if (w is not JsonObject widget) throw new ArgumentException("`widget` must be a widget as an object.");
                    widgetId = body["widget_id"] is null ? "preview" : body["widget_id"] is JsonValue wi && wi.TryGetValue<string>(out var wid) ? wid : throw new ArgumentException("`widget_id` must be a name.");
                    if (!WidgetIdPattern.IsMatch(widgetId)) throw new ArgumentException($"`{widgetId}` is not a name for a widget: use a letter, then letters, digits, `_` or `-`.");
                    text = Synthetic(kind, widgetId, widget, baseDocument);
                    only = null;
                    layout = null;
                    viewport = new LayoutContext(null, null, kind == ViewDefinitions.Report ? "print" : "screen", null);
                }
                else
                {
                    if (body["widget_id"] is not null) throw new ArgumentException("`widget_id` goes with a `widget`.");
                    if (body["text"] is not null && baseDocument is not null) throw new ArgumentException("Send the document as `document` or as `text`, not both.");
                    text = body["text"] is { } t ? t is JsonValue tv && tv.TryGetValue<string>(out var ts) ? ts : throw new ArgumentException("`text` must be the document's text.")
                        : baseDocument?.ToJsonString() ?? throw new ArgumentException("A preview request has a `document` (or its `text`), or a `widget`.");
                }
            }
            catch (ArgumentException e)
            {
                return RefuseDraft(who, kind, 400, "invalid", e.Message, traceId, ("shape", shape));
            }

            var draft = published.Draft(kind, DraftName, text, out var refusal, out var diagnostics);
            if (draft is null)
            {
                var answer = new JsonObject { ["code"] = "invalid", ["message"] = refusal, ["diagnostics"] = new JsonArray(diagnostics.Select(Diagnostic).ToArray()) };
                Record(who, ViewsAuditVerbs.Preview, kind, DraftName, null, AuditEvent.Refused, Detail(traceId, ("code", "invalid"), ("shape", shape), ("problems", diagnostics.Count)));
                Interlocked.Increment(ref refused);
                return new ViewsAnswer(400, answer);
            }
            if (Narrowing(who, draft) is { } why)
            {
                Record(who, ViewsAuditVerbs.Preview, kind, DraftName, null, AuditEvent.Refused, Detail(traceId, ("code", "denied"), ("reason", "app_scope"), ("shape", shape)));
                Interlocked.Increment(ref refused);
                return ViewsAnswer.Error(403, "denied", why);
            }

            var chosen = LayoutSelector.Select(draft.Document, viewport);
            if (chosen.Layout is not { } picked)
                return RefuseDraft(who, kind, 400, ResolveCodes.UnknownLayout, chosen.Refusal ?? "No layout fits.", traceId, ("shape", shape));

            var context = ContextOf(who, clientAddress, traceId);
            try
            {
                // never the module's cache: an author editing a query must see rows, not what an earlier draft left, and nothing of a draft is kept
                var resolver = draft.Resolver(services.Queries, Basis(cached: false), services.Entities);
                var rendered = await resolver.ResolveAsync(context, draft.Document, picked.Name, parameters, only, cancellationToken).ConfigureAwait(false);
                var errors = rendered.Widgets.Where(w => rendered.Failed(w.Key)).Select(w => (w.Value["code"]?.GetValue<string>() ?? "error")).ToList();
                var detail = Detail(traceId, ("shape", shape), ("layout", picked.Name), ("widgets", rendered.Widgets.Count), ("errors", errors.Count), ("error_codes", errors.Distinct().Order(StringComparer.Ordinal).ToList()),
                    ("parameters", parameters?.Keys.Order(StringComparer.Ordinal).ToList()), ("only", only));
                return Finish(who, ViewsAuditVerbs.Preview, kind, DraftName, null, AuditEvent.Ok, detail, new ViewsAnswer(200, rendered.ToJson()));
            }
            catch (ViewResolutionException e)
            {
                return RefuseDraft(who, kind, 400, e.Code, e.Message, traceId, ("shape", shape));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AuditFailure))
            {
                Record(who, ViewsAuditVerbs.Preview, kind, DraftName, null, AuditEvent.Failed, Detail(traceId, ("error", e.GetType().Name), ("shape", shape)));
                Interlocked.Increment(ref refused);
                return ViewsAnswer.Error(500, "error", $"The preview failed ({e.GetType().Name}).");
            }
        }
    }

    /// <summary>A document of one widget: the widget on a layout of its own that places it, with the parameters of <paramref name="baseDocument"/> (the document it belongs to) so the widget may use them.</summary>
    private static string Synthetic(string kind, string id, JsonObject widget, JsonObject? baseDocument)
    {
        var document = new JsonObject { ["schemaVersion"] = ViewsFormat.Current, ["kind"] = kind, ["title"] = "Preview" };
        if (baseDocument?["parameters"] is JsonArray parameters) document["parameters"] = parameters.DeepClone();
        document["widgets"] = new JsonObject { [id] = widget.DeepClone() };
        document["layouts"] = new JsonArray(kind == ViewDefinitions.Report
            ? new JsonObject { ["name"] = "preview", ["type"] = "flow", ["default"] = true, ["sections"] = new JsonArray(new JsonObject { ["blocks"] = new JsonArray(new JsonObject { ["widget"] = id }) }) }
            : new JsonObject { ["name"] = "preview", ["type"] = "reflow", ["default"] = true, ["grid"] = new JsonObject { ["columns"] = 12 }, ["tiles"] = new JsonArray(new JsonObject { ["widget"] = id, ["x"] = 0, ["y"] = 0, ["w"] = 12, ["h"] = 4 }) });
        return document.ToJsonString();
    }

    private static JsonNode Diagnostic(DbDataKit.Config.Yaml.ConfigDiagnostic d)
    {
        var o = new JsonObject { ["code"] = d.Code, ["severity"] = "error", ["message"] = d.Message };
        if (d.KeyPath is not null) o["path"] = d.KeyPath;
        if (d.Line > 0) { o["line"] = d.Line; o["column"] = d.Column; }
        return o;
    }

    private ViewsAnswer RefuseDraft(ViewsPrincipal who, string kind, int status, string code, string message, string? traceId, params (string Key, object? Value)[] more)
    {
        Record(who, ViewsAuditVerbs.Preview, kind, DraftName, null, AuditEvent.Refused, Detail(traceId, [("code", code), .. more]));
        Interlocked.Increment(ref refused);
        return ViewsAnswer.Error(status, code, message);
    }

    // ---- export ----

    /// <summary>The route the page and the browser load the print bundle and the fonts from (no authentication: code and fonts, no data).</summary>
    public const string PrintAssetRoute = "/api/v1/views/print/";

    /// <summary>The formats of a download (<c>format=</c>).</summary>
    public static readonly IReadOnlyList<string> Formats = ["pdf", "csv", "xlsx", "svg"];

    /// <summary>
    /// A download (plan 32 DBR15, DBR17); every one is resolved as the caller and audited (<c>view.export</c>, with the format, layout, widget and what came out; a refusal is audited too). <c>parameters</c> are the document's by name; the layout is the one
    /// named, else the one the print medium selects (the print layout, or the default).
    /// <list type="bullet">
    /// <item><c>pdf</c> (the default): the whole layout, drawn from the print page by the exporter (needs the browser).</item>
    /// <item><c>csv</c>: the rows of the table or matrix <paramref name="widget"/> (or the data of a chart) as the caller was shown them; <c>xlsx</c>: the same for one widget, or with no <paramref name="widget"/> one sheet for each table, matrix and chart the layout places and, first, a <c>Metrics</c> sheet with a row for each metric (widget, label, value, comparison, change). No browser.
    /// A widget that failed gives its error block as an xlsx sheet, and for a CSV is refused with its code and words.</item>
    /// <item><c>svg</c>: the chart <paramref name="widget"/> as the print page draws it (needs the browser: the module has no JavaScript of its own, so the picture is the browser's, the same as the screen and the PDF show).</item>
    /// </list>
    /// </summary>
    public async Task<ViewsAnswer> ExportAsync(ViewsPrincipal who, string kind, string name, string? format, string? layout, string? widget, IReadOnlyDictionary<string, JsonNode?>? parameters = null, string? clientAddress = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var found = Find(who, kind, name, ViewsAuditVerbs.Export, traceId);
            if (found.View is not { } view) return found.Refusal!;
            var chosen = format ?? "pdf";
            var more = new (string, object?)[] { ("format", chosen), ("layout", layout), ("widget", widget) };
            if (!Formats.Contains(chosen, StringComparer.Ordinal))
                return Refuse(who, ViewsAuditVerbs.Export, kind, view, 400, "invalid", $"`format` is `pdf`, `csv`, `xlsx` or `svg`, not `{chosen}`.", traceId, more);
            if (chosen is "csv" or "svg" && widget is null)
                return Refuse(who, ViewsAuditVerbs.Export, kind, view, 400, "invalid", $"`format={chosen}` is one widget's: name it with `widget=<id>` (`xlsx` alone gives every table, matrix and chart of the layout).", traceId, more);
            if (chosen == "pdf" && widget is not null)
                return Refuse(who, ViewsAuditVerbs.Export, kind, view, 400, "invalid", "`widget` names one widget for `csv`, `xlsx` and `svg`; a PDF is the whole layout.", traceId, more);
            if (widget is not null && view.Document.Widget(widget) is null)
                return Refuse(who, ViewsAuditVerbs.Export, kind, view, 404, "widget_not_found", $"The {kind} '{view.Name}' has no widget '{widget}'.", traceId, more);

            if (chosen is "csv" or "xlsx")
                return await DataAsync(who, kind, view, chosen, layout, widget, parameters, clientAddress, traceId, cancellationToken).ConfigureAwait(false);

            var (exporter, pageAssets, unavailable) = await BrowserAsync(who, kind, view, traceId, more, cancellationToken).ConfigureAwait(false);
            if (unavailable is not null) return unavailable;

            var prepared = await PrepareAsync(who, kind, view, ViewsAuditVerbs.Export, layout, parameters, null, clientAddress, traceId, cancellationToken).ConfigureAwait(false);
            if (prepared.Refusal is not null) return prepared.Refusal;
            var p = prepared.Page!;
            var asOf = p.Rendered["as_of"]?.GetValue<string>();
            var page = PrintPages.Html(view, p.Layout, p.Paper, p.Rendered, p.Parameters, who.Caller.Id, exporter!.Fonts, "");
            var job = new PrintJob(view.Document.Title, page, pageAssets!,
                new PdfLayout(p.Paper.WidthMm, p.Paper.HeightMm, p.Paper.Margins,
                    PrintPages.RunningTemplate(p.Paper.Header, p.Paper, view.Document.Title, asOf, who.Caller.Id, p.Parameters),
                    PrintPages.RunningTemplate(p.Paper.Footer, p.Paper, view.Document.Title, asOf, who.Caller.Id, p.Parameters), PreferCssPage: true));
            var parameterNames = parameters?.Keys.Order(StringComparer.Ordinal).ToList();
            try
            {
                if (chosen == "svg")
                {
                    var node = (p.Rendered["widgets"] as JsonObject)?[widget!] as JsonObject;
                    if (node is null)
                        return Refuse(who, ViewsAuditVerbs.Export, kind, view, 400, "widget_not_placed", NotPlaced(view, p.Layout.Name, widget!), traceId, more);
                    if (node["type"]?.GetValue<string>() == "error") return WidgetFailed(who, kind, view, widget!, node, traceId, more);
                    if (node["type"]?.GetValue<string>() != "chart")
                        return Refuse(who, ViewsAuditVerbs.Export, kind, view, 400, "widget_not_chart", $"The widget '{widget}' is a {node["type"]}, not a chart: `format=svg` is a chart's picture (`csv` and `xlsx` give a table's or matrix's rows).", traceId, more);
                    var svg = await exporter.ExportSvgAsync(job, widget!, cancellationToken).ConfigureAwait(false);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(svg.Svg);
                    var svgDetail = Detail(traceId, ("format", "svg"), ("layout", p.Layout.Name), ("widget", widget), ("bytes", bytes.Length), ("browser", svg.BrowserVersion), ("mode", exporter.Availability.Mode),
                        ("warnings", svg.Warnings.Count), ("parameters", parameterNames));
                    var svgHeaders = new Dictionary<string, string>(Definition(view)) { ["X-DbData-Export-Browser"] = svg.BrowserVersion };
                    if (svg.Warnings.Count > 0) svgHeaders["X-DbData-Export-Warnings"] = svg.Warnings.Count.ToString(CultureInfo.InvariantCulture);
                    return Finish(who, ViewsAuditVerbs.Export, kind, view.Name, view.Version, AuditEvent.Ok, svgDetail,
                        new ViewsAnswer(200, new JsonObject(), svgHeaders, new ViewsFile(bytes, "image/svg+xml", $"{view.Name}-{widget}.svg")));
                }

                var result = await exporter.ExportPdfAsync(job, cancellationToken).ConfigureAwait(false);
                var detail = Detail(traceId, ("format", "pdf"), ("layout", p.Layout.Name), ("pages", result.Pages), ("bytes", result.Pdf.Length), ("browser", result.BrowserVersion), ("mode", exporter.Availability.Mode),
                    ("warnings", result.Warnings.Count), ("fonts", result.EmbeddedFonts), ("parameters", parameterNames));
                var headers = new Dictionary<string, string>(Definition(view)) { ["X-DbData-Export-Pages"] = result.Pages.ToString(CultureInfo.InvariantCulture), ["X-DbData-Export-Browser"] = result.BrowserVersion };
                if (result.Warnings.Count > 0) headers["X-DbData-Export-Warnings"] = result.Warnings.Count.ToString(CultureInfo.InvariantCulture);
                return Finish(who, ViewsAuditVerbs.Export, kind, view.Name, view.Version, AuditEvent.Ok, detail,
                    new ViewsAnswer(200, new JsonObject(), headers, new ViewsFile(result.Pdf, "application/pdf", $"{view.Name}-{p.Layout.Name}.pdf")));
            }
            catch (ExportException e) when (e.Code != "cancelled")
            {
                var (status, code) = e.Code switch
                {
                    "unavailable" => (501, ExportUnavailable),
                    "deadline" => (504, "export_timeout"),
                    "pages" => (413, "export_too_large"),
                    "busy" => (503, "export_busy"),
                    _ => (500, "export_failed"),
                };
                Record(who, ViewsAuditVerbs.Export, kind, view.Name, view.Version, AuditEvent.Failed, Detail(traceId, ("format", chosen), ("layout", p.Layout.Name), ("widget", widget), ("code", code)));
                Interlocked.Increment(ref refused);
                return ViewsAnswer.Error(status, code, $"Export of the {kind} '{view.Name}' failed: {e.Message}", e.Code == "busy" ? new Dictionary<string, string> { ["Retry-After"] = "5" } : null);
            }
            catch (ExportException) { throw new OperationCanceledException(cancellationToken); }
        }
    }

    /// <summary>The exporter and the print page's files when this host can draw one, else the 501 that says why (the setting is named).</summary>
    private async Task<(IReportExporter? Exporter, IReadOnlyDictionary<string, PrintAsset>? Assets, ViewsAnswer? Refusal)> BrowserAsync(ViewsPrincipal who, string kind, PublishedView view, string? traceId, (string, object?)[] more, CancellationToken cancellationToken)
    {
        var exporter = options.Exporter;
        if (exporter is null)
            return (null, null, Refuse(who, ViewsAuditVerbs.Export, kind, view, 501, ExportUnavailable,
                $"Export of the {kind} '{view.Name}' is unavailable: export unavailable: it is off on this host (the views.export setting is not set).", traceId, more));
        if (!exporter.Availability.Available)
            await exporter.CheckAsync(cancellationToken).ConfigureAwait(false);
        if (!exporter.Availability.Available)
            return (null, null, Refuse(who, ViewsAuditVerbs.Export, kind, view, 501, ExportUnavailable, $"Export of the {kind} '{view.Name}' is unavailable: {exporter.Availability.Reason}", traceId, more));
        var (pageAssets, missing) = PrintPages.LoadAssets(options.PageAssets);
        if (pageAssets is null)
            return (null, null, Refuse(who, ViewsAuditVerbs.Export, kind, view, 501, ExportUnavailable, $"Export of the {kind} '{view.Name}' is unavailable: export unavailable: {missing}", traceId, more));
        return (exporter, pageAssets, null);
    }

    private static string NotPlaced(PublishedView view, string layout, string widget) =>
        $"The layout '{layout}' of the {view.Kind} '{view.Name}' does not place the widget '{widget}': name a layout that does with `layout=`.";

    /// <summary>A widget that failed has no rows or picture to give: the answer is its error block (code and words), 422.</summary>
    private ViewsAnswer WidgetFailed(ViewsPrincipal who, string kind, PublishedView view, string widget, JsonObject node, string? traceId, (string, object?)[] more)
    {
        var code = node["code"]?.GetValue<string>() ?? "error";
        Record(who, ViewsAuditVerbs.Export, kind, view.Name, view.Version, AuditEvent.Refused, Detail(traceId, [("code", "widget_failed"), ("widget_code", code), .. more]));
        Interlocked.Increment(ref refused);
        return new ViewsAnswer(422, new JsonObject { ["code"] = "widget_failed", ["message"] = $"The widget '{widget}' could not be resolved ({code}): {node["message"]?.GetValue<string>()}", ["widget"] = node.DeepClone() });
    }

    private async Task<ViewsAnswer> DataAsync(ViewsPrincipal who, string kind, PublishedView view, string format, string? layout, string? widget, IReadOnlyDictionary<string, JsonNode?>? parameters, string? clientAddress, string? traceId, CancellationToken cancellationToken)
    {
        var more = new (string, object?)[] { ("format", format), ("layout", layout), ("widget", widget) };
        var prepared = await PrepareAsync(who, kind, view, ViewsAuditVerbs.Export, layout, parameters, widget is null ? null : [widget], clientAddress, traceId, cancellationToken).ConfigureAwait(false);
        if (prepared.Refusal is not null) return prepared.Refusal;
        var p = prepared.Page!;
        var widgets = p.Rendered["widgets"] as JsonObject ?? [];
        var sheets = new List<DataSheet>();
        if (widget is not null)
        {
            if (widgets[widget] is not JsonObject node)
                return Refuse(who, ViewsAuditVerbs.Export, kind, view, 400, "widget_not_placed", NotPlaced(view, p.Layout.Name, widget), traceId, more);
            if (format == "csv" && node["type"]?.GetValue<string>() == "error") return WidgetFailed(who, kind, view, widget, node, traceId, more);
            if (DataExports.Sheet(widget, node, AxisNames(view, widget)) is not { } sheet)
                return Refuse(who, ViewsAuditVerbs.Export, kind, view, 400, "widget_not_tabular", $"The widget '{widget}' is a {node["type"]}: it has no rows (`csv` and `xlsx` are for a table, a matrix or a chart's data).", traceId, more);
            sheets.Add(sheet);
        }
        else
        {
            var metrics = new List<(string Id, string? Label, JsonObject Node)>();
            foreach (var (id, node) in widgets)
            {
                if (node is not JsonObject o) continue;
                if (o["type"]?.GetValue<string>() == "metric") metrics.Add((id, view.Document.Widget(id)?.Raw["label"]?.GetValue<string>(), o));
                else if (DataExports.Sheet(id, o, AxisNames(view, id)) is { } sheet) sheets.Add(sheet);
            }
            // the metrics of the layout are one sheet of their own, first: a metric alone has no rows, but the figures a dashboard opens with belong in its workbook
            if (DataExports.Metrics(metrics) is { } summary) sheets.Insert(0, summary);
        }
        if (sheets.Count == 0)
            return Refuse(who, ViewsAuditVerbs.Export, kind, view, 400, "nothing_tabular", $"The layout '{p.Layout.Name}' of the {kind} '{view.Name}' places no table, matrix or chart: there are no rows to export.", traceId, more);

        var rows = sheets.Sum(x => (long)x.Rows.Count);
        var most = Math.Min(options.MaxExportRows, format == "xlsx" ? DataExports.XlsxMaxRows : long.MaxValue);
        if (rows > most)
            return Refuse(who, ViewsAuditVerbs.Export, kind, view, 413, "export_too_large",
                $"The export has {rows} rows and a {format} export is limited to {most} (ViewsOptions.MaxExportRows" + (format == "xlsx" ? $" and the {DataExports.XlsxMaxRows} rows of a sheet" : "") + "): narrow it with a parameter, or export a smaller widget.", traceId, [.. more, ("rows", rows)]);

        var content = format == "csv" ? DataExports.Csv(sheets[0]) : DataExports.Xlsx(sheets);
        var errors = sheets.Count(x => x.Kind == "error");
        var detail = Detail(traceId, ("format", format), ("layout", p.Layout.Name), ("widget", widget), ("sheets", sheets.Count), ("rows", rows), ("errors", errors), ("bytes", content.Length),
            ("parameters", parameters?.Keys.Order(StringComparer.Ordinal).ToList()));
        var headers = new Dictionary<string, string>(Definition(view)) { ["X-DbData-Export-Rows"] = rows.ToString(CultureInfo.InvariantCulture) };
        var file = $"{view.Name}-{widget ?? p.Layout.Name}.{format}";
        return Finish(who, ViewsAuditVerbs.Export, kind, view.Name, view.Version, AuditEvent.Ok, detail,
            new ViewsAnswer(200, new JsonObject(), headers, new ViewsFile(content, format == "csv" ? DataExports.CsvContentType : DataExports.XlsxContentType, file)));
    }

    /// <summary>The names of a matrix's row-label columns: the fields of its <c>rows</c> encoding.</summary>
    private static IReadOnlyList<string> AxisNames(PublishedView view, string widget) =>
        view.Document.Widget(widget)?.Raw["encodings"]?["rows"] is JsonArray rows
            ? [.. rows.Select(r => r is JsonValue v && v.TryGetValue<string>(out var s) ? s : r?["field"]?.GetValue<string>() ?? "")]
            : [];

    /// <summary>The print page as HTML, for the browser's own print (and for looking at what the exporter draws): the same page, its files loaded from <see cref="PrintAssetRoute"/>.</summary>
    public async Task<ViewsAnswer> PrintPageAsync(ViewsPrincipal who, string kind, string name, string? layout, IReadOnlyDictionary<string, JsonNode?>? parameters = null, string? clientAddress = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var found = Find(who, kind, name, ViewsAuditVerbs.Print, traceId);
            if (found.View is not { } view) return found.Refusal!;
            var (pageAssets, missing) = PrintPages.LoadAssets(options.PageAssets);
            if (pageAssets is null)
                return Refuse(who, ViewsAuditVerbs.Print, kind, view, 501, ExportUnavailable, $"The print page of the {kind} '{view.Name}' is unavailable: {missing}", traceId, ("layout", layout));
            var prepared = await PrepareAsync(who, kind, view, ViewsAuditVerbs.Print, layout, parameters, null, clientAddress, traceId, cancellationToken).ConfigureAwait(false);
            if (prepared.Refusal is not null) return prepared.Refusal;
            var p = prepared.Page!;
            var html = PrintPages.Html(view, p.Layout, p.Paper, p.Rendered, p.Parameters, who.Caller.Id, options.Exporter?.Fonts, PrintAssetRoute);
            return Finish(who, ViewsAuditVerbs.Print, kind, view.Name, view.Version, AuditEvent.Ok, Detail(traceId, ("layout", p.Layout.Name), ("parameters", parameters?.Keys.Order(StringComparer.Ordinal).ToList())),
                new ViewsAnswer(200, new JsonObject(), Definition(view), new ViewsFile(System.Text.Encoding.UTF8.GetBytes(html), "text/html; charset=utf-8")));
        }
    }

    /// <summary>A file of the print page: <c>print.js</c>, <c>print.css</c> or a bundled font (<c>fonts/...</c>); null for anything else.</summary>
    public PrintAsset? PrintAsset(string path)
    {
        if (path.StartsWith("fonts/", StringComparison.Ordinal)) return options.Exporter?.Fonts?.Assets.GetValueOrDefault(path);
        if (path is not ("print.js" or "print.css")) return null;
        return PrintPages.LoadAssets(options.PageAssets).Assets?.GetValueOrDefault(path);
    }

    private sealed record PreparedPage(ViewLayout Layout, PrintPaper Paper, JsonObject Rendered, JsonObject Parameters);

    private async Task<(PreparedPage? Page, ViewsAnswer? Refusal)> PrepareAsync(ViewsPrincipal who, string kind, PublishedView view, string verb, string? layout, IReadOnlyDictionary<string, JsonNode?>? parameters, IReadOnlyCollection<string>? only, string? clientAddress, string? traceId, CancellationToken cancellationToken)
    {
        var chosen = LayoutSelector.Select(view.Document, new LayoutContext(null, null, "print", layout));
        if (chosen.Layout is not { } picked)
            return (null, Refuse(who, verb, kind, view, 400, ResolveCodes.UnknownLayout, chosen.Refusal ?? "No layout fits.", traceId));
        try
        {
            var resolver = view.Resolver(services.Queries, Basis(), services.Entities);
            var rendered = await resolver.ResolveAsync(ContextOf(who, clientAddress, traceId), view.Document, picked.Name, parameters is null ? null : new Dictionary<string, JsonNode?>(parameters), only, cancellationToken).ConfigureAwait(false);
            var values = new JsonObject();
            foreach (var (key, value) in parameters ?? new Dictionary<string, JsonNode?>()) values[key] = value?.DeepClone();
            return (new PreparedPage(picked, PrintPages.Paper(picked), rendered.ToJson(), values), null);
        }
        catch (ViewResolutionException e)
        {
            return (null, Refuse(who, verb, kind, view, 400, e.Code, e.Message, traceId));
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or StackOverflowException or AuditFailure))
        {
            Record(who, verb, kind, view.Name, view.Version, AuditEvent.Failed, Detail(traceId, ("error", e.GetType().Name)));
            Interlocked.Increment(ref refused);
            return (null, ViewsAnswer.Error(500, "error", $"The page could not be resolved ({e.GetType().Name})."));
        }
    }

    // ---- health ----

    /// <summary>The module's health as JSON (the shape of <c>/health</c>): state, whether it is draining, what is served, what is not, the cache and the export.</summary>
    public JsonObject Health(ServingHealth state)
    {
        var served = new JsonObject();
        foreach (var kind in ViewDefinitions.Kinds) served[Collection(kind)] = published.For(kind).All.Count;
        var unserved = new JsonArray();
        try
        {
            foreach (var u in published.Unserved())
                unserved.Add(new JsonObject { ["kind"] = u.Kind, ["name"] = u.Name, ["version"] = u.Head, ["served"] = u.Served });
        }
        catch (Exception e) when (e is not OutOfMemoryException) { unserved.Add(new JsonObject { ["error"] = $"The group's definitions could not be read ({e.GetType().Name})." }); }
        return new JsonObject
        {
            ["module"] = "views",
            ["state"] = state.State.ToString().ToLowerInvariant(),
            ["detail"] = state.Detail,
            ["draining"] = drain.IsDraining,
            ["in_flight"] = drain.ActiveWork,
            ["served"] = served,
            ["not_served"] = unserved,
            ["cache"] = new JsonObject { ["enabled"] = cache is not null, ["entries"] = cache?.Count ?? 0, ["hits"] = cache?.Hits ?? 0, ["misses"] = cache?.Misses ?? 0 },
            ["export"] = ExportHealth(),
            ["requests"] = Requests,
            ["refused"] = Refused,
        };
    }

    private JsonObject ExportHealth()
    {
        if (options.Exporter is not { } exporter) return new JsonObject { ["available"] = false, ["reason"] = "export unavailable: it is off on this host (the views.export setting is not set)" };
        var a = exporter.Availability;
        var (assets, missing) = PrintPages.LoadAssets(options.PageAssets);
        var reason = a.Available && assets is null ? "export unavailable: " + missing : a.Reason;
        return new JsonObject { ["available"] = a.Available && assets is not null, ["reason"] = reason, ["browser"] = a.BrowserVersion, ["mode"] = a.Mode };
    }

    // ---- the parts ----

    private IDisposable? Enter()
    {
        try
        {
            var work = drain.TrackWork();
            Interlocked.Increment(ref requests);
            return work;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static ViewsAnswer Draining() => ViewsAnswer.Error(503, "draining", "This instance is draining for an upgrade and takes no new requests; the next one is served by another instance.", new Dictionary<string, string> { ["Retry-After"] = "5" });

    private ResolverOptions Basis(bool cached = true)
    {
        var b = options.Resolver;
        return new ResolverOptions
        {
            MaxWidgets = b.MaxWidgets, MaxRows = b.MaxRows, MaxParallelism = b.MaxParallelism, WidgetTimeout = b.WidgetTimeout, Time = options.Time, MaxImageBytes = b.MaxImageBytes,
            ModelSourcePrefix = b.ModelSourcePrefix, FrameOrigins = b.FrameOrigins, Cache = cached ? cache : null,
            OnUnexpected = (id, e) =>
            {
                b.OnUnexpected?.Invoke(id, e);
                unexpected.Enqueue($"{id}: {e.GetType().Name}");
                while (unexpected.Count > 50) unexpected.TryDequeue(out _);
            },
        };
    }

    private ServingContext ContextOf(ViewsPrincipal who, string? clientAddress, string? traceId)
    {
        var caller = who.Caller;
        if (who.App is { } app && !caller.Claims.ContainsKey("app"))
            caller = caller with { Claims = new Dictionary<string, string>(caller.Claims) { ["app"] = app.Instance } };
        return new ServingContext(caller, options.Channel, TraceId: traceId, ClientAddress: clientAddress);
    }

    /// <summary>Why an app's narrowing refuses the document, or null: the first model the document queries that the app's manifest does not name.</summary>
    private static string? Narrowing(ViewsPrincipal who, PublishedView view)
    {
        if (who.App is not { } app) return null;
        var models = ViewDefinitions.Models(view.Document).Concat(ViewDefinitions.PinnedModels(view.DependsOn).Keys).Distinct(StringComparer.OrdinalIgnoreCase);
        var outside = models.FirstOrDefault(m => !app.Allows(m));
        return outside is null ? null : $"The app '{app.Instance}' may not read the model '{outside}' (its manifest's semantic models do not name it), so it may not use the {view.Kind} '{view.Name}'.";
    }

    private readonly record struct Found(PublishedView? View, ViewsAnswer? Refusal);

    private Found Find(ViewsPrincipal who, string kind, string name, string verb, string? traceId)
    {
        var view = published.For(kind).Find(name);
        if (view is null)
        {
            var answer = ViewsAnswer.Error(404, "not_found", $"There is no {kind} named '{name}' served here.");
            Record(who, verb, kind, name, null, AuditEvent.Refused, Detail(traceId, ("reason", "not_found")));
            Interlocked.Increment(ref refused);
            return new(null, answer);
        }
        if (Narrowing(who, view) is { } why)
        {
            var answer = ViewsAnswer.Error(403, "denied", why);
            Record(who, verb, kind, name, view.Version, AuditEvent.Refused, Detail(traceId, ("reason", "app_scope")));
            Interlocked.Increment(ref refused);
            return new(null, answer);
        }
        return new(view, null);
    }

    private ViewsAnswer Refuse(ViewsPrincipal who, string verb, string kind, PublishedView view, int status, string code, string message, string? traceId, params (string Key, object? Value)[] more)
    {
        Record(who, verb, kind, view.Name, view.Version, AuditEvent.Refused, Detail(traceId, [("code", code), .. more]));
        Interlocked.Increment(ref refused);
        return ViewsAnswer.Error(status, code, message);
    }

    private ViewsAnswer Finish(ViewsPrincipal who, string verb, string kind, string? name, int? version, string outcome, string detail, ViewsAnswer answer)
    {
        Record(who, verb, kind, name, version, outcome, detail);
        return answer;
    }

    /// <summary>Thrown when an entry cannot be written: what was asked is not answered (the caller is told, the data is not given).</summary>
    private sealed class AuditFailure(Exception inner) : Exception("The audit entry could not be written.", inner);

    private void Record(ViewsPrincipal who, string verb, string kind, string? name, int? version, string outcome, string detail)
    {
        var app = who.App;
        try
        {
            audit.Record(new AuditEvent("person", who.Caller.Id, verb, outcome, ViaKind: app is null ? "api" : "app", ViaId: app?.Instance,
                TargetKind: kind, TargetId: name is null ? null : version is { } v ? $"{name}@{v}" : name, Detail: detail));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new AuditFailure(e);
        }
    }

    private static string Detail(string? traceId, params (string Key, object? Value)[] parts)
    {
        var o = new JsonObject();
        if (traceId is not null) o["trace_id"] = traceId;
        foreach (var (key, value) in parts)
            if (value is not null) o[key] = JsonSerializer.SerializeToNode(value);
        return o.ToJsonString();
    }

    private static IReadOnlyDictionary<string, string> Definition(PublishedView view) =>
        new Dictionary<string, string> { ["X-DbData-Definition"] = $"{view.Kind}/{view.Name}@{view.Version}" };

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>Whether an exception is the module's own refusal to answer without an audit entry (the routes turn it into a 500 naming the audit).</summary>
    public static bool IsAuditFailure(Exception e) => e is AuditFailure;
}
