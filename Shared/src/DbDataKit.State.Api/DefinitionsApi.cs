using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api;

/// <summary>Who calls the shared publish API: the id the audit and the version's publisher record, the roles, and for a paired client its id (the owner rules read it; null for a person signed in to a console).</summary>
public sealed record DefinitionsCaller(string Actor, bool Admin, bool Publisher, bool Viewer, string? ClientId = null)
{
    /// <summary>What the host knows of the caller beyond this (the runner's actor, for its audit); the API does not read it.</summary>
    public object? Context { get; init; }
}

/// <summary>An answer: the HTTP status and the JSON body (an error is <c>{error, code, message}</c>).</summary>
public sealed record DefinitionsResult(int Status, JsonObject Body);

/// <summary>What the API tells its host about each change, for the audit: the verb (<c>definition.publish</c>), the caller, kind, name, channel, the outcome (<c>ok</c> or <c>refused</c>) and a detail that never holds file content.</summary>
public delegate void DefinitionsAuditor(string verb, DefinitionsCaller caller, string kind, string name, string channel, string outcome, object? detail);

/// <summary>
/// The shared publish API (plan 30 PB02), generalised from the runner's: one set of calls for every kind a host's registry lists, relative to the host's <c>/api/v1</c>.
/// <code>
/// HEAD artifacts/{sha256}  |  PUT artifacts/{sha256}                           (the stream calls <see cref="HasArtifact"/> and <see cref="StoreArtifactAsync"/>)
/// GET  definitions/{kind}                                                       what each channel serves of each definition of the kind        (viewer)
/// GET  definitions/{kind}/{name}                                                versions, what each channel serves, bindings                    (viewer)
/// PUT  definitions/{kind}/{name}      {manifest, artifacts[], depends_on[], channel, mode, expected_version, note, format}   (publisher: own definitions; admin)
/// GET  definitions/{kind}/{name}/versions/{n}                                   one version whole: manifest, files, depends_on, signature       (viewer; what a promotion to another group reads, PB06)
/// POST definitions/{kind}/{name}/activate|switch|rollback|retire|prune          {channel, expected_version, version | keep | versions, dry_run}  (admin)
/// PUT  bindings/{channel}/{kind}/{name}  {slots: {slot: boundTo}, expected_revision}  |  DELETE bindings/{channel}/{kind}/{name}/{slot}   (publisher: own; admin)
/// </code>
/// The caller's roles and the owner rules (a publisher changes only the definitions it created) are the API's; what a kind checks is the registry's. A host that serves a kind from what the group serves (the Gateway) is told of each change
/// through <paramref name="changed"/> so it applies it at once. Framework-free: the host maps the routes and hands over the body text.
/// </summary>
public sealed partial class DefinitionsApi(DefinitionPublisher publisher, DefinitionStore store, DefinitionKinds kinds, PayloadStore payloads, IGroupSettings settings, string targetType, DefinitionsAuditor? audit = null, Action<string, string, string>? changed = null,
    long maxArtifactBytes = 512L * 1024 * 1024, IVersionSignatureVerifier? verifier = null, SignaturePolicy? policy = null, DefinitionTrustStore? trust = null, IEnumerable<IDefinitionKindHook>? hooks = null, string payloadOwner = DefinitionsApi.PayloadOwner)
{
    public const string PayloadOwner = "publish";

    private readonly Dictionary<string, IDefinitionKindHook> _hooks = (hooks ?? []).ToDictionary(h => h.Kind, StringComparer.Ordinal);

    private IDefinitionKindHook? HookOf(string kind) => _hooks.GetValueOrDefault(kind);

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Rule();

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,63}$")]
    private static partial Regex ChannelRule();

    public static bool Handles(string route) => route.StartsWith("definitions/", StringComparison.Ordinal) || route.StartsWith("bindings/", StringComparison.Ordinal) || route.StartsWith("artifacts/", StringComparison.Ordinal) || route == "definitions" || route.StartsWith("trust/", StringComparison.Ordinal) || route.StartsWith("settings/signatures/", StringComparison.Ordinal) || route == "settings/group";

    private static DefinitionsResult Error(int status, string message, string? code = null) =>
        new(status, new JsonObject { ["error"] = message, ["code"] = code ?? (status switch { 400 => "invalid", 403 => "denied", 404 => "not_found", 409 => "refused", 413 => "too_large", _ => "error" }), ["message"] = message });

    private static string OwnerKey(string kind, string name) => $"owner/{kind}/{name}";

    // ---- artifacts

    private Guid? Find(string sha) => publisher.FindArtifact(sha) ?? payloads.FindComplete(sha, payloadOwner);

    public bool HasArtifact(string sha256) => Sha256Rule().IsMatch(sha256.ToLowerInvariant()) && Find(sha256.ToLowerInvariant()) is not null;

    /// <summary>Streams an upload into the payload store, hashing as it goes; a hash that is not the one named, or a file past the cap, keeps nothing. A file the group has is not read.</summary>
    public async Task<DefinitionsResult> StoreArtifactAsync(string sha256, Stream body, string? mediaType, DefinitionsCaller caller, CancellationToken ct = default)
    {
        if (!caller.Admin && !caller.Publisher) return Refuse("definition.upload", caller, "artifact", sha256, "-", "uploading needs the publisher role");
        var sha = sha256.ToLowerInvariant();
        if (!Sha256Rule().IsMatch(sha)) return Error(400, $"'{sha256}' is not a SHA-256: write 64 hexadecimal digits.");
        if (Find(sha) is { } existing) return new(200, new JsonObject { ["sha256"] = sha, ["size"] = payloads.Get(existing)?.Size ?? 0, ["already"] = true, ["already_stored"] = true });
        var writer = payloads.OpenWrite(new PayloadOptions(mediaType, payloadOwner, $"upload/{sha}", caller.Actor, MaxBytes: maxArtifactBytes, Label: $"the upload {sha}"));
        var id = writer.Id;
        string? actual = null;
        long size;
        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(buffer, ct)) > 0)
            {
                hash.AppendData(buffer, 0, read);
                await writer.WriteAsync(buffer.AsMemory(0, read), ct);
            }

            size = writer.Length;
            actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (actual == sha) writer.Complete();
        }
        catch (PayloadTooLargeException)
        {
            await writer.DisposeAsync();
            payloads.Delete(id);
            audit?.Invoke("definition.upload", caller, "artifact", sha, "-", "refused", new { reason = "too large", max_bytes = maxArtifactBytes });
            return Error(413, $"The file named {sha} is larger than the cap of {maxArtifactBytes} bytes.");
        }
        catch
        {
            await writer.DisposeAsync();
            payloads.Delete(id);
            throw;
        }

        await writer.DisposeAsync();
        if (actual != sha)
        {
            payloads.Delete(id);
            audit?.Invoke("definition.upload", caller, "artifact", sha, "-", "refused", new { reason = "hash mismatch", sha256_received = actual, size });
            return Error(400, $"The bytes received hash to {actual}, not to {sha}, the hash the upload names; nothing was stored.");
        }

        audit?.Invoke("definition.upload", caller, "artifact", sha, "-", "ok", new { size });
        return new(200, new JsonObject { ["sha256"] = sha, ["size"] = size, ["already"] = false, ["already_stored"] = false });
    }

    // ---- the JSON calls

    /// <summary>Handles a call whose <paramref name="route"/> is relative to <c>/api/v1/</c> (no leading slash) and <see cref="Handles"/> says is this API's.</summary>
    public DefinitionsResult Handle(string method, string route, string? body, DefinitionsCaller caller) => HandleAsync(method, route, body, caller).GetAwaiter().GetResult();

    /// <summary>The same as <see cref="Handle"/> for a host that awaits: a kind's hook may take the life-cycle calls asynchronously (the runner waits for the runs in flight).</summary>
    public async Task<DefinitionsResult> HandleAsync(string method, string route, string? body, DefinitionsCaller caller, CancellationToken ct = default)
    {
        var parts = route.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        var hookOfCall = parts is ["definitions", var hk, ..] ? HookOf(hk) : parts is ["bindings", _, var hk2, ..] ? HookOf(hk2) : null;
        try
        {
            if (parts is ["definitions", var kind] && method == "GET") return List(kind, caller);
            if (parts is ["definitions", var k, var name] && method == "GET") return Describe(k, name, caller);
            if (parts is ["definitions", var kv, var nv, "versions", var number] && method == "GET") return VersionOf(kv, nv, number, caller);
            if (parts is ["definitions", var k2, var n2] && method == "PUT") return Publish(k2, n2, body, caller);
            if (parts is ["definitions", var k3, var n3, var act] && method == "POST") return await Act(k3, n3, act, body, caller, ct).ConfigureAwait(false);
            if (parts is ["trust", var tk] && method == "GET") return Trust(tk, caller);
            if (parts is ["trust", var tk2, "keys"] && method == "PUT") return AddKey(tk2, body, caller);
            if (parts is ["trust", var tk3, var keyId, "revoke"] && method == "POST") return RevokeKey(tk3, keyId, caller);
            if (parts is ["settings", "signatures", var sk] && method == "PUT") return RequireSignatures(sk, body, caller);
            if (parts is ["settings", "group"] && method == "PUT") return GroupMode(body, caller);
            if (parts is ["bindings", var channel, var k4, var n4] && method == "PUT") return Bind(channel, k4, n4, body, caller);
            if (parts is ["bindings", var channel2, var k5, var n5, var slot] && method == "DELETE") return Unbind(channel2, k5, n5, slot, caller);
            return Error(404, "There is no such call.");
        }
        catch (DefinitionsRefusalException ex)
        {
            return ex.Body is { } custom ? new(ex.Status, custom) : Error(ex.Status, ex.Message);
        }
        catch (PublishRefusedException ex)
        {
            var status = hookOfCall?.StatusOf(ex.Reason) ?? ex.Reason switch { PublishRefusal.UnknownKind => 404, PublishRefusal.WrongTarget or PublishRefusal.Invalid or PublishRefusal.Artifact => 400, _ => 409 };
            return Error(status, ex.Message, ex.Reason.ToString().ToLowerInvariant());
        }
        catch (DefinitionConflictException ex)
        {
            return new(409, new JsonObject { ["error"] = ex.Message, ["code"] = "version_conflict", ["message"] = ex.Message, ["current_version"] = ex.Actual });
        }
        catch (DefinitionNotFoundException ex) { return Error(404, ex.Message); }
        catch (DefinitionRejectedException ex) { return Error(400, ex.Message); }
        catch (ArgumentException ex) { return Error(400, ex.Message); }
    }

    private DefinitionsResult? CheckKind(string kind)
    {
        var found = kinds.Find(kind);
        if (found is null) return Error(404, $"The kind '{kind}' is not registered here; the registered kinds are {string.Join(", ", kinds.All.Where(k => k.TargetTypes.Contains(targetType)).Select(k => k.Name))}.", "unknown_kind");
        return found.TargetTypes.Contains(targetType) ? null : Error(400, $"The kind '{kind}' is not accepted by a {targetType} target.", "wrong_target");
    }

    private DefinitionsResult Refuse(string verb, DefinitionsCaller caller, string kind, string name, string channel, string reason)
    {
        audit?.Invoke(verb, caller, kind, name, channel, "refused", new { reason });
        return Error(403, $"This call is refused: {reason}.");
    }

    private bool SignaturesRequired(string kind) => HookOf(kind)?.SignaturesRequired ?? policy?.Required(kind) ?? false;

    private string? Owner(string kind, string name) => settings.Get(OwnerKey(kind, name));

    /// <summary>The owner rules: an administrator may do anything; a publisher creates, and changes only what it created.</summary>
    private DefinitionsResult? Authorize(string verb, DefinitionsCaller caller, string kind, string name, string channel)
    {
        if (caller.Admin) return null;
        if (!caller.Publisher) return Refuse(verb, caller, kind, name, channel, "this needs the publisher role");
        if (store.Versions(kind, name).Count == 0 && Owner(kind, name) is null) return null;
        return caller.ClientId is not null && Owner(kind, name) == caller.ClientId ? null : Refuse(verb, caller, kind, name, channel, "the publisher role changes only the definitions its client created (this one belongs to another publisher or to an administrator), or the admin role does");
    }

    private DefinitionsResult? CanRead(string kind, DefinitionsCaller caller) =>
        caller.Admin || caller.Viewer || (caller.Publisher && (HookOf(kind)?.PublisherMayRead ?? true)) ? null : Error(403, "This needs the viewer role.");

    private DefinitionsResult List(string kind, DefinitionsCaller caller)
    {
        if (CheckKind(kind) is { } bad) return bad;
        if (CanRead(kind, caller) is { } denied) return denied;
        if (HookOf(kind)?.List() is { } listed) return new(200, listed);
        var rows = new JsonArray();
        foreach (var name in publisher.Names(kind))
            rows.Add(new JsonObject { ["name"] = name, ["owner"] = Owner(kind, name), ["channels"] = Heads(kind, name) });
        return new(200, new JsonObject { ["kind"] = kind, ["definitions"] = rows });
    }

    private JsonArray Heads(string kind, string name) =>
        new([.. publisher.HeadsOf(kind, name).Select(h => (JsonNode)new JsonObject { ["channel"] = h.Channel, ["active"] = h.Active, ["previous"] = h.Previous, ["staged"] = h.Staged })]);

    private DefinitionsResult Describe(string kind, string name, DefinitionsCaller caller)
    {
        if (CheckKind(kind) is { } bad) return bad;
        if (CanRead(kind, caller) is { } denied) return denied;
        if (HookOf(kind)?.Describe(name) is { } described) return new(200, described);
        var versions = store.Versions(kind, name);
        if (versions.Count == 0) return Error(404, $"There is no {kind} '{name}'.");
        return new(200, new JsonObject
        {
            ["kind"] = kind, ["name"] = name, ["owner"] = Owner(kind, name), ["latest_version"] = versions.Max(v => v.Version),
            ["versions"] = new JsonArray([.. versions.OrderByDescending(v => v.Version).Select(v => (JsonNode)new JsonObject
            {
                ["version"] = v.Version, ["hash"] = v.Hash, ["published_by"] = v.PublishedBy, ["published_at"] = v.PublishedAt.UtcDateTime.ToString("O"), ["note"] = v.Note, ["format"] = v.Format,
                ["depends_on"] = new JsonArray([.. v.DependsOn.Select(d => (JsonNode)JsonValue.Create(d)!)]), ["files"] = publisher.Artifacts(kind, name, v.Version).Count,
            })]),
            ["channels"] = Heads(kind, name),
            ["bindings"] = new JsonArray([.. publisher.HeadsOf(kind, name).SelectMany(h => publisher.Bindings(h.Channel, kind, name)).Select(b => (JsonNode)new JsonObject { ["channel"] = b.Channel, ["slot"] = b.Slot, ["bound_to"] = b.BoundTo, ["revision"] = b.Revision })]),
        });
    }

    /// <summary>
    /// A stored version whole (PB06): what a promotion to another group reads to publish the same version there. The files are listed by hash; they are read by <see cref="OpenArtifact"/>. The caller must be allowed to read the kind.
    /// </summary>
    private DefinitionsResult VersionOf(string kind, string name, string number, DefinitionsCaller caller)
    {
        if (CheckKind(kind) is { } bad) return bad;
        if (CanRead(kind, caller) is { } denied) return denied;
        if (!int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1) return Error(400, $"'{number}' is not a version number.");
        var v = store.Versions(kind, name).FirstOrDefault(x => x.Version == n);
        if (v is null) return Error(404, $"There is no version {n} of {kind} '{name}'.");
        return new(200, new JsonObject
        {
            ["kind"] = kind, ["name"] = name, ["version"] = v.Version, ["hash"] = v.Hash, ["format"] = v.Format, ["manifest"] = v.Content, ["note"] = v.Note, ["signature"] = v.Signature, ["signer"] = v.Signer,
            ["depends_on"] = new JsonArray([.. v.DependsOn.Select(d => (JsonNode)JsonValue.Create(d)!)]),
            ["artifacts"] = new JsonArray([.. publisher.Artifacts(kind, name, n).Select(a => (JsonNode)new JsonObject { ["path"] = a.Path, ["sha256"] = a.Sha256, ["size"] = a.Size, ["media_type"] = a.MediaType })]),
        });
    }

    /// <summary>The bytes of a stored file by hash (<c>GET artifacts/{sha256}</c>, PB06), or null when the group has none; the host checks that the caller may read and disposes the stream. The hash is checked as the stream is read to its end.</summary>
    public Stream? OpenArtifact(string sha256)
    {
        var sha = sha256.ToLowerInvariant();
        return Sha256Rule().IsMatch(sha) && Find(sha) is { } id ? payloads.OpenRead(id) : null;
    }

    private static JsonObject? Json(string? body, out DefinitionsResult? bad)
    {
        bad = null;
        if (string.IsNullOrWhiteSpace(body)) return [];
        try { if (JsonNode.Parse(body) is JsonObject o) return o; }
        catch (System.Text.Json.JsonException) { }
        bad = Error(400, "The body is a JSON object.");
        return null;
    }

    private static string? Text(JsonObject j, string key) => j[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static bool Int(JsonObject j, string key, out int? value)
    {
        value = null;
        if (j[key] is null) return true;
        if (j[key] is JsonValue v && v.TryGetValue<int>(out var n)) { value = n; return true; }
        return false;
    }

    private DefinitionsResult Publish(string kind, string name, string? body, DefinitionsCaller caller)
    {
        if (CheckKind(kind) is { } badKind) return badKind;
        var json = Json(body, out var badJson);
        if (json is null) return badJson!;
        var channel = Text(json, "channel") ?? DefinitionStore.Production;
        if (!ChannelRule().IsMatch(channel)) return Error(400, $"'{channel}' is not a channel name: use 1 to 64 lowercase letters, digits, '-' or '_'.");
        if (Authorize("definition.publish", caller, kind, name, channel) is { } denied) return denied;
        if (Text(json, "manifest") is not { Length: > 0 } manifest) return Error(400, "The body has no manifest: {\"manifest\": \"<the definition's text>\", \"channel\": \"prod\", \"mode\": \"serve\", \"expected_version\": 3}.");
        if (!Int(json, "expected_version", out var expected) || !Int(json, "format", out var format)) return Error(400, "expected_version and format are numbers.");
        PublishMode? mode = null;
        if (Text(json, "mode") is { } m)
        {
            if (m == "serve") mode = PublishMode.Serve;
            else if (m == "stage") mode = PublishMode.Stage;
            else return Error(400, "mode is serve or stage.");
        }

        var files = new List<ArtifactRef>();
        if (json["artifacts"] is JsonArray list)
            foreach (var item in list)
            {
                if (item is not JsonObject f || Text(f, "path") is not { } path || Text(f, "sha256") is not { } sha || f["size"] is not JsonValue size || !size.TryGetValue<long>(out var bytes)) return Error(400, "An artifact is {path, sha256, size, media_type}.");
                files.Add(new ArtifactRef(path, sha.ToLowerInvariant(), bytes, Text(f, "media_type"), Sha256Rule().IsMatch(sha.ToLowerInvariant()) ? Find(sha.ToLowerInvariant()) : null));
            }

        var hook = HookOf(kind);
        var hookContext = new PublishHookContext(kind, name, channel, manifest, files, caller);
        if (hook is not null)
            try
            {
                hook.CheckChannel(channel);
                hook.BeforePublish(hookContext);
            }
            catch (DefinitionsRefusalException ex)
            {
                audit?.Invoke("definition.publish", caller, kind, name, channel, "refused", new { status = ex.Status, reason = ex.Message });
                throw;
            }

        var verdict = (verifier ?? new NoSignatureVerifier()).Verify(new VersionSignatureRequest(kind, name, manifest, files, Text(json, "signature")));
        if (verdict.State == SignatureState.Invalid || (verdict.State != SignatureState.Verified && SignaturesRequired(kind)))
        {
            var why = verdict.State == SignatureState.Invalid ? $"the signature is refused: {verdict.Message}" : hook?.UnsignedRefusal(name, verdict) ?? $"this group requires signed versions of {kind} and the {kind} '{name}' has no signature (publish it signed with a key an administrator trusts for {kind}, or have an administrator turn signatures off for {kind})";
            audit?.Invoke("definition.publish", caller, kind, name, channel, "refused", new { reason = why, signature = verdict.Word });
            return Error(403, why.Length > 0 ? char.ToUpperInvariant(why[0]) + why[1..] + "." : why, "signature_" + verdict.Word);
        }

        var depends = json["depends_on"] is JsonArray d ? d.Select(x => x?.GetValue<string>() ?? "").ToList() : [];
        var existed = store.Versions(kind, name).Count > 0;
        try
        {
            var result = publisher.Publish(new PublishRequest(kind, name, manifest, caller.Actor, channel, mode, depends, Text(json, "note"), files, ExpectedVersion: expected, Format: format, Signature: verdict.State == SignatureState.Verified ? Text(json, "signature") : null, Signer: verdict.Signer));
            if (!existed && !caller.Admin && caller.ClientId is not null) settings.Put(OwnerKey(kind, name), caller.ClientId);
            audit?.Invoke("definition.publish", caller, kind, name, channel, "ok", new { version = result.Version.Version, mode = result.Mode.ToString().ToLowerInvariant(), changed = result.Changed, files = files.Count, signature = verdict.Word, signer = verdict.Signer });
            changed?.Invoke(kind, name, channel);
            var answer = new JsonObject { ["kind"] = kind, ["name"] = name, ["channel"] = channel, ["version"] = result.Version.Version, ["changed"] = result.Changed, ["signature"] = verdict.Word, ["mode"] = result.Mode.ToString().ToLowerInvariant(), ["depends_on"] = new JsonArray([.. depends.Select(x => (JsonNode)JsonValue.Create(x)!)]) };
            hook?.AfterPublish(hookContext, result, answer);
            return new(200, answer);
        }
        catch (Exception ex) when (ex is PublishRefusedException or DefinitionRejectedException or DefinitionConflictException)
        {
            audit?.Invoke("definition.publish", caller, kind, name, channel, "refused", new { reason = ex.Message });
            throw;
        }
    }

    private async Task<DefinitionsResult> Act(string kind, string name, string act, string? body, DefinitionsCaller caller, CancellationToken ct)
    {
        if (CheckKind(kind) is { } badKind) return badKind;
        if (act is not ("activate" or "switch" or "rollback" or "retire" or "prune")) return Error(404, $"There is no call '{act}'; the life cycle is activate, switch, rollback, retire and prune.");
        var json = Json(body, out var badJson);
        if (json is null) return badJson!;
        var channel = Text(json, "channel") ?? DefinitionStore.Production;
        if (!ChannelRule().IsMatch(channel)) return Error(400, $"'{channel}' is not a channel name.");
        if (!caller.Admin) return Refuse("definition." + act, caller, kind, name, channel, "this needs the admin role");
        if (!Int(json, "expected_version", out var expected) || !Int(json, "version", out var version) || !Int(json, "keep", out var keep)) return Error(400, "expected_version, version and keep are numbers.");
        var by = caller.Actor;
        if (act != "prune" && HookOf(kind) is { } hook)
        {
            var context = new LifecycleHookContext(kind, name, channel, act, expected, version, json, caller);
            try
            {
                hook.CheckChannel(channel);
                if (await hook.ActAsync(context, ct).ConfigureAwait(false) is { } done) return done;
            }
            catch (DefinitionsRefusalException ex)
            {
                audit?.Invoke("definition." + act, caller, kind, name, channel, "refused", new { status = ex.Status, reason = ex.Message });
                throw;
            }
        }

        object detail;
        switch (act)
        {
            case "activate":
                var activated = publisher.Activate(kind, name, by, channel, expected);
                detail = new { version = activated.Version };
                Done(act, caller, kind, name, channel, detail);
                return new(200, new JsonObject { ["kind"] = kind, ["name"] = name, ["channel"] = channel, ["active"] = activated.Version });
            case "switch":
                if (version is null) return Error(400, "switch names the version: {\"channel\": \"prod\", \"version\": 3}.");
                var switched = publisher.Switch(kind, name, version.Value, by, channel, expected);
                Done(act, caller, kind, name, channel, new { version = switched.Version });
                return new(200, new JsonObject { ["kind"] = kind, ["name"] = name, ["channel"] = channel, ["active"] = switched.Version });
            case "rollback":
                var back = publisher.Rollback(kind, name, by, channel, expected);
                Done(act, caller, kind, name, channel, new { version = back.Version });
                return new(200, new JsonObject { ["kind"] = kind, ["name"] = name, ["channel"] = channel, ["active"] = back.Version });
            case "retire":
                var retired = publisher.Retire(kind, name, by, channel, expected);
                Done(act, caller, kind, name, channel, new { retired });
                return new(200, new JsonObject { ["kind"] = kind, ["name"] = name, ["channel"] = channel, ["retired"] = retired });
            default:
                List<int>? versions = null;
                if (json["versions"] is JsonArray array)
                {
                    versions = [];
                    foreach (var item in array)
                    {
                        if (item is JsonValue v && v.TryGetValue<int>(out var number) && number > 0) versions.Add(number);
                        else return Error(400, "versions is a list of version numbers.");
                    }
                }
                else if (json["versions"] is not null) return Error(400, "versions is a list of version numbers.");
                if ((keep is null) == (versions is null)) return Error(400, "Say either keep (how many of the newest versions stay) or versions (which to remove). A call that does not say dry_run: false is a dry run.");
                var dryRun = json["dry_run"] is JsonValue flag && flag.TryGetValue<bool>(out var asked) ? asked : true;
                var plan = publisher.Prune(kind, name, by, keep, versions, dryRun);
                if (!dryRun) audit?.Invoke("definition.prune", caller, kind, name, "-", "ok", new { removed = plan.Remove, files = plan.Artifacts.Count });
                return new(200, new JsonObject
                {
                    ["kind"] = kind, ["name"] = name, ["dry_run"] = !plan.Done,
                    ["remove"] = new JsonArray([.. plan.Remove.Select(v => (JsonNode)JsonValue.Create(v)!)]),
                    ["retained"] = new JsonArray([.. plan.Retained.Select(r => (JsonNode)new JsonObject { ["version"] = r.Version, ["reason"] = r.Reason })]),
                    ["files"] = new JsonArray([.. plan.Artifacts.Select(a => (JsonNode)JsonValue.Create(a)!)]),
                });
        }
    }

    private void Done(string act, DefinitionsCaller caller, string kind, string name, string channel, object detail)
    {
        audit?.Invoke("definition." + act, caller, kind, name, channel, "ok", detail);
        changed?.Invoke(kind, name, channel);
    }

    private DefinitionsResult Bind(string channel, string kind, string name, string? body, DefinitionsCaller caller)
    {
        if (CheckKind(kind) is { } badKind) return badKind;
        if (!ChannelRule().IsMatch(channel)) return Error(400, $"'{channel}' is not a channel name.");
        if (Authorize("definition.bind", caller, kind, name, channel) is { } denied) return denied;
        var json = Json(body, out var badJson);
        if (json is null) return badJson!;
        if (json["slots"] is not JsonObject slots || slots.Count == 0) return Error(400, "The body is {\"slots\": {\"<slot>\": \"<what it is bound to>\"}, \"expected_revision\": <number, or absent>}.");
        if (!Int(json, "expected_revision", out var revision)) return Error(400, "expected_revision is a number.");
        foreach (var (slot, value) in slots)
            if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || string.IsNullOrWhiteSpace(text)) return Error(400, $"The slot '{slot}' must be bound to a name.");
        // the slots of a definition's first version are bound before it exists: the binder then owns the name, as its first publisher would
        if (!caller.Admin && caller.ClientId is not null && store.Versions(kind, name).Count == 0 && settings.Put(OwnerKey(kind, name), caller.ClientId) != caller.ClientId)
            return Refuse("definition.bind", caller, kind, name, channel, $"the {kind} '{name}' belongs to another publisher");
        var hook = HookOf(kind);
        var context = new BindingHookContext(kind, name, channel, slots.ToDictionary(s => s.Key, s => s.Value!.GetValue<string>(), StringComparer.Ordinal), revision, json, caller);
        try
        {
            hook?.CheckChannel(channel);
            hook?.BeforeBind(context);
        }
        catch (DefinitionsRefusalException ex)
        {
            audit?.Invoke("definition.bind", caller, kind, name, channel, "refused", new { status = ex.Status, reason = ex.Message });
            throw;
        }

        foreach (var (slot, value) in context.Slots.OrderBy(s => s.Key, StringComparer.Ordinal)) publisher.Bind(channel, kind, name, slot, value, caller.Actor, revision);
        hook?.AfterBind(context);
        // names, never values, are audited
        audit?.Invoke("definition.bind", caller, kind, name, channel, "ok", new { slots = slots.Select(s => s.Key).Order(StringComparer.Ordinal).ToArray() });
        changed?.Invoke(kind, name, channel);
        return new(200, new JsonObject
        {
            ["kind"] = kind, ["name"] = name, ["channel"] = channel,
            ["bindings"] = new JsonArray([.. publisher.Bindings(channel, kind, name).Select(b => (JsonNode)new JsonObject { ["slot"] = b.Slot, ["bound_to"] = b.BoundTo, ["revision"] = b.Revision })]),
        });
    }

    private DefinitionsResult Unbind(string channel, string kind, string name, string slot, DefinitionsCaller caller)
    {
        if (CheckKind(kind) is { } badKind) return badKind;
        if (Authorize("definition.unbind", caller, kind, name, channel) is { } denied) return denied;
        var hook = HookOf(kind);
        var context = new BindingHookContext(kind, name, channel, new Dictionary<string, string> { [slot] = "" }, null, [], caller);
        try { hook?.BeforeUnbind(context); }
        catch (DefinitionsRefusalException ex)
        {
            audit?.Invoke("definition.unbind", caller, kind, name, channel, "refused", new { slot, status = ex.Status, reason = ex.Message });
            throw;
        }

        var removed = publisher.Unbind(channel, kind, name, slot, caller.Actor);
        audit?.Invoke("definition.unbind", caller, kind, name, channel, "ok", new { slot, removed });
        if (removed) changed?.Invoke(kind, name, channel);
        if (hook?.AfterUnbind(context, removed) is { } replaced) return replaced;
        return new(200, new JsonObject { ["kind"] = kind, ["name"] = name, ["channel"] = channel, ["slot"] = slot, ["removed"] = removed });
    }

    // ---- the trust store and the signature policy (PB03)

    private DefinitionsResult? NeedAdmin(string verb, DefinitionsCaller caller, string kind) => caller.Admin ? null : Refuse(verb, caller, kind, "-", "-", "this needs the admin role");

    private DefinitionsResult Trust(string kind, DefinitionsCaller caller)
    {
        if (CheckKind(kind) is { } bad) return bad;
        if (CanRead(kind, caller) is { } denied) return denied;
        return new(200, new JsonObject
        {
            ["kind"] = kind, ["required"] = SignaturesRequired(kind), ["test_group"] = policy?.IsTestGroup ?? false,
            ["keys"] = new JsonArray([.. (trust?.List(kind) ?? []).Select(k => (JsonNode)new JsonObject { ["key_id"] = k.KeyId, ["label"] = k.Label, ["added_by"] = k.AddedBy, ["added_at"] = k.AddedAt.UtcDateTime.ToString("O"), ["revoked"] = k.Revoked, ["revoked_by"] = k.RevokedBy })]),
        });
    }

    private DefinitionsResult AddKey(string kind, string? body, DefinitionsCaller caller)
    {
        if (CheckKind(kind) is { } bad) return bad;
        if (NeedAdmin("definition.trust", caller, kind) is { } denied) return denied;
        if (trust is null) return Error(404, "This host keeps no trust store.");
        var json = Json(body, out var badJson);
        if (json is null) return badJson!;
        var key = Text(json, "public_key") is { } text ? Url.Decode(text) : null;
        if (key is not { Length: 32 }) return Error(400, "The body is {\"public_key\": \"<the 32-byte Ed25519 public key, base64url>\", \"label\": \"...\"}.");
        var added = trust.Add(kind, key, Text(json, "label"), caller.Actor);
        audit?.Invoke("definition.trust", caller, kind, "-", "-", "ok", new { key_id = added.KeyId });
        return new(200, new JsonObject { ["kind"] = kind, ["key_id"] = added.KeyId, ["revoked"] = added.Revoked });
    }

    private DefinitionsResult RevokeKey(string kind, string keyId, DefinitionsCaller caller)
    {
        if (CheckKind(kind) is { } bad) return bad;
        if (NeedAdmin("definition.revoke", caller, kind) is { } denied) return denied;
        if (trust is null) return Error(404, "This host keeps no trust store.");
        var revoked = trust.Revoke(kind, keyId, caller.Actor);
        audit?.Invoke("definition.revoke", caller, kind, "-", "-", revoked ? "ok" : "refused", new { key_id = keyId });
        return revoked ? new(200, new JsonObject { ["kind"] = kind, ["key_id"] = keyId, ["revoked"] = true }) : Error(404, $"There is no key {keyId} of {kind} to revoke (or it is revoked already).");
    }

    private DefinitionsResult RequireSignatures(string kind, string? body, DefinitionsCaller caller)
    {
        if (CheckKind(kind) is { } bad) return bad;
        if (NeedAdmin("definition.signatures", caller, kind) is { } denied) return denied;
        if (policy is null) return Error(404, "This host keeps no signature policy.");
        var json = Json(body, out var badJson);
        if (json is null) return badJson!;
        if (json["required"] is not JsonValue v || !v.TryGetValue<bool>(out var required)) return Error(400, "The body is {\"required\": true}.");
        policy.Set(kind, required);
        audit?.Invoke("definition.signatures", caller, kind, "-", "-", "ok", new { required });
        return new(200, new JsonObject { ["kind"] = kind, ["required"] = required });
    }

    private DefinitionsResult GroupMode(string? body, DefinitionsCaller caller)
    {
        if (NeedAdmin("definition.group", caller, "-") is { } denied) return denied;
        if (policy is null) return Error(404, "This host keeps no signature policy.");
        var json = Json(body, out var badJson);
        if (json is null) return badJson!;
        if (Text(json, "mode") is not ("test" or "production")) return Error(400, "The body is {\"mode\": \"test\"} or {\"mode\": \"production\"}.");
        policy.MarkTestGroup(Text(json, "mode") == "test");
        audit?.Invoke("definition.group", caller, "-", "-", "-", "ok", new { mode = Text(json, "mode") });
        return new(200, new JsonObject { ["mode"] = Text(json, "mode") });
    }
}
