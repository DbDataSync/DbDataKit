using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using DbDataKit.Lineage;
using DbDataKit.Web.Trust;

namespace DbDataKit.Serving.Notify;

/// <summary>The graphs of the paired serving groups that answered, and the ones that did not (a name and why), so a caller can refuse a step it cannot check instead of passing it.</summary>
public sealed record GatewayLineageResult(IReadOnlyList<(string Server, LineageGraph Graph)> Graphs, IReadOnlyList<(string Server, string Why)> Unreachable);

/// <summary>
/// How a product server reaches the lineage of the serving groups it is paired with (plan 21 CH06): <c>GET /api/v1/lineage</c> on each pairing, as the server itself (the same signed token and pinned certificate as <see cref="DataChangeNotifier"/>, with the
/// Publisher role the pairing has; the Gateway lets a Publisher read its graph, without the names of the readers). The Store's contract step and Build's apply ask it before a column is dropped or retyped, with <see cref="ContractGuard"/>. With no pairing the answer is
/// empty and nothing is unreachable: a server that publishes nowhere has no published consumers to protect. A group that does not answer is named in <see cref="GatewayLineageResult.Unreachable"/> and never skipped silently.
/// </summary>
public sealed class GatewayLineageClient(IGatewayPairings pairings, Func<CancellationToken, Task<ConsoleIdentity>> identity, TimeSpan? callTimeout = null, string product = "server", Func<GatewayPairing, HttpMessageHandler>? handler = null)
{
    public async Task<GatewayLineageResult> FetchAsync(CancellationToken ct = default)
    {
        IReadOnlyList<GatewayPairing> all;
        try { all = pairings.All(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return new GatewayLineageResult([], [("pairings", $"the list of paired gateways could not be read: {ex.Message}")]);
        }

        var graphs = new List<(string, LineageGraph)>();
        var unreachable = new List<(string, string)>();
        foreach (var pairing in all)
        {
            try { graphs.Add((pairing.Name, await FetchOneAsync(pairing, ct).ConfigureAwait(false))); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IncompatibleServerException or LineageGraphException or InvalidOperationException or System.Text.Json.JsonException or FormatException)
            {
                if (ct.IsCancellationRequested) throw;
                unreachable.Add((pairing.Name, ex is InvalidOperationException or LineageGraphException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}"));
            }
        }

        return new GatewayLineageResult(graphs, unreachable);
    }

    private async Task<LineageGraph> FetchOneAsync(GatewayPairing pairing, CancellationToken ct)
    {
        var self = await identity(ct).ConfigureAwait(false);
        using var http = new HttpClient((handler ?? (p => PairingClient.Pinned(p.Fingerprint)))(pairing)) { BaseAddress = new Uri(pairing.Url), Timeout = callTimeout ?? TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/lineage");
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, self.Issuer().Issue(new ConsoleTokenRequest(pairing.InstanceId, self.Id, product + " server", ServerRole.Publisher)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var detail = "";
            try { detail = (string?)(JsonNode.Parse(text) as JsonObject)?["error"] ?? ""; } catch (System.Text.Json.JsonException) { }
            throw new InvalidOperationException($"it answered {(int)response.StatusCode}{(detail.Length > 0 ? " " + detail : "")}");
        }

        return LineageJson.Read(JsonNode.Parse(text));
    }
}
