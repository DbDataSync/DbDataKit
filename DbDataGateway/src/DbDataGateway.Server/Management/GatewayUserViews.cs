using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Views;
using DbDataKit.Views.Hosting;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// A person's own dashboards and reports on the management API (plan 32 DBR12): <c>/api/v1/my/views</c> and <c>/api/v1/my/dashboards|reports/{name}[/render|history|restore|shares|promote]</c>, the module's own calls
/// (<see cref="ViewsApi"/>) in process, **as the person the console's token names**: that person owns what they save, opens what was shared with them, and every widget runs with their own access. The viewer role is enough (it is
/// the person's own space, not the group's published documents); a call with no person behind it (a server client) is refused.
/// </summary>
internal static class GatewayUserViews
{
    private static ManagementResult Problem(int status, string code, string message) =>
        new(status, new JsonObject { ["error"] = message, ["code"] = code, ["message"] = message });

    public static async Task<ManagementResult> HandleAsync(GatewayViews? views, string method, string route, string? query, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (!caller.Holds(ServerRole.Viewer)) return Problem(403, "denied", "This needs the viewer role.");
        if (views is null) return Problem(404, "not_found", "This gateway does not serve the views module: list `views` under `modules:` in server.yaml.");
        if (caller.Token is not { } token) return Problem(403, "denied", "Personal dashboards are a person's: sign in through a console, or call the module's own address with a token of the identity home.");
        var limit = new ViewsOptions().MaxPreviewBytes;
        if (body is not null && System.Text.Encoding.UTF8.GetByteCount(body) > limit) return Problem(413, "too_large", $"A request is at most {limit} bytes.");
        JsonObject? json;
        try { json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body) as JsonObject ?? throw new JsonException("The body must be a JSON object."); }
        catch (JsonException ex) { return Problem(400, "invalid", $"The body is not JSON: {ex.Message}"); }
        var api = views.Module.Api;
        if (api is null) return Problem(503, "unavailable", "The views module has not started.");
        var who = await views.ConsolePrincipalAsync(token, cancellationToken);
        var trace = System.Diagnostics.Activity.Current?.TraceId.ToString();
        var q = query is null ? new System.Collections.Specialized.NameValueCollection() : System.Web.HttpUtility.ParseQueryString(query);

        var parts = route[(GatewayViewsManagement.Prefix.Length + "/my/".Length)..].Split('/');
        if (parts is ["views"]) return method == "GET" ? Of(api.MyList(who, q["scope"], q["kind"], trace)) : Problem(405, "invalid", "The list is read with GET.");
        if (parts.Length < 2 || parts[0] is not ("dashboards" or "reports") || parts.Length > 3) return Problem(404, "not_found", "There is no such call.");
        var kind = parts[0] == "reports" ? ViewDefinitions.Report : ViewDefinitions.Dashboard;
        var name = Uri.UnescapeDataString(parts[1]);
        var action = parts.Length > 2 ? parts[2] : null;
        return (method, action) switch
        {
            ("GET", null) => Of(api.MyOpen(who, kind, name, q["owner"], trace)),
            ("PUT", null) => Of(api.MySave(who, kind, name, json, trace)),
            ("DELETE", null) => Of(api.MyDelete(who, kind, name, trace)),
            ("POST", "render") => Of(await api.MyRenderAsync(who, kind, name, q["owner"], json, null, trace, cancellationToken)),
            ("GET", "history") => Of(api.MyHistory(who, kind, name, q["version"], trace)),
            ("POST", "restore") => Of(api.MyRestore(who, kind, name, json, trace)),
            ("GET", "shares") => Of(api.MyShares(who, kind, name, null, write: false, trace)),
            ("PUT", "shares") => Of(api.MyShares(who, kind, name, json, write: true, trace)),
            ("GET", "promote") => Of(api.MyPromote(who, kind, name, trace)),
            _ => Problem(405, "invalid", "That method is not allowed here."),
        };
    }

    private static ManagementResult Of(ViewsAnswer answer) => new(answer.Status, answer.Body);
}
