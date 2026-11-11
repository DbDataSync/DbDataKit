using System.Buffers.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Authoring.Server;

/// <summary>A publish was refused or could not be done; <see cref="Status"/> is the HTTP status the route answers with, and the message is a sentence a person can act on (the Gateway's own words when it refused).</summary>
public sealed class WorkspacePublishException(string message, int status) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>A Gateway this server publishes to: where it is, its instance id, the certificate fingerprint every call pins, the roles the pairing allows and the name the client was paired under. Kept in <c>publish-targets.json</c>, never the key.</summary>
public sealed record PublishTarget(string Name, string Url, string InstanceId, string Fingerprint, IReadOnlyList<string> Roles, string ClientName, DateTimeOffset PairedAt);

/// <summary>
/// Publishing a built app to a paired Gateway (plan 22a GP35, plan 30): the build's folder is sent as an app version through the Gateway's publish API (<c>POST /api/v1/apps/check</c>, then <c>PUT /api/v1/apps/{name}</c>), signed as the
/// <b>publisher key</b> of the target. The key is an identity (an id and a signing seed) kept in this server's own secret store; what the <c>publisher_key</c> table holds is only its <b>reference</b> (<c>secret:dbdata:workspace:...</c>), read
/// through the server's secret resolver at the moment of publishing and never returned by any call. A target is made by <see cref="PairAsync"/> (a single-use pairing code made on the Gateway with <c>server pair --max-role publisher</c>):
/// it makes the key, pairs with the Gateway so it knows the key's public half, and records the reference and the Gateway's address and certificate fingerprint.
/// </summary>
public sealed class WorkspacePublishing(string directory, WorkspaceStore store, Func<ISecretStore> keys, Func<ISecretResolver> secrets)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();

    private ISecretResolver Resolver => secrets();

    private string TargetsFile => Path.Combine(directory, "publish-targets.json");

    public IReadOnlyList<PublishTarget> Targets()
    {
        lock (gate)
        {
            if (!File.Exists(TargetsFile)) return [];
            try { return JsonSerializer.Deserialize<List<PublishTarget>>(File.ReadAllText(TargetsFile), Json) ?? []; }
            catch (JsonException) { throw new InvalidDataException($"{TargetsFile} is not a list of publish targets; remove it and pair again."); }
        }
    }

    public PublishTarget? Find(string name) => Targets().FirstOrDefault(t => t.Name == name);

    private void Save(PublishTarget target)
    {
        lock (gate)
        {
            var all = Targets().Where(t => t.Name != target.Name).Append(target).OrderBy(t => t.Name, StringComparer.Ordinal);
            File.WriteAllText(TargetsFile, JsonSerializer.Serialize(all, Json));
        }
    }

    /// <summary>The name a target's key is stored under in the secret store, and so the reference a new target gets.</summary>
    public static string ReferenceFor(string name) => new SecretName("workspace", "publisher-key", name).ToRef().ToString();

    /// <summary>Makes the key for the target <paramref name="name"/>, pairs with the Gateway at <paramref name="url"/> with it (the Gateway then knows its public half) and records the reference and the Gateway. An earlier key of the name is replaced.</summary>
    public async Task<PublishTarget> PairAsync(string name, Uri url, string code, string? clientName, string by, CancellationToken ct = default)
    {
        if (!WorkspaceProjects.IsValidId(name)) throw new WorkspacePublishException($"'{name}' is not a name: lower-case letters, digits, '-' and '_', at most 32.", 400);
        var identity = new ConsoleIdentity("workspace-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), ConsoleSigningKey.Generate());
        var label = clientName is { Length: > 0 } ? clientName : $"dbdataworkspace@{Environment.MachineName}";
        PairedServer paired;
        try { paired = await PairingClient.PairAsync(url, code, identity, ClientKind.Server, label, ct); }
        catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException) { throw new WorkspacePublishException(ex.Message, 409); }
        // the key is kept only once the Gateway has accepted it
        var secret = new SecretName("workspace", "publisher-key", name);
        using (var value = SecretValue.From(identity.Id + ":" + Base64Url.EncodeToString(identity.Key.ExportSeed())))
            await keys().SetAsync(secret, value, ct);
        var target = new PublishTarget(name, paired.Url.ToString().TrimEnd('/'), paired.InstanceId, paired.CertificateFingerprint, ServerRoles.Names(paired.Ceiling), label, DateTimeOffset.UtcNow);
        Save(target);
        store.SetPublisherKey(name, secret.ToRef().ToString(), by);
        return target;
    }

    /// <summary>The key a reference names: its identity. Refused, in words, when the reference names nothing or something that is not a key.</summary>
    private async Task<ConsoleIdentity> KeyAsync(string name, string reference, CancellationToken ct)
    {
        if (!SecretRef.TryParse(reference, out var parsed)) throw new WorkspacePublishException($"The publisher key '{name}' names '{reference}', which is not a secret reference.", 409);
        SecretValue value;
        try { value = await Resolver.ResolveAsync(parsed, ct); }
        catch (SecretNotFoundException) { throw new WorkspacePublishException($"The secret {parsed} that holds the publisher key '{name}' is not in this server's secret store. Pair the target again (`dbdataworkspace publisher pair {name} --url <gateway> --code <code>`) or put the key there.", 409); }
        using (value)
        {
            var text = value.RevealString();
            var split = text.IndexOf(':');
            byte[]? seed = null;
            try { seed = split > 0 ? Base64Url.DecodeFromChars(text.AsSpan(split + 1)) : null; }
            catch (FormatException) { /* damaged: said below */ }
            if (seed is not { Length: ConsoleSigningKey.SeedLength }) throw new WorkspacePublishException($"The secret {parsed} does not hold a publisher key (an id and a signing seed). Pair the target again to make one.", 409);
            return new ConsoleIdentity(text[..split], ConsoleSigningKey.FromSeed(seed));
        }
    }

    /// <summary>
    /// Publishes the app in <paramref name="appFolder"/> (a build's output) to the target <paramref name="targetName"/>. <paramref name="step"/> is told each step as it happens (the screen's progress). The Gateway first checks what publishing
    /// would do (its refusal, in its words, ends the publish before anything is stored); then the version is published. Returns what the Gateway answered.
    /// </summary>
    public async Task<JsonObject> PublishAsync(string targetName, string appFolder, string? note, string actor, Action<string> step, CancellationToken ct = default)
    {
        if (store.PublisherKeys().FirstOrDefault(k => k.Name == targetName) is not { } key)
            throw new WorkspacePublishException($"There is no publisher key called '{targetName}'. An administrator adds one (a reference to the secret that holds the key) under Publisher keys.", 409);
        if (Find(targetName) is not { } target)
            throw new WorkspacePublishException($"The publisher key '{targetName}' has no Gateway paired with it. Pair it (`dbdataworkspace publisher pair {targetName} --url <gateway> --code <code>`; the code is made on the Gateway with `server pair --max-role publisher`).", 409);
        var ceiling = ServerRoles.TryParse(target.Roles, out var parsed) ? parsed : ServerRole.None;
        var role = ServerRoles.Allows(ceiling, ServerRole.Publisher) ? ServerRole.Publisher : ServerRoles.Allows(ceiling, ServerRole.Admin) ? ServerRole.Admin : ServerRole.None;
        if (role == ServerRole.None)
            throw new WorkspacePublishException($"This server is paired with {target.Url} with the roles {string.Join(", ", target.Roles)}, and publishing an app needs the publisher role. Pair again with a code made by `server pair --max-role publisher` on the Gateway.", 409);

        step("Read the built app.");
        AppPackage package;
        try { package = AppPackage.FromFolder(appFolder); }
        catch (Exception ex) when (ex is AppPackageException or IOException) { throw new WorkspacePublishException($"The built app cannot be sent: {ex.Message}", 409); }
        var body = new JsonObject { ["manifest"] = package.ManifestYaml, ["files"] = new JsonObject(package.Files.OrderBy(f => f.Key, StringComparer.Ordinal).Select(f => KeyValuePair.Create(f.Key, (JsonNode?)Convert.ToBase64String(f.Value)))), ["note"] = note };
        var name = AppManifestLoader.Load(package.ManifestYaml) is { Ok: true } load ? load.Manifest!.Name : throw new WorkspacePublishException("The built app's manifest is not valid.", 409);

        step("Read the publisher key '" + targetName + "' from the secret store.");
        var identity = await KeyAsync(targetName, key.SecretRef, ct);

        using var http = new HttpClient(PairingClient.Pinned(target.Fingerprint)) { BaseAddress = new Uri(target.Url + "/"), Timeout = TimeSpan.FromMinutes(5) };
        async Task<JsonObject> Call(HttpMethod method, string path, string doing)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, identity.Issuer().Issue(new ConsoleTokenRequest(target.InstanceId, "client:" + identity.Id, target.ClientName, role)));
            request.Content = JsonContent.Create(body, options: Json);
            HttpResponseMessage response;
            try { response = await http.SendAsync(request, ct); }
            catch (HttpRequestException ex) { throw new WorkspacePublishException($"{target.Url} could not be reached ({ex.Message}).", 502); }
            using (response)
            {
                var text = await response.Content.ReadAsStringAsync(ct);
                JsonObject? answer = null;
                try { answer = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject; }
                catch (JsonException) { /* not JSON: the text is the answer */ }
                if (!response.IsSuccessStatusCode)
                    throw new WorkspacePublishException($"The Gateway refused {doing} ({(int)response.StatusCode}): {(string?)answer?["error"] ?? (text.Length > 0 ? text : response.ReasonPhrase)}", response.StatusCode == System.Net.HttpStatusCode.Forbidden ? 403 : 409);
                return answer ?? [];
            }
        }

        step($"Asked {target.Url} what publishing {name} would do.");
        var check = await Call(HttpMethod.Post, "api/v1/apps/check", $"the check of the app {name}");
        if (check["diagnostics"] is JsonArray diagnostics && diagnostics.Any(d => (string?)d?["severity"] is "error" or "Error"))
            throw new WorkspacePublishException("The Gateway's check of the app found errors: " + string.Join(" ", diagnostics.Where(d => (string?)d?["severity"] is "error" or "Error").Select(d => (string?)d?["message"] ?? d!.ToJsonString())), 409);
        step($"Published {name} to {target.Url}.");
        var published = await Call(HttpMethod.Put, $"api/v1/apps/{Uri.EscapeDataString(name)}", $"the app {name}");
        published["app"] = name;
        published["target"] = targetName;
        published["gateway"] = target.Url;
        return published;
    }
}
