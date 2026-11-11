using System.Globalization;
using DbDataKit.Delivery;

namespace DbDataKit.Views.Hosting;

/// <summary>
/// Makes the principal a subscription is rendered as (plan 32 DBR18): the subscriber's own identity, roles and attributes, <b>never the runner's</b>. The host answers from the identity home (the Gateway's <c>HomeCallers</c> knows how to make a
/// <see cref="ViewsPrincipal"/> from a subject id and the directory attributes). Null means the person cannot be acted as any more (removed, disabled), which the renderer reports as lost access.
/// </summary>
public interface ISubscriberPrincipals
{
    Task<ViewsPrincipal?> ForAsync(string subscriber, CancellationToken cancellationToken);
}

/// <summary>Where the renderer's links point: a template with <c>{kind}</c> and <c>{name}</c> (for example <c>https://console.example/views/{kind}/{name}</c>); null puts no link in the message.</summary>
public sealed record SubscriptionRendererOptions(string? LinkTemplate = null);

/// <summary>
/// The <see cref="ISubscriptionRenderer"/> over the views module (plan 22a GP17, plan 32 DBR18): a subscription is exported by <see cref="ViewsApi.ExportAsync"/> <b>as the subscriber</b> (the principal comes from <see cref="ISubscriberPrincipals"/>),
/// so access, the audit row, the cache key and the layout selection are the module's, the same as when that person presses Export. The layout is the one the subscription names; with none, the module's own rule for the print medium (plan 32 section 3.6).
/// <list type="bullet">
/// <item>A refusal because the person may not open the document any more (403 <c>denied</c>, 404 <c>not_found</c>), or a layout whose widgets <b>all</b> failed as <c>denied</c>, is <see cref="RenderResult.AccessLost"/>: nothing is sent and the subscription stops.</item>
/// <item>A document with a failed widget is sent <b>with the error block in the widget's place</b> and the message says which widget failed and why (the <c>X-DbData-Export-Widgets-Failed</c> header of the export).</item>
/// <item>An export that cannot be made (no exporter on this host, no browser, a deadline, too many pages) is <see cref="RenderResult.Failed"/> with the module's own words, which name the setting (<c>views.export</c>) where there is one.</item>
/// </list>
/// </summary>
public sealed class SubscriptionRenderer(ViewsApi api, ViewsPublications published, ISubscriberPrincipals principals, SubscriptionRendererOptions? options = null) : ISubscriptionRenderer
{
    public async Task<RenderResult> RenderAsync(SubscriptionRender request, CancellationToken cancellationToken)
    {
        var who = await principals.ForAsync(request.Subscriber, cancellationToken).ConfigureAwait(false);
        if (who is null) return new RenderResult.AccessLost("the identity home does not know this person, or the identity is disabled, so the document cannot be rendered as them.");

        var traceId = $"subscription-{request.SubscriptionId:N}";
        var answer = await api.ExportAsync(who, request.Kind, request.View, request.Format, request.Layout, request.Widget, request.Parameters, null, traceId, cancellationToken).ConfigureAwait(false);
        var code = answer.Body is System.Text.Json.Nodes.JsonObject body ? (string?)body["code"] : null;
        var message = answer.Body is System.Text.Json.Nodes.JsonObject b2 ? (string?)b2["message"] : null;

        if (answer.Status == 200 && answer.File is { } file)
        {
            var failed = Failed(answer.Headers);
            var total = answer.Headers is not null && answer.Headers.TryGetValue("X-DbData-Export-Widgets", out var t) && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
            if (failed.Count > 0 && failed.Count == total && failed.All(f => f.Code == "denied"))
                return new RenderResult.AccessLost($"every widget of the {request.Kind} '{request.View}' is denied to this person ({string.Join(", ", failed.Select(f => f.Id))}).");
            var notes = failed.Select(f => $"The widget '{f.Id}' could not be resolved ({f.Code}); its place shows the error." ).ToList();
            var title = published.For(request.Kind).Find(request.View)?.Document.Title ?? request.View;
            var link = options?.LinkTemplate?.Replace("{kind}", Uri.EscapeDataString(request.Kind), StringComparison.Ordinal).Replace("{name}", Uri.EscapeDataString(request.View), StringComparison.Ordinal);
            var summary = request.Format == "pdf" ? $"{title}, as you are allowed to see it, is attached as a PDF." : $"The rows of {title} are attached as {request.Format.ToUpperInvariant()}.";
            return new RenderResult.Rendered(file.Content, file.ContentType, file.FileName ?? $"{request.View}.{request.Format}", title, summary, link, notes);
        }

        if (answer.Status is 403 or 404 && code is "denied" or "not_found")
            return new RenderResult.AccessLost(message ?? $"the {request.Kind} '{request.View}' is not available to this person any more.");
        if (answer.Status == 422 && code == "widget_failed" && (string?)answer.Body["widget"]?["code"] == "denied")
            return new RenderResult.AccessLost(message ?? "the widget is denied to this person.");
        return new RenderResult.Failed(code == ViewsApi.ExportUnavailable ? "unavailable" : code ?? $"http-{answer.Status}", message ?? $"the export answered {answer.Status}.");
    }

    private static List<(string Id, string Code)> Failed(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null || !headers.TryGetValue("X-DbData-Export-Widgets-Failed", out var list)) return [];
        return [.. list.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).Select(p => (p[0], p.Length > 1 ? p[1] : "error"))];
    }
}
