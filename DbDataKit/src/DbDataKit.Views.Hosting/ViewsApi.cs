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
/// <item><b>export</b>: a PDF of the layout the print medium selects (or the one named), drawn from the module's print page by <see cref="ViewsOptions.Exporter"/>; without an exporter, or without a browser, <c>export_unavailable</c> in words that name the setting, and the health says it too. <b>print</b>: the same page as HTML, for the browser's own print.</item>
/// </list>
/// Every call is audited (<c>view.list</c>, <c>view.open</c>, <c>view.render</c>, <c>view.export</c>: who, through what, which document and version, the outcome, never a parameter's value), and the answer is held back when the entry cannot be written.
/// While draining, new calls are refused with 503 and the ones in flight finish.
/// </summary>
public sealed class ViewsApi(ViewsPublications published, IServingServices services, ViewsOptions options, IViewsAudit audit, IDrainCoordinator drain)
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
    /// <param name="body">The request: <c>parameters</c> (by document parameter name), <c>layout</c> (omitted: chosen for <c>viewport</c> and <c>medium</c>, else the default), <c>only</c> (widget ids to resolve: the ones a changed parameter touches).</param>
    public async Task<ViewsAnswer> RenderAsync(ViewsPrincipal who, string kind, string name, JsonObject? body, string? clientAddress = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var found = Find(who, kind, name, ViewsAuditVerbs.Render, traceId);
            if (found.View is not { } view) return found.Refusal!;

            Dictionary<string, JsonNode?>? parameters = null;
            List<string>? only = null;
            string? layout = null;
            LayoutContext viewport = new();
            try
            {
                foreach (var key in body?.Select(p => p.Key) ?? [])
                    if (key is not ("parameters" or "layout" or "only" or "viewport" or "medium"))
                        throw new ArgumentException($"Unknown key `{key}`: a render request has `parameters`, `layout`, `only`, `viewport` and `medium`.");
                if (body?["parameters"] is { } p) parameters = (p as JsonObject ?? throw new ArgumentException("`parameters` must be an object of values by parameter name.")).ToDictionary(x => x.Key, x => x.Value?.DeepClone());
                if (body?["only"] is { } o) only = (o as JsonArray ?? throw new ArgumentException("`only` must be a list of widget ids.")).Select(x => x is JsonValue v && v.TryGetValue<string>(out var s) ? s : throw new ArgumentException("`only` must be a list of widget ids.")).ToList();
                if (body?["layout"] is { } l) layout = l is JsonValue lv && lv.TryGetValue<string>(out var ls) ? ls : throw new ArgumentException("`layout` must be a layout's name.");
                var medium = body?["medium"] is JsonValue mv && mv.TryGetValue<string>(out var ms) ? ms : "screen";
                double? Dimension(string key) => body?["viewport"] is JsonObject v && v[key] is JsonValue d && d.TryGetValue<double>(out var n) ? n : null;
                viewport = new LayoutContext(Dimension("width"), Dimension("height"), medium, layout);
            }
            catch (ArgumentException e)
            {
                return Refuse(who, ViewsAuditVerbs.Render, kind, view, 400, "invalid", e.Message, traceId);
            }

            var chosen = LayoutSelector.Select(view.Document, viewport);
            if (chosen.Layout is not { } picked)
                return Refuse(who, ViewsAuditVerbs.Render, kind, view, 400, ResolveCodes.UnknownLayout, chosen.Refusal ?? "No layout fits.", traceId);

            var context = ContextOf(who, clientAddress, traceId);
            try
            {
                var resolver = view.Resolver(services.Queries, Basis(), services.Entities);
                var rendered = await resolver.ResolveAsync(context, view.Document, picked.Name, parameters, only, cancellationToken).ConfigureAwait(false);
                var errors = rendered.Widgets.Where(w => rendered.Failed(w.Key)).Select(w => (w.Value["code"]?.GetValue<string>() ?? "error")).ToList();
                var detail = Detail(traceId, ("layout", picked.Name), ("widgets", rendered.Widgets.Count), ("errors", errors.Count), ("error_codes", errors.Distinct().Order(StringComparer.Ordinal).ToList()),
                    ("parameters", parameters?.Keys.Order(StringComparer.Ordinal).ToList()), ("only", only));
                return Finish(who, ViewsAuditVerbs.Render, kind, view.Name, view.Version, AuditEvent.Ok, detail, new ViewsAnswer(200, rendered.ToJson(), Definition(view)));
            }
            catch (ViewResolutionException e)
            {
                return Refuse(who, ViewsAuditVerbs.Render, kind, view, 400, e.Code, e.Message, traceId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException or AuditFailure))
            {
                // only the kind of an unexpected failure leaves the module: its words can name tables, servers and files
                Record(who, ViewsAuditVerbs.Render, kind, view.Name, view.Version, AuditEvent.Failed, Detail(traceId, ("error", e.GetType().Name)));
                Interlocked.Increment(ref refused);
                return ViewsAnswer.Error(500, "error", $"The render failed ({e.GetType().Name}).");
            }
        }
    }

    // ---- export ----

    /// <summary>The route the page and the browser load the print bundle and the fonts from (no authentication: code and fonts, no data).</summary>
    public const string PrintAssetRoute = "/api/v1/views/print/";

    /// <summary>
    /// A download. <c>pdf</c> (the default): the layout named, else the one the print medium selects (the print layout, or the default), resolved as the caller and drawn by the exporter; <c>parameters</c> are the document's by name.
    /// <c>svg</c> is a chart's export without a browser (DBR17) and is refused for now. The audit row names the format, layout, pages and the browser's version.
    /// </summary>
    public async Task<ViewsAnswer> ExportAsync(ViewsPrincipal who, string kind, string name, string? format, string? layout, IReadOnlyDictionary<string, JsonNode?>? parameters = null, string? clientAddress = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var found = Find(who, kind, name, ViewsAuditVerbs.Export, traceId);
            if (found.View is not { } view) return found.Refusal!;
            var chosen = format ?? "pdf";
            if (chosen != "pdf")
                return Refuse(who, ViewsAuditVerbs.Export, kind, view, 400, chosen == "svg" ? "format_unsupported" : "invalid",
                    chosen == "svg" ? "Export as svg is a chart's export without a browser (plan 32 DBR17) and is not built yet; `format=pdf` is." : $"`format` is `pdf` (or `svg`, not built yet), not `{chosen}`.", traceId, ("format", format), ("layout", layout));
            var exporter = options.Exporter;
            if (exporter is null)
                return Refuse(who, ViewsAuditVerbs.Export, kind, view, 501, ExportUnavailable,
                    $"Export of the {kind} '{view.Name}' is unavailable: export unavailable: it is off on this host (the views.export setting is not set).", traceId, ("format", format), ("layout", layout));
            if (!exporter.Availability.Available)
                await exporter.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (!exporter.Availability.Available)
                return Refuse(who, ViewsAuditVerbs.Export, kind, view, 501, ExportUnavailable, $"Export of the {kind} '{view.Name}' is unavailable: {exporter.Availability.Reason}", traceId, ("format", format), ("layout", layout));
            var (pageAssets, missing) = PrintPages.LoadAssets(options.PageAssets);
            if (pageAssets is null)
                return Refuse(who, ViewsAuditVerbs.Export, kind, view, 501, ExportUnavailable, $"Export of the {kind} '{view.Name}' is unavailable: export unavailable: {missing}", traceId, ("format", format), ("layout", layout));

            var prepared = await PrepareAsync(who, kind, view, ViewsAuditVerbs.Export, layout, parameters, clientAddress, traceId, cancellationToken).ConfigureAwait(false);
            if (prepared.Refusal is not null) return prepared.Refusal;
            var p = prepared.Page!;
            var asOf = p.Rendered["as_of"]?.GetValue<string>();
            var page = PrintPages.Html(view, p.Layout, p.Paper, p.Rendered, p.Parameters, who.Caller.Id, exporter.Fonts, "");
            var job = new PrintJob(view.Document.Title, page, pageAssets,
                new PdfLayout(p.Paper.WidthMm, p.Paper.HeightMm, p.Paper.Margins,
                    PrintPages.RunningTemplate(p.Paper.Header, p.Paper, view.Document.Title, asOf, who.Caller.Id, p.Parameters),
                    PrintPages.RunningTemplate(p.Paper.Footer, p.Paper, view.Document.Title, asOf, who.Caller.Id, p.Parameters), PreferCssPage: true));
            try
            {
                var result = await exporter.ExportPdfAsync(job, cancellationToken).ConfigureAwait(false);
                var detail = Detail(traceId, ("format", "pdf"), ("layout", p.Layout.Name), ("pages", result.Pages), ("bytes", result.Pdf.Length), ("browser", result.BrowserVersion), ("mode", exporter.Availability.Mode),
                    ("warnings", result.Warnings.Count), ("fonts", result.EmbeddedFonts), ("parameters", parameters?.Keys.Order(StringComparer.Ordinal).ToList()));
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
                Record(who, ViewsAuditVerbs.Export, kind, view.Name, view.Version, AuditEvent.Failed, Detail(traceId, ("format", "pdf"), ("layout", p.Layout.Name), ("code", code)));
                Interlocked.Increment(ref refused);
                return ViewsAnswer.Error(status, code, $"Export of the {kind} '{view.Name}' failed: {e.Message}", e.Code == "busy" ? new Dictionary<string, string> { ["Retry-After"] = "5" } : null);
            }
            catch (ExportException) { throw new OperationCanceledException(cancellationToken); }
        }
    }

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
            var prepared = await PrepareAsync(who, kind, view, ViewsAuditVerbs.Print, layout, parameters, clientAddress, traceId, cancellationToken).ConfigureAwait(false);
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

    private async Task<(PreparedPage? Page, ViewsAnswer? Refusal)> PrepareAsync(ViewsPrincipal who, string kind, PublishedView view, string verb, string? layout, IReadOnlyDictionary<string, JsonNode?>? parameters, string? clientAddress, string? traceId, CancellationToken cancellationToken)
    {
        var chosen = LayoutSelector.Select(view.Document, new LayoutContext(null, null, "print", layout));
        if (chosen.Layout is not { } picked)
            return (null, Refuse(who, verb, kind, view, 400, ResolveCodes.UnknownLayout, chosen.Refusal ?? "No layout fits.", traceId));
        try
        {
            var resolver = view.Resolver(services.Queries, Basis(), services.Entities);
            var rendered = await resolver.ResolveAsync(ContextOf(who, clientAddress, traceId), view.Document, picked.Name, parameters is null ? null : new Dictionary<string, JsonNode?>(parameters), null, cancellationToken).ConfigureAwait(false);
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

    private ResolverOptions Basis()
    {
        var b = options.Resolver;
        return new ResolverOptions
        {
            MaxWidgets = b.MaxWidgets, MaxRows = b.MaxRows, MaxParallelism = b.MaxParallelism, WidgetTimeout = b.WidgetTimeout, Time = options.Time, MaxImageBytes = b.MaxImageBytes,
            ModelSourcePrefix = b.ModelSourcePrefix, Cache = cache,
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
