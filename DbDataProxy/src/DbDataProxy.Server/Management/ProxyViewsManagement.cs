using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Views;
using DbDataKit.Views.Hosting;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Serving;

namespace DbDataProxy.Server.Management;

/// <summary>
/// The views module on the management API (plan 32, DBR11b): what a console relays (<c>/relay/&lt;proxy&gt;/api/v1/dashboards...</c>) and what the command line publishes through. The routes and their bodies are the module's own
/// (<c>DbDataKit.Views.Hosting</c>), so the console's <c>moduleClient</c> is written once for the module's listener and for this:
/// <code>
/// GET  /api/v1/views/status                       {enabled, state, detail, address, dashboards, reports}  (viewer; says "enabled": false when the module is off)
/// GET  /api/v1/dashboards | /reports              the list the caller may open
/// GET  /api/v1/dashboards/{name} | /reports/{name} the document as published
/// POST /api/v1/dashboards/{name}/render           the rendered widgets, as the person the console vouches for
/// PUT  /api/v1/dashboards/{name} | /reports/{name} {text, expected_version?, note?} publishes it to the group (author, publisher or administrator)
/// </code>
/// Reading is <b>as the person</b>: a console's token names them, and a person who signs in through the identity home is that home user, so their groups decide which data sources every widget may read. Publishing needs no module: the document is checked and
/// stored for every instance that runs the module.
/// </summary>
public sealed class ProxyViewsManagement(ProxyViewsPublishing publishing, ProxyViews? views)
{
    public const string Prefix = ProxyManagement.Prefix;

    public static bool Handles(string route) =>
        route is Prefix + "/dashboards" or Prefix + "/reports" or Prefix + "/views/status"
        || route.StartsWith(Prefix + "/dashboards/", StringComparison.Ordinal) || route.StartsWith(Prefix + "/reports/", StringComparison.Ordinal);

    private static ManagementResult Problem(int status, string code, string message) =>
        new(status, new JsonObject { ["error"] = message, ["code"] = code, ["message"] = message });

    public async Task<ManagementResult> HandleAsync(string method, string route, string? query, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (route == Prefix + "/views/status")
            return method == "GET" ? (caller.Holds(ServerRole.Viewer) ? Status() : Problem(403, "denied", "This needs the viewer role.")) : Problem(405, "invalid", "The status is read with GET.");

        var parts = route[(Prefix.Length + 1)..].Split('/');
        var kind = parts[0] == "reports" ? ViewDefinitions.Report : ViewDefinitions.Dashboard;
        var name = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : null;
        var action = parts.Length > 2 ? parts[2] : null;
        if (parts.Length > 3 || (action is not null && action is not ("render" or "export"))) return Problem(404, "not_found", "There is no such call.");

        if (name is not null && action is null && method == "PUT") return Publish(kind, name, body, caller);

        if (!caller.Holds(ServerRole.Viewer)) return Problem(403, "denied", "This needs the viewer role.");
        if (views is null) return Problem(404, "not_found", "This proxy does not serve the views module: list `views` under `modules:` in server.yaml.");
        if (caller.Token is not { } token) return Problem(403, "denied", "Dashboards and reports are read as a person: sign in through a console, or call the module's own address with a token of the identity home.");
        var who = await views.ConsolePrincipalAsync(token, cancellationToken);
        var api = views.Module.Api;
        if (api is null) return Problem(503, "unavailable", "The views module has not started.");
        var trace = System.Diagnostics.Activity.Current?.TraceId.ToString();
        ViewsAnswer answer;
        switch (method, name, action)
        {
            case ("GET", null, null): answer = api.List(who, kind, trace); break;
            case ("GET", not null, null): answer = api.Open(who, kind, name, trace); break;
            case ("GET", not null, "export"):
                var q = System.Web.HttpUtility.ParseQueryString(query ?? "");
                answer = api.Export(who, kind, name, q["format"] is { Length: > 0 } f ? f : null, q["layout"] is { Length: > 0 } l ? l : null, trace);
                break;
            case ("POST", not null, "render"):
                JsonObject? json;
                try { json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject ?? throw new JsonException("The body must be a JSON object."); }
                catch (JsonException ex) { return Problem(400, "invalid", $"The body is not JSON: {ex.Message}"); }
                answer = await api.RenderAsync(who, kind, name, json, null, trace, cancellationToken);
                break;
            default: return Problem(405, "invalid", "That method is not allowed here.");
        }
        return new ManagementResult(answer.Status, answer.Body);
    }

    private ManagementResult Publish(string kind, string name, string? body, ManagementCaller caller)
    {
        // a document is an author's; the publisher role of a server client (a Build server) and an administrator publish too
        if (!caller.Holds(ServerRole.Author) && !caller.Holds(ServerRole.Publisher) && !caller.Holds(ServerRole.Admin)) return Problem(403, "denied", "Publishing a document needs the author, publisher or administrator role.");
        string? text; int? expected = null; string? note = null;
        try
        {
            var json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject;
            text = json?["text"]?.GetValue<string>();
            note = json?["note"]?.GetValue<string>();
            if (json?["expected_version"] is { } e)
                expected = e.GetValueKind() == JsonValueKind.String ? int.Parse(e.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture) : e.GetValue<int>();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return Problem(400, "invalid", "The body is {\"text\": \"<the document's yaml or json>\", \"expected_version\": <the version read, or absent for a new document>, \"note\": \"<why>\"}.");
        }
        var by = caller.Audit("", null, "", DateTimeOffset.UtcNow) is { } entry ? entry.UserName ?? entry.UserId : "management";
        var outcome = publishing.Publish(kind, name, text ?? "", by, expected, note);
        return new ManagementResult(outcome.Status, outcome.Body);
    }

    private ManagementResult Status()
    {
        var found = new JsonObject { ["enabled"] = views is not null };
        found["dashboards"] = publishing.List(ViewDefinitions.Dashboard).Count(h => h.Active is > 0);
        found["reports"] = publishing.List(ViewDefinitions.Report).Count(h => h.Active is > 0);
        if (views is not null)
        {
            var health = views.Module.Health();
            found["state"] = health.State.ToString().ToLowerInvariant();
            found["detail"] = health.Detail;
            found["address"] = views.Module.Api is null ? null : views.Address;
        }
        return ManagementResult.Ok(found);
    }
}
