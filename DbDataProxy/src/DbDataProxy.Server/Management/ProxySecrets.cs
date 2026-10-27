using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;

namespace DbDataProxy.Server.Management;

/// <summary>
/// Secrets through the management API, for a server client that publishes data sources (plan 13, T10): a Build server that creates a data source sends the password of its read login once, over the pinned, signed channel, and the proxy keeps
/// it in its own encrypted store. <c>PUT /api/v1/secrets/{name}</c> (<c>{"value"}</c>) answers the reference to write in the data source's file; <c>DELETE</c> removes it; <c>GET /api/v1/secrets</c> lists names.
/// <para>
/// <b>A value is accepted and never returned</b>, not in this answer nor any other. A client's secrets live under a name of its own (<c>dbdata:proxy:managed-&lt;client id&gt;:&lt;name&gt;</c>), so a Publisher cannot overwrite, delete or even list another client's, and
/// cannot reach the secrets an administrator keeps. An administrator uses the same calls, under their own client id (or <c>local</c> on the local channel).
/// </para>
/// </summary>
public sealed partial class ProxySecrets(ISecretStore store, IAuditLog audit, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed record WriteRequest(string? Value);

    /// <summary>The kind segment of this caller's secrets: its client id, made safe for a secret name.</summary>
    private static string KindOf(ManagementCaller caller) => "managed-" + Unsafe().Replace(caller.Token is { } t ? OwnerScope.Stamp(t) : "local", "-");

    public async Task<ManagementResult> HandleAsync(string method, string[] rest, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        var kind = KindOf(caller);
        if (rest.Length == 1 && method == "GET")
        {
            var names = new JsonArray();
            await foreach (var name in store.ListAsync($"dbdata:proxy:{kind}:", cancellationToken)) names.Add(name.Name);
            return ManagementResult.Ok(new JsonObject { ["secrets"] = names });
        }
        if (rest.Length != 2) return ManagementResult.Problem(404, "There is no such call.");
        var given = Uri.UnescapeDataString(rest[1]);
        if (!NamePattern().IsMatch(given)) return ManagementResult.Problem(400, "A secret name is a letter followed by letters, digits, '.', '_' or '-' (up to 64).");
        var secret = new SecretName("proxy", kind, given);

        switch (method)
        {
            case "PUT":
                WriteRequest? request;
                try { request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<WriteRequest>(body, Json); }
                catch (JsonException) { request = null; }
                if (string.IsNullOrEmpty(request?.Value)) return ManagementResult.Problem(400, "The body is a JSON object with the secret's value: {\"value\": \"...\"}.");
                using (var value = SecretValue.From(request.Value)) await store.SetAsync(secret, value, cancellationToken);
                Record(caller, "secret write", given, "stored");
                return ManagementResult.Ok(new JsonObject { ["name"] = given, ["reference"] = secret.ToRef().ToString() });
            case "DELETE":
                await store.DeleteAsync(secret, cancellationToken);
                Record(caller, "secret delete", given, "removed");
                return ManagementResult.Ok(new JsonObject { ["name"] = given, ["removed"] = true });
            default:
                return ManagementResult.Problem(405, "A secret is stored with PUT and removed with DELETE; its value is never read back.");
        }
    }

    private void Record(ManagementCaller caller, string command, string target, string outcome) => audit.Append(caller.Audit(command, target, outcome, _clock.GetUtcNow()));

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex("[^A-Za-z0-9._-]")]
    private static partial Regex Unsafe();
}
