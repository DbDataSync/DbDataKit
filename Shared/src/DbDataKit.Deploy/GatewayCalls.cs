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
}
