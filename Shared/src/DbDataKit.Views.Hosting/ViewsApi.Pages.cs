using System.Globalization;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.State;
using DbDataKit.Views.Export;

namespace DbDataKit.Views.Hosting;

public sealed partial class ViewsApi
{
    /// <summary>The kind a custom page is audited and subscribed under (plan 32 DBR34); its name is <c>&lt;instance&gt;.&lt;view name&gt;</c>, the instance and the manifest's name of the view, which no name of either contains a dot.</summary>
    public const string PageKind = "page";

    /// <summary>The formats of a custom page's export.</summary>
    public static readonly IReadOnlyList<string> PageFormats = ["pdf", "png"];

    /// <summary>
    /// A custom page (an app's <c>kind: page</c> view) exported as the person (plan 32 DBR34, with DBR32's print): the rows of the queries its manifest entry declares are run **as the caller** (access, limits and audit are the serving core's), the app's page is drawn
    /// as the top-level document by the exporter (<see cref="CustomViewPrint.Page"/>) and the answer is a PDF (<c>format=pdf</c>) or the picture of the same page (<c>png</c>), audited as <c>view.export</c> of the kind <c>page</c>. A caller who may not open the
    /// instance, or a page the host does not serve, is 404 <c>not_found</c> (a subscription then loses access); a query the core refused is 422 <c>query_failed</c> with its code and words naming the query; a page that cannot be drawn is <c>export_failed</c> in the module's words.
    /// </summary>
    public async Task<ViewsAnswer> ExportPageAsync(ViewsPrincipal who, string instance, string name, string? format, IReadOnlyDictionary<string, JsonNode?>? parameters = null, string? clientAddress = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        if (Enter() is not { } work) return Draining();
        using (work)
        {
            var target = $"{instance}.{name}";
            var chosen = format ?? "pdf";
            ViewsAnswer Refused(int status, string code, string message)
            {
                Record(who, ViewsAuditVerbs.Export, PageKind, target, null, AuditEvent.Refused, Detail(traceId, ("format", chosen), ("code", code)));
                Interlocked.Increment(ref refused);
                return ViewsAnswer.Error(status, code, message);
            }
            if (!PageFormats.Contains(chosen, StringComparer.Ordinal)) return Refused(400, "invalid", $"`format` of a custom page is `pdf` or `png`, not `{chosen}`.");
            var apps = options.CustomApps;
            var found = apps?.FindPage(instance, name);
            if (apps is null || found is null || !apps.MayOpen(who, instance)) return Refused(404, "not_found", $"There is no custom page '{name}' of '{instance}' served here for you.");
            var view = found.View;

            var exporter = options.Exporter;
            if (exporter is null) return Refused(501, ExportUnavailable, $"Export of the page '{target}' is unavailable: export unavailable: it is off on this host (the views.export setting is not set).");
            if (!exporter.Availability.Available) await exporter.CheckAsync(cancellationToken).ConfigureAwait(false);
            if (!exporter.Availability.Available) return Refused(501, ExportUnavailable, $"Export of the page '{target}' is unavailable: {exporter.Availability.Reason}");

            JsonObject resolved;
            try
            {
                var resolver = new WidgetResolver(services.Queries, _ => null, Basis(cached: false), services.Entities);
                resolved = await resolver.ResolveCustomViewAsync(ContextOf(who, clientAddress, traceId), view, $"{found.Files.App}/{view.Export}", parameters ?? new Dictionary<string, JsonNode?>(), cancellationToken).ConfigureAwait(false);
            }
            catch (CustomViewException e)
            {
                return Refused(e.Code is "denied" or "unauthenticated" ? 403 : 422, e.Code is "denied" or "unauthenticated" ? "denied" : "query_failed", $"The page '{target}' could not be resolved ({e.Code}): {e.Message}");
            }

            try
            {
                var job = CustomViewPrint.Page(view, found.Files, resolved, CustomViewPrint.PaperOf(view), view.Title ?? view.Name, exporter.Fonts);
                var names = parameters?.Keys.Order(StringComparer.Ordinal).ToList();
                if (chosen == PngFormat)
                {
                    var picture = await exporter.ExportPngAsync(job, PreviewMaxHeight, cancellationToken).ConfigureAwait(false);
                    return Finish(who, ViewsAuditVerbs.Export, PageKind, target, null, AuditEvent.Ok,
                        Detail(traceId, ("format", "png"), ("bytes", picture.Png.Length), ("width", picture.Width), ("height", picture.Height), ("browser", picture.BrowserVersion), ("mode", exporter.Availability.Mode), ("warnings", picture.Warnings.Count), ("parameters", names)),
                        new ViewsAnswer(200, new JsonObject(), PageHeaders(picture.BrowserVersion, picture.Warnings.Count, null), new ViewsFile(picture.Png, "image/png", $"{name}.png")));
                }
                var result = await exporter.ExportPdfAsync(job, cancellationToken).ConfigureAwait(false);
                return Finish(who, ViewsAuditVerbs.Export, PageKind, target, null, AuditEvent.Ok,
                    Detail(traceId, ("format", "pdf"), ("pages", result.Pages), ("bytes", result.Pdf.Length), ("browser", result.BrowserVersion), ("mode", exporter.Availability.Mode), ("warnings", result.Warnings.Count), ("fonts", result.EmbeddedFonts), ("parameters", names)),
                    new ViewsAnswer(200, new JsonObject(), PageHeaders(result.BrowserVersion, result.Warnings.Count, result.Pages), new ViewsFile(result.Pdf, "application/pdf", $"{name}.pdf")));
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
                Record(who, ViewsAuditVerbs.Export, PageKind, target, null, AuditEvent.Failed, Detail(traceId, ("format", chosen), ("code", code)));
                Interlocked.Increment(ref refused);
                return ViewsAnswer.Error(status, code, $"Export of the page '{target}' failed: {e.Message}", e.Code == "busy" ? new Dictionary<string, string> { ["Retry-After"] = "5" } : null);
            }
            catch (ExportException) { throw new OperationCanceledException(cancellationToken); }
        }
    }

    /// <summary>
    /// The same export for an address's parameters (<c>p.name=value</c> of the route): they are read against the page's manifest entry (<see cref="CustomViewParameters.Read"/>), so a value is of the type the entry declares and a wrong one is a 400 <c>invalid</c>
    /// in words. A page the host does not serve for this person is 404 first, whatever the parameters say.
    /// </summary>
    public async Task<ViewsAnswer> ExportPageAsync(ViewsPrincipal who, string instance, string name, string? format, IEnumerable<KeyValuePair<string, string[]>> query, string? clientAddress = null, string? traceId = null, CancellationToken cancellationToken = default)
    {
        var apps = options.CustomApps;
        if (apps?.FindPage(instance, name) is { } found && apps.MayOpen(who, instance))
        {
            var (values, problem) = CustomViewParameters.Read(found.View, query);
            if (values is null)
            {
                Record(who, ViewsAuditVerbs.Export, PageKind, $"{instance}.{name}", null, AuditEvent.Refused, Detail(traceId, ("format", format ?? "pdf"), ("code", "invalid")));
                Interlocked.Increment(ref refused);
                return ViewsAnswer.Error(400, "invalid", problem!);
            }
            return await ExportPageAsync(who, instance, name, format, values, clientAddress, traceId, cancellationToken).ConfigureAwait(false);
        }
        return await ExportPageAsync(who, instance, name, format, (IReadOnlyDictionary<string, JsonNode?>?)null, clientAddress, traceId, cancellationToken).ConfigureAwait(false);   // the refusal, audited, in the one place that words it
    }

    private static Dictionary<string, string> PageHeaders(string browser, int warnings, int? pages)
    {
        var headers = new Dictionary<string, string> { ["X-DbData-Export-Browser"] = browser };
        if (pages is { } p) headers["X-DbData-Export-Pages"] = p.ToString(CultureInfo.InvariantCulture);
        if (warnings > 0) headers["X-DbData-Export-Warnings"] = warnings.ToString(CultureInfo.InvariantCulture);
        return headers;
    }
}
