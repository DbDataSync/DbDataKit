using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.State;

/// <summary>One group's shared publish API as a caller reaches it (plan 30 PB06): a request relative to <c>/api/v1/</c> (no leading slash), sent as the caller the link stands for. A paired runner or Gateway, or a test's in-process group, is one.</summary>
public interface IDefinitionsLink
{
    /// <summary>What the group is called in a message (its name or address).</summary>
    string Name { get; }

    /// <summary>Sends <paramref name="method"/> to <paramref name="path"/> (for example <c>definitions/dashboard/sales</c>); the answer is the group's, whatever its status.</summary>
    Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct);
}

/// <summary>A promotion that the source or the target group refused or could not carry out; the message is in the group's words and names the group.</summary>
public sealed class DefinitionPromotionException(string message, int status = 0, string? code = null) : InvalidOperationException(message)
{
    /// <summary>The HTTP status of the refusal (0 when the call did not get an answer).</summary>
    public int Status { get; } = status;

    /// <summary>The group's code for the refusal (<c>refused</c> for an unbound slot or a dependency that is not active, <c>signature_*</c>, <c>version_conflict</c>), when it gave one.</summary>
    public string? Code { get; } = code;
}

/// <summary>What to promote (plan 30 PB06): the definition, the channel it is read from in the source group and the channel it goes to in the target group.</summary>
/// <param name="Version">The version to send; null is the one <paramref name="FromChannel"/> serves in the source group.</param>
/// <param name="Mode"><c>serve</c> or <c>stage</c>; null is the kind's default in the target group.</param>
/// <param name="Bindings">The target group's own bindings for the channel (slot to the name of what it is bound to in that group, never a value), written before the version is published; null leaves what the channel has.</param>
/// <param name="ExpectedVersion">The target's latest version the caller saw (the optimistic check of the publish); null does not check.</param>
public sealed record PromoteRequest(string Kind, string Name, string FromChannel, string ToChannel, int? Version = null, string? Mode = null, IReadOnlyDictionary<string, string>? Bindings = null, int? ExpectedVersion = null, string? Note = null);

/// <summary>What a promotion did: the version it read, the version the target now holds (numbers are each group's own), whether the target changed, and how many files were sent and how many it already had.</summary>
public sealed record Promotion(string Kind, string Name, string FromChannel, string ToChannel, int SourceVersion, int TargetVersion, bool Changed, string Mode, string Signature, int Sent, int Skipped, IReadOnlyList<string> Bound, JsonObject Answer);

/// <summary>
/// Promotion between groups (plan 30 PB06): the same stored version is read from the source group and published to the target group over the shared publish API, so a test group's version reaches a production group without a second build.
/// The manifest, the format, the dependencies and the <b>signature</b> travel with the version (the target verifies the signature against its own trust store, so a key it does not trust is refused there), the files go by hash (<c>HEAD</c> first, and only the
/// files the target lacks are read from the source and sent: a second call sends nothing), the version is published in the channel asked, and the target's checks decide: a slot the channel does not bind and a dependency that is not active there refuse the move naming it.
/// </summary>
public sealed class DefinitionPromoter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Promotes <paramref name="request"/> from <paramref name="source"/> to <paramref name="target"/>.</summary>
    public async Task<Promotion> PromoteAsync(IDefinitionsLink source, IDefinitionsLink target, PromoteRequest request, Action<string>? log = null, CancellationToken ct = default)
    {
        var kind = Uri.EscapeDataString(request.Kind);
        var name = Uri.EscapeDataString(request.Name);
        var version = request.Version ?? await ServedAsync(source, request, kind, name, ct);
        var read = await ReadAsync(source, HttpMethod.Get, $"definitions/{kind}/{name}/versions/{version}", $"to read version {version} of {request.Kind} '{request.Name}'", ct)
                   ?? throw new DefinitionPromotionException($"{source.Name} has no version {version} of {request.Kind} '{request.Name}'.", 404, "not_found");
        var manifest = (string?)read["manifest"] ?? throw new DefinitionPromotionException($"{source.Name} answered version {version} without its manifest.");
        var files = (read["artifacts"] as JsonArray ?? []).Select(a => (Path: (string)a!["path"]!, Sha: (string)a["sha256"]!, Size: (long)a["size"]!, Media: (string?)a["media_type"])).ToList();

        var bound = new List<string>();
        if (request.Bindings is { Count: > 0 } slots)
        {
            var body = new JsonObject { ["slots"] = new JsonObject([.. slots.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))]) };
            using var bind = await target.SendAsync(HttpMethod.Put, $"bindings/{Uri.EscapeDataString(request.ToChannel)}/{kind}/{name}", JsonContent.Create(body, options: Json), ct);
            if (!bind.IsSuccessStatusCode) throw await RefusedAsync(target, bind, $"the bindings of {request.Name} in {request.ToChannel}", ct);
            bound.AddRange(slots.Keys.Order(StringComparer.Ordinal));
        }

        int sent = 0, skipped = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!seen.Add(file.Sha)) { skipped++; continue; }
            using (var head = await target.SendAsync(HttpMethod.Head, $"artifacts/{file.Sha}", null, ct))
            {
                if (head.StatusCode == HttpStatusCode.OK) { skipped++; continue; }
                if (head.StatusCode != HttpStatusCode.NotFound) throw await RefusedAsync(target, head, $"to say whether it has {file.Path}", ct);
            }

            using var download = await source.SendAsync(HttpMethod.Get, $"artifacts/{file.Sha}", null, ct);
            if (!download.IsSuccessStatusCode) throw await RefusedAsync(source, download, $"the file {file.Path}", ct);
            await using var stream = await download.Content.ReadAsStreamAsync(ct);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue(file.Media ?? "application/octet-stream");
            content.Headers.ContentLength = file.Size;
            using var put = await target.SendAsync(HttpMethod.Put, $"artifacts/{file.Sha}", content, ct);
            if (!put.IsSuccessStatusCode) throw await RefusedAsync(target, put, $"the file {file.Path}", ct);
            sent++;
            log?.Invoke($"Sent {file.Path} ({file.Size} bytes) to {target.Name}.");
        }

        var publish = new JsonObject
        {
            ["manifest"] = manifest,
            ["channel"] = request.ToChannel,
            ["format"] = (int?)read["format"],
            ["note"] = request.Note ?? $"promoted from {source.Name} {request.FromChannel} (version {version})",
            ["artifacts"] = new JsonArray([.. files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["sha256"] = f.Sha, ["size"] = f.Size, ["media_type"] = f.Media })]),
            ["depends_on"] = new JsonArray([.. (read["depends_on"] as JsonArray ?? []).Select(d => (JsonNode)JsonValue.Create((string)d!)!)]),
        };
        if ((string?)read["signature"] is { Length: > 0 } signature) publish["signature"] = signature;
        if (request.Mode is { } mode) publish["mode"] = mode;
        if (request.ExpectedVersion is { } expected) publish["expected_version"] = expected;
        using var response = await target.SendAsync(HttpMethod.Put, $"definitions/{kind}/{name}", JsonContent.Create(publish, options: Json), ct);
        if (!response.IsSuccessStatusCode) throw await RefusedAsync(target, response, $"{request.Kind} '{request.Name}' version {version} for {request.ToChannel}", ct);
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
        return new Promotion(request.Kind, request.Name, request.FromChannel, request.ToChannel, version, (int)answer["version"]!, (bool)answer["changed"]!, (string?)answer["mode"] ?? "serve", (string?)answer["signature"] ?? "none", sent, skipped, bound, answer);
    }

    private static async Task<int> ServedAsync(IDefinitionsLink source, PromoteRequest request, string kind, string name, CancellationToken ct)
    {
        var described = await ReadAsync(source, HttpMethod.Get, $"definitions/{kind}/{name}", $"to describe {request.Kind} '{request.Name}'", ct)
                        ?? throw new DefinitionPromotionException($"{source.Name} has no {request.Kind} '{request.Name}': publish it to '{request.FromChannel}' first.", 404, "not_found");
        var head = (described["channels"] as JsonArray ?? []).FirstOrDefault(c => (string?)c!["channel"] == request.FromChannel);
        var active = head?["active"]?.GetValue<int>() ?? 0;
        if (active <= 0)
            throw new DefinitionPromotionException($"{request.Kind} '{request.Name}' has no active version in channel '{request.FromChannel}' of {source.Name} to promote{(head?["staged"] is not null ? $" (version {head["staged"]} is staged there, not active yet)" : "")}.", 409, "refused");
        return active;
    }

    private static async Task<JsonObject?> ReadAsync(IDefinitionsLink link, HttpMethod method, string path, string doing, CancellationToken ct)
    {
        using var response = await link.SendAsync(method, path, null, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw await RefusedAsync(link, response, doing, ct);
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!.AsObject();
    }

    private static async Task<DefinitionPromotionException> RefusedAsync(IDefinitionsLink link, HttpResponseMessage response, string doing, CancellationToken ct)
    {
        var body = response.Content is null ? "" : await response.Content.ReadAsStringAsync(ct);
        string? error = null, code = null;
        try { var json = JsonNode.Parse(body); error = (string?)json?["error"]; code = (string?)json?["code"]; }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException) { /* not our JSON */ }
        return new DefinitionPromotionException($"{link.Name} refused {doing} ({(int)response.StatusCode}): {error ?? (body.Length > 0 ? body : response.ReasonPhrase)}", (int)response.StatusCode, code);
    }
}
