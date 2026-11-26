using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataKit.State;

namespace DbDataKit.Deploy;

/// <summary>A Gateway whose lineage is read: the name it is reported under, its address (for a message) and how to open the link to it. Opening may fail with a <see cref="DeployException"/> (no key, no pairing), which is reported, not thrown.</summary>
public sealed record GatewayLineageSource(string Server, string Address, Func<CancellationToken, Task<IDefinitionsLink>> Open);

/// <summary>
/// The reads from a Gateway around a deploy: the tables and columns of a data source, and the lineage graph. Each goes through the link the caller gives, which says who is calling. The Gateway reads with its own logins; no row and no login
/// comes back.
/// </summary>
public static class GatewayCalls
{
    /// <summary>Refuses, in words, a target that is not a Gateway: the catalog of a data source is read from a Gateway.</summary>
    public static void RequireForSchema(DeployTarget target)
    {
        if (target.Product != DeployProducts.Gateway)
            throw new DeployException($"'{target.Name}' is a {target.Product} ({target.Url}); the catalog of a data source is read from a Gateway.", 409);
    }

    /// <summary>
    /// The shape of a data source's catalog: <c>POST /api/v1/data-sources/{name}/schema</c>, which the Gateway answers with the tables, columns and keys it reads with its own login. A data source the caller did not make is asked for with
    /// <paramref name="adopt"/> (the person's confirmation, which the Gateway records); without it the Gateway's refusal comes back with the data source and who made it (<see cref="DeployException.Adopt"/>).
    /// </summary>
    public static async Task<JsonObject> ReadSchemaAsync(IDefinitionsLink link, DeployTarget target, string dataSource, bool adopt, Action<string> step, CancellationToken ct = default)
    {
        RequireForSchema(target);
        step($"Asked {target.Url} for the tables and columns of the data source '{dataSource}' (no row is read).");
        HttpResponseMessage response;
        try { response = await link.SendAsync(HttpMethod.Post, $"data-sources/{Uri.EscapeDataString(dataSource)}/schema", JsonContent.Create(new JsonObject { ["adopt"] = adopt ? dataSource : null }), ct); }
        catch (HttpRequestException ex) { throw new DeployException($"{target.Url} could not be reached ({ex.Message}).", 502); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonObject? answer = null;
            try { answer = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject; }
            catch (System.Text.Json.JsonException) { /* not JSON: the text is the answer */ }
            if (!response.IsSuccessStatusCode)
                throw new DeployException($"The Gateway refused to give the catalog of '{dataSource}' ({(int)response.StatusCode}): {(string?)answer?["error"] ?? (text.Length > 0 ? text : response.ReasonPhrase)}",
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden ? 403 : 409, answer?["adopt"] is JsonObject hint ? (JsonObject)hint.DeepClone() : null);
            return answer ?? [];
        }
    }

    /// <summary>
    /// The lineage graph of each Gateway of <paramref name="sources"/> (<c>GET /api/v1/lineage</c>), for a Store model's contract step. A Gateway that does not answer, answers what is not a graph, or whose link cannot be opened is named in
    /// <c>Unreachable</c> with why, never skipped: a caller refuses a step it cannot check. A link that is <see cref="IDisposable"/> is disposed after its read.
    /// </summary>
    public static async Task<(IReadOnlyList<(string Server, LineageGraph Graph)> Graphs, IReadOnlyList<(string Server, string Why)> Unreachable)> LineageAsync(IEnumerable<GatewayLineageSource> sources, CancellationToken ct = default)
    {
        var graphs = new List<(string, LineageGraph)>();
        var unreachable = new List<(string, string)>();
        foreach (var source in sources)
        {
            IDefinitionsLink? link = null;
            try
            {
                link = await source.Open(ct);
                using var response = await link.SendAsync(HttpMethod.Get, "lineage", null, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode)
                {
                    string? detail = null;
                    try { detail = (string?)(JsonNode.Parse(text) as JsonObject)?["error"]; } catch (System.Text.Json.JsonException) { }
                    unreachable.Add((source.Server, $"it answered {(int)response.StatusCode}{(detail is { Length: > 0 } ? " " + detail : "")}"));
                    continue;
                }
                graphs.Add((source.Server, LineageJson.Read(JsonNode.Parse(text))));
            }
            catch (Exception ex) when (ex is DeployException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or LineageGraphException or InvalidOperationException or FormatException)
            {
                if (ct.IsCancellationRequested) throw;
                unreachable.Add((source.Server, ex is HttpRequestException or TaskCanceledException ? $"{source.Address} could not be reached ({ex.Message})" : ex.Message));
            }
            finally { (link as IDisposable)?.Dispose(); }
        }
        return (graphs, unreachable);
    }

    /// <summary>The start of the id a Workspace server's publisher key pairs with a Gateway under (a server client): what such a key published is what <see cref="HandOverAsync"/> hands to the control service.</summary>
    public const string WorkspaceClientPrefix = "workspace-";

    /// <summary>
    /// Makes <paramref name="to"/> (the control service's client id on the Gateway) the owner of what each client of <paramref name="clients"/> owns there: one <c>PUT /api/v1/settings/handovers/{client}</c> per client, which the
    /// Gateway takes from an administrator and records in its audit, one entry for each definition whose owner changes. With no client named, the clients are the Workspace servers' publisher keys the Gateway lists
    /// (<c>GET /api/v1/clients</c>, ids that start with <see cref="WorkspaceClientPrefix"/>). The answer has each hand-over and the definitions it moved, in the Gateway's words; a refusal is a <see cref="DeployException"/> in them.
    /// </summary>
    public static async Task<JsonObject> HandOverAsync(IDefinitionsLink link, DeployTarget target, string to, IReadOnlyList<string>? clients, Action<string> step, CancellationToken ct = default)
    {
        if (target.Product != DeployProducts.Gateway)
            throw new DeployException($"'{target.Name}' is a {target.Product} ({target.Url}); what a publisher key published is handed over on a Gateway.", 409);
        var from = clients is { Count: > 0 } ? clients.Select(c => c.Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList() : await WorkspaceClientsAsync(link, target, ct);
        if (from.Count == 0) step($"{target.Name} has no client of a Workspace server's publisher key, so there is nothing to hand over.");
        var handed = new JsonArray();
        foreach (var client in from.Where(c => c != to))
        {
            var answer = await SendAsync(link, target, HttpMethod.Put, $"settings/handovers/{Uri.EscapeDataString(client)}", new JsonObject { ["to"] = to }, $"to hand what '{client}' owns to '{to}'", ct);
            var moved = answer["definitions"] as JsonArray ?? [];
            step(answer["unchanged"]?.GetValue<bool>() == true
                ? $"What '{client}' owns on {target.Name} was handed to '{to}' already ({moved.Count} definition(s))."
                : $"{target.Name} handed what '{client}' owns to '{to}': {moved.Count} definition(s).");
            handed.Add(new JsonObject { ["from"] = client, ["to"] = to, ["unchanged"] = answer["unchanged"]?.DeepClone(), ["definitions"] = moved.DeepClone() });
        }
        return new JsonObject { ["gateway"] = target.Name, ["to"] = to, ["handovers"] = handed };
    }

    private static async Task<List<string>> WorkspaceClientsAsync(IDefinitionsLink link, DeployTarget target, CancellationToken ct)
    {
        var answer = await SendAsync(link, target, HttpMethod.Get, "clients", null, "to list its paired server clients", ct);
        return [.. (answer["clients"] as JsonArray ?? []).Select(c => (string?)c?["id"]).Where(id => id?.StartsWith(WorkspaceClientPrefix, StringComparison.Ordinal) == true).Select(id => id!).Order(StringComparer.Ordinal)];
    }

    private static async Task<JsonObject> SendAsync(IDefinitionsLink link, DeployTarget target, HttpMethod method, string path, JsonObject? body, string doing, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await link.SendAsync(method, path, body is null ? null : JsonContent.Create(body), ct); }
        catch (HttpRequestException ex) { throw new DeployException($"{target.Url} could not be reached ({ex.Message}).", 502); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonObject? answer = null;
            try { answer = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject; }
            catch (System.Text.Json.JsonException) { /* not JSON: the text is the answer */ }
            if (!response.IsSuccessStatusCode)
                throw new DeployException($"{target.Name} refused {doing} ({(int)response.StatusCode}): {(string?)answer?["error"] ?? (text.Length > 0 ? text : response.ReasonPhrase)}", response.StatusCode == System.Net.HttpStatusCode.Forbidden ? 403 : 409);
            return answer ?? [];
        }
    }
}
