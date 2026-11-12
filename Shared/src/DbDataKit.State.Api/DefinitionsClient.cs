using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api;

/// <summary>A file a publish sends beside the manifest: its path in the definition, its bytes and media type. The client uploads it by hash when the group does not have it.</summary>
public sealed record DefinitionFile(string Path, byte[] Bytes, string? MediaType = null);

/// <summary>What the group answered: its status and its body (a JSON object; a refusal carries <c>error</c>). A call that could not be made says so in <c>error</c> under the status the client chose.</summary>
public sealed record DefinitionsAnswer(int Status, JsonObject Body)
{
    public bool Ok => Status is >= 200 and < 300;

    /// <summary>The group's sentence of a refusal, or null.</summary>
    public string? Error => (string?)Body["error"];
}

/// <summary>What a publish asks (<c>PUT /api/v1/definitions/{kind}/{name}</c>): the manifest, the files, the channel and mode, the version the caller saw (<see cref="ExpectedVersion"/>, null to create), what it depends on, and <see cref="Extra"/> fields a kind reads from the body (the Gateway's <c>adopt</c>).</summary>
public sealed record DefinitionPublishRequest(string Kind, string Name, string Manifest, IReadOnlyList<DefinitionFile>? Files = null, string Channel = DefinitionStore.Production, string? Mode = null, int? ExpectedVersion = null,
    IReadOnlyList<string>? DependsOn = null, int? Format = null, string? Note = null, JsonObject? Extra = null);

/// <summary>
/// The client of the shared publish API (plan 30 PB02, <see cref="DefinitionsApi"/>) over the HTTP form every group speaks: <c>HEAD</c> and <c>PUT artifacts/{sha256}</c>, <c>PUT definitions/{kind}/{name}</c>, <c>PUT bindings/...</c> and the reads.
/// It reaches the group through an <see cref="IDefinitionsLink"/> (requests relative to <c>/api/v1/</c>, the caller's token and pinned certificate are the link's), so Build, the apps command and the Workspace server publish with the same code. It signs a version
/// (<see cref="DefinitionSigning"/>) with <paramref name="key"/> when the group trusts that key for the kind; it does not sign when the group does not trust it, and says so, with what an administrator does about it, when the group requires signatures.
/// </summary>
public sealed class DefinitionsClient(IDefinitionsLink link, ConsoleSigningKey? key = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Whether the group trusts this client's key for the kind and whether it requires signatures (<c>GET trust/{kind}</c>); null for a group that does not answer the question (the publish then goes unsigned and the group decides).</summary>
    public async Task<(bool Required, bool Trusted)?> TrustAsync(string kind, CancellationToken ct = default)
    {
        if (key is null) return null;
        var answer = await GetAsync($"trust/{Uri.EscapeDataString(kind)}", ct).ConfigureAwait(false);
        if (!answer.Ok) return null;
        var trusted = answer.Body["keys"] is JsonArray keys && keys.OfType<JsonObject>().Any(k => (string?)k["key_id"] == key.Fingerprint && (bool?)k["revoked"] != true);
        return ((bool?)answer.Body["required"] ?? false, trusted);
    }

    /// <summary>Uploads what the group does not have (by hash) and publishes the version in one call. A refusal comes back as the group's own answer.</summary>
    public async Task<DefinitionsAnswer> PublishAsync(DefinitionPublishRequest request, CancellationToken ct = default)
    {
        var refs = new List<ArtifactRef>();
        foreach (var file in request.Files ?? [])
        {
            var sha = Convert.ToHexStringLower(SHA256.HashData(file.Bytes));
            refs.Add(new ArtifactRef(file.Path, sha, file.Bytes.LongLength, file.MediaType));
            using var head = await link.SendAsync(HttpMethod.Head, $"artifacts/{sha}", null, ct).ConfigureAwait(false);
            if (head.IsSuccessStatusCode) continue;
            using var content = new ByteArrayContent(file.Bytes);
            using var put = await link.SendAsync(HttpMethod.Put, $"artifacts/{sha}", content, ct).ConfigureAwait(false);
            if (!put.IsSuccessStatusCode) return await ReadAsync(put, ct).ConfigureAwait(false);
        }

        string? signature = null;
        if (key is not null && await TrustAsync(request.Kind, ct).ConfigureAwait(false) is { } trust)
        {
            if (trust.Trusted) signature = DefinitionSigning.Sign(key, request.Kind, request.Name, request.Manifest, refs);
            else if (trust.Required)
                return new DefinitionsAnswer(403, new JsonObject
                {
                    ["error"] = $"This group requires signed versions of {request.Kind} and does not trust this client's key {key.Fingerprint} for it. An administrator of the group trusts it with PUT /api/v1/trust/{request.Kind}/keys "
                        + $"({{\"public_key\": \"{Url.Encode(key.PublicKey)}\", \"label\": \"...\"}}), or sets {request.Kind} to optional with PUT /api/v1/settings/signatures/{request.Kind}.",
                    ["code"] = "signature_key_not_trusted", ["key_id"] = key.Fingerprint,
                });
        }

        var body = new JsonObject { ["manifest"] = request.Manifest, ["channel"] = request.Channel };
        if (request.Mode is not null) body["mode"] = request.Mode;
        if (request.ExpectedVersion is { } expected) body["expected_version"] = expected;
        if (request.Format is { } format) body["format"] = format;
        if (request.Note is not null) body["note"] = request.Note;
        if (signature is not null) body["signature"] = signature;
        if (request.DependsOn is { Count: > 0 } depends) body["depends_on"] = new JsonArray([.. depends.Select(d => (JsonNode?)JsonValue.Create(d))]);
        if (refs.Count > 0) body["artifacts"] = new JsonArray([.. refs.Select(r => (JsonNode)new JsonObject { ["path"] = r.Path, ["sha256"] = r.Sha256, ["size"] = r.Size, ["media_type"] = r.MediaType })]);
        foreach (var (field, value) in request.Extra ?? []) body[field] = value?.DeepClone();
        return await SendAsync(HttpMethod.Put, $"definitions/{Uri.EscapeDataString(request.Kind)}/{Uri.EscapeDataString(request.Name)}", body, ct).ConfigureAwait(false);
    }

    /// <summary>The group's account of one definition (<c>GET definitions/{kind}/{name}</c>): 404 when there is none.</summary>
    public Task<DefinitionsAnswer> DescribeAsync(string kind, string name, CancellationToken ct = default) =>
        GetAsync($"definitions/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(name)}", ct);

    /// <summary>The version the channel serves (null when it serves none or the definition is not there) with its manifest, read as <c>GET definitions/{kind}/{name}</c> and then <c>.../versions/{n}</c>. A refusal to read comes back as the answer in <paramref name="refusal"/>.</summary>
    public async Task<(int Version, string Manifest)?> ServedAsync(string kind, string name, string channel, Action<DefinitionsAnswer>? refusal = null, CancellationToken ct = default)
    {
        var described = await DescribeAsync(kind, name, ct).ConfigureAwait(false);
        if (!described.Ok)
        {
            if (described.Status != 404) refusal?.Invoke(described);
            return null;
        }
        var active = described.Body["channels"] is JsonArray heads ? heads.OfType<JsonObject>().FirstOrDefault(h => (string?)h["channel"] == channel)?["active"]?.GetValue<int?>() : null;
        if (active is not { } version) return null;
        var read = await GetAsync($"definitions/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(name)}/versions/{version}", ct).ConfigureAwait(false);
        if (!read.Ok) { refusal?.Invoke(read); return null; }
        return (version, (string?)read.Body["manifest"] ?? "");
    }

    /// <summary>Binds slots of a definition in a channel (<c>PUT bindings/{channel}/{kind}/{name}</c>): slot to the name of what it is bound to, never a value.</summary>
    public Task<DefinitionsAnswer> BindAsync(string channel, string kind, string name, IReadOnlyDictionary<string, string> slots, int? expectedRevision = null, CancellationToken ct = default)
    {
        var body = new JsonObject { ["slots"] = new JsonObject([.. slots.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))]) };
        if (expectedRevision is { } revision) body["expected_revision"] = revision;
        return SendAsync(HttpMethod.Put, $"bindings/{Uri.EscapeDataString(channel)}/{Uri.EscapeDataString(kind)}/{Uri.EscapeDataString(name)}", body, ct);
    }

    private Task<DefinitionsAnswer> GetAsync(string path, CancellationToken ct) => SendAsync(HttpMethod.Get, path, null, ct);

    private async Task<DefinitionsAnswer> SendAsync(HttpMethod method, string path, JsonObject? body, CancellationToken ct)
    {
        using var content = body is null ? null : JsonContent.Create(body, options: Json);
        using var response = await link.SendAsync(method, path, content, ct).ConfigureAwait(false);
        return await ReadAsync(response, ct).ConfigureAwait(false);
    }

    private static async Task<DefinitionsAnswer> ReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonObject? body = null;
        try { body = text.Length > 0 ? JsonNode.Parse(text) as JsonObject : []; } catch (JsonException) { }
        return new DefinitionsAnswer((int)response.StatusCode, body ?? new JsonObject { ["error"] = text.Length > 0 ? text : response.ReasonPhrase ?? "" });
    }
}
