using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The group's master data on the management API, for the console's screens (plan 33 SC24, the Store without its server): <c>/api/v1/store/models/...</c> with the paths a DbDataStore server answered and the store module answers on its own
/// port (<c>{model}/schema</c>, <c>/instances</c>, <c>/entities/{entity}/row|rows</c>, <c>/changes</c>, <c>/drafts[/{id}[/submit]]</c>, <c>/requests[/{id}[/approve|reject]]</c>), so the shared <c>store-ui</c> controls are pointed at it
/// through the console's relay with no change. The caller is the person the console's token names, with the roles it carries on this server (<see cref="GatewayMcpManagement.PersonOf"/>); what each call may do is <see cref="GroupStoreApi"/>'s,
/// the same rules as the module's port, the apps host and an agent's tools.
/// </summary>
public static class GatewayStoreManagement
{
    public const string Prefix = "/api/v1/store/models";

    /// <summary>The copies the group serves of a model: its own (the default) only (<see cref="GroupStoreApi.DefaultCopy"/>); a model with instances is served per copy by the Store's tables, not by the group.</summary>
    private static JsonObject Instances() => new()
    {
        ["mode"] = "none",
        ["instances"] = new JsonArray(new JsonObject { ["name"] = GroupStoreApi.DefaultCopy, ["connection"] = null, ["schema"] = null }),
    };

    public static async Task<(int Status, JsonNode Body)> HandleAsync(GroupStoreApi api, ValidatedToken token, string method, string pathAndQuery, byte[] body, CancellationToken ct)
    {
        var person = GatewayMcpManagement.PersonOf(token);
        var rest = pathAndQuery.Length > Prefix.Length ? pathAndQuery[Prefix.Length..] : "";
        var query = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rest.IndexOf('?') is var q and >= 0)
        {
            foreach (var pair in rest[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                query[Uri.UnescapeDataString(parts[0].Replace('+', ' '))] = parts.Length > 1 ? Uri.UnescapeDataString(parts[1].Replace('+', ' ')) : "";
            }
            rest = rest[..q];
        }
        var draft = query.TryGetValue("draft", out var d) && d.Length > 0 ? d : null;
        var p = rest.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        static (int, JsonNode) Of(StoreAnswer a) => (a.Status, a.Body);

        StoreAnswer? answer = (method, p) switch
        {
            ("GET", []) => api.List(person),
            ("GET", [var m, "schema"]) => api.Schema(person, m),
            ("GET", [var m, "instances"]) => api.Schema(person, m) is { Status: 200 } ? StoreAnswer.Ok(Instances()) : api.Schema(person, m),
            ("GET", [var m, "entities", var e, "row"]) => await api.RowAsync(person, m, e, query, ct, draft),
            ("GET", [var m, "entities", var e, "rows"]) => await api.RowsAsync(person, m, e, query, ct, draft),
            ("POST", [var m, "changes"]) => await api.ChangesAsync(person, m, body, mayWrite: null, ct, draft: draft),
            ("GET", [var m, "drafts"]) => await api.DraftsAsync(person, m, ct),
            ("GET", [var m, "drafts", var id]) => await api.DraftAsync(person, m, id, ct),
            ("DELETE", [var m, "drafts", var id]) => await api.DiscardDraftAsync(person, m, id, ct),
            ("POST", [var m, "drafts", var id, "submit"]) => await api.SubmitDraftAsync(person, m, id, body, ct),
            ("GET", [var m, "requests"]) => api.Requests(person, m, query.GetValueOrDefault("state")),
            ("GET", [var m, "requests", var id]) => api.Request(person, m, id),
            ("POST", [var m, "requests", var id, "approve"]) => await api.DecideAsync(person, m, id, body, approve: true, ct),
            ("POST", [var m, "requests", var id, "reject"]) => await api.DecideAsync(person, m, id, body, approve: false, ct),
            _ => null,
        };
        return answer is null ? (404, new JsonObject { ["error"] = $"There is no store route {method} {Prefix}{rest}." }) : Of(answer);
    }

    /// <summary>The body of a call as bytes (what <see cref="GroupStoreApi"/> reads a batch from); empty for a read.</summary>
    public static async Task<byte[]> BodyAsync(Stream stream, string method, CancellationToken ct)
    {
        if (method is not ("POST" or "PUT")) return [];
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
