using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;

namespace DbDataKit.Authoring.Server;

public sealed partial class WorkspacePublishing
{
    /// <summary>
    /// The shape of a data source's catalog from a paired Gateway (plan 33 SC20j, D20): <c>POST /api/v1/data-sources/{name}/schema</c> as the paired publisher client, which the Gateway answers with the tables, columns and keys it reads with its own login. The Workspace
    /// holds no login and reads no row. A data source the client did not make is asked for with <paramref name="adopt"/> (the person's confirmation, which the Gateway records); without it the Gateway's refusal comes back with the data source and who made it, as
    /// for a publish (SC21f). The target must be a Gateway.
    /// </summary>
    public async Task<(JsonObject Schema, PublishTarget Target)> ReadSchemaAsync(string targetName, string dataSource, bool adopt, Action<string> step, CancellationToken ct = default)
    {
        var (target, key, role) = ResolveTarget(targetName, "the catalog of a data source");
        if (target.Product != PublishProducts.Gateway)
            throw new WorkspacePublishException($"'{target.Name}' is a {target.Product} ({target.Url}); the catalog of a data source is read from a Gateway.", 409);
        step("Read the publisher key '" + targetName + "' from the secret store.");
        var identity = await KeyAsync(targetName, key.SecretRef, ct);
        using var http = new HttpClient(PairingClient.Pinned(target.Fingerprint)) { BaseAddress = new Uri(target.Url + "/"), Timeout = TimeSpan.FromMinutes(2) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"api/v1/data-sources/{Uri.EscapeDataString(dataSource)}/schema");
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, identity.Issuer().Issue(new ConsoleTokenRequest(target.InstanceId, "client:" + identity.Id, target.ClientName, role)));
        request.Content = JsonContent.Create(new JsonObject { ["adopt"] = adopt ? dataSource : null });
        step($"Asked {target.Url} for the tables and columns of the data source '{dataSource}' (no row is read).");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new WorkspacePublishException($"{target.Url} could not be reached ({ex.Message}).", 502); }
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonObject? answer = null;
            try { answer = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject; }
            catch (System.Text.Json.JsonException) { /* not JSON: the text is the answer */ }
            if (!response.IsSuccessStatusCode)
                throw new WorkspacePublishException($"The Gateway refused to give the catalog of '{dataSource}' ({(int)response.StatusCode}): {(string?)answer?["error"] ?? (text.Length > 0 ? text : response.ReasonPhrase)}",
                    response.StatusCode == System.Net.HttpStatusCode.Forbidden ? 403 : 409, answer?["adopt"] is JsonObject hint ? (JsonObject)hint.DeepClone() : null);
            return (answer ?? [], target);
        }
    }
}
