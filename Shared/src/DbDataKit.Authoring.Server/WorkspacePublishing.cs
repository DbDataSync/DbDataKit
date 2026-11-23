using System.Buffers.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Deploy;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Authoring.Server;

/// <summary>A publish was refused or could not be done; <see cref="Status"/> is the HTTP status the route answers with, and the message is a sentence a person can act on (the Gateway's own words when it refused).</summary>
public sealed class WorkspacePublishException(string message, int status, System.Text.Json.Nodes.JsonObject? adopt = null) : Exception(message)
{
    public int Status { get; } = status;

    /// <summary>Set when the Gateway refused for want of an adoption (plan 33 SC21f): the data source a model would go on and who made it (<c>data_source</c>, <c>made_by</c>); a screen asks the person and publishes again with <c>adopt</c>.</summary>
    public System.Text.Json.Nodes.JsonObject? Adopt { get; } = adopt;

    /// <summary>The same refusal as the shared deploy library's, with its status and adoption.</summary>
    public static WorkspacePublishException From(DeployException ex) => new(ex.Message, ex.Status, ex.Adopt);

    /// <summary>Runs a call of the shared deploy library and gives its refusal as this server's.</summary>
    internal static void Adapt(Action call)
    {
        try { call(); }
        catch (DeployException ex) { throw From(ex); }
    }

    /// <summary>Runs a call of the shared deploy library and gives its refusal as this server's.</summary>
    internal static T Adapt<T>(Func<T> call)
    {
        try { return call(); }
        catch (DeployException ex) { throw From(ex); }
    }

    /// <summary>Awaits a call of the shared deploy library and gives its refusal as this server's.</summary>
    internal static async Task<T> AdaptAsync<T>(Func<Task<T>> call)
    {
        try { return await call(); }
        catch (DeployException ex) { throw From(ex); }
    }
}

/// <summary>A server this server publishes to (a Gateway, or since plan 33 SC20b a Runner): where it is, its instance id, the certificate fingerprint every call pins, the roles the pairing allows, the name the client was paired under and the product it is (<c>gateway</c> or <c>runner</c>; a file written before the field existed has none, and those targets are Gateways). Kept in <c>publish-targets.json</c>, never the key.</summary>
public sealed record PublishTarget(string Name, string Url, string InstanceId, string Fingerprint, IReadOnlyList<string> Roles, string ClientName, DateTimeOffset PairedAt, string Product = PublishProducts.Gateway);

/// <summary>The products a publish target can be (plan 33 SC20b) and how a server's own name (<c>GET /version</c>) says which: <see cref="DeployProducts"/>, under this server's name.</summary>
public static class PublishProducts
{
    public const string Gateway = DeployProducts.Gateway;
    public const string Runner = DeployProducts.Runner;

    public static IReadOnlyList<string> All => DeployProducts.All;

    /// <summary>The product a server name stands for, or null when it is neither (<c>dbdatagateway</c>, <c>dbdatarunner</c>).</summary>
    public static string? OfServer(string? serverName) => DeployProducts.OfServer(serverName);
}

/// <summary>
/// Publishing a built app to a paired Gateway (plan 22a GP35, plan 30): the build's folder is sent as an app version through the Gateway's publish API (<c>POST /api/v1/apps/check</c>, then <c>PUT /api/v1/apps/{name}</c>), signed as the
/// <b>publisher key</b> of the target. The key is an identity (an id and a signing seed) kept in this server's own secret store; what the <c>publisher_key</c> table holds is only its <b>reference</b> (<c>secret:dbdatakit:workspace:...</c>), read
/// through the server's secret resolver at the moment of publishing and never returned by any call. A target is made by <see cref="PairAsync"/> (a single-use pairing code made on the Gateway with <c>server pair --max-role publisher</c>):
/// it makes the key, pairs with the Gateway so it knows the key's public half, and records the reference and the Gateway's address and certificate fingerprint.
/// <para>
/// The calls to a Gateway or a Runner are the shared deploy library's (<c>DbDataKit.Deploy</c>): this class finds the target and its key, makes a <see cref="PinnedLink"/> that signs each call with the key, and gives the library's
/// <see cref="DeployException"/> as a <see cref="WorkspacePublishException"/>.
/// </para>
/// </summary>
public sealed partial class WorkspacePublishing(string directory, WorkspaceStore store, Func<ISecretStore> keys, Func<ISecretResolver> secrets)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object gate = new();

    /// <summary>The Gateway channel a preview with data goes to (plan 21, plan 22a GP35d).</summary>
    public const string PreviewChannel = AppDeploy.PreviewChannel;

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
    public async Task<PublishTarget> PairAsync(string name, Uri url, string code, string? clientName, string by, CancellationToken ct = default, string? product = null)
    {
        if (product is not null && !PublishProducts.All.Contains(product)) throw new WorkspacePublishException($"'{product}' is not a product a target can be ({string.Join(", ", PublishProducts.All)}).", 400);
        if (!WorkspaceProjects.IsValidId(name)) throw new WorkspacePublishException($"'{name}' is not a name: lower-case letters, digits, '-' and '_', at most 32.", 400);
        var identity = new ConsoleIdentity("workspace-" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), ConsoleSigningKey.Generate());
        var label = clientName is { Length: > 0 } ? clientName : $"dbdataworkspace@{Environment.MachineName}";
        PairedServer paired;
        try { paired = await PairingClient.PairAsync(url, code, identity, ClientKind.Server, label, ct); }
        catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException) { throw new WorkspacePublishException(ex.Message, 409); }
        var detected = await DetectProductAsync(paired, ct);
        if (detected is not null && product is not null && detected != product)
            throw new WorkspacePublishException($"{paired.Url} is a {detected}, not a {product}; pair it without naming a product, or name {detected}.", 409);
        var resolved = detected ?? product ?? PublishProducts.Gateway;
        // the key is kept only once the server has accepted it
        var secret = new SecretName("workspace", "publisher-key", name);
        using (var value = SecretValue.From(identity.Id + ":" + Base64Url.EncodeToString(identity.Key.ExportSeed())))
            await keys().SetAsync(secret, value, ct);
        var target = new PublishTarget(name, paired.Url.ToString().TrimEnd('/'), paired.InstanceId, paired.CertificateFingerprint, ServerRoles.Names(paired.Ceiling), label, DateTimeOffset.UtcNow, resolved);
        Save(target);
        store.SetPublisherKey(name, secret.ToRef().ToString(), by);
        return target;
    }

    /// <summary>Which product the paired server is, from its own <c>GET /version</c> (no token; the certificate is the pinned one). Null when it does not say, or says something that is neither a Gateway nor a Runner.</summary>
    private static async Task<string?> DetectProductAsync(PairedServer paired, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = new Uri(paired.Url.ToString().TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(15) };
            var text = await http.GetStringAsync("version", ct);
            return PublishProducts.OfServer((string?)JsonNode.Parse(text)?["product"] ?? (string?)JsonNode.Parse(text)?["Product"]);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException) { return null; }
    }

    /// <summary>
    /// Whether a project of <paramref name="kind"/> may be published to <paramref name="target"/> (plan 33 SC20b): the kind's <see cref="IProjectKind.Publishes"/> must list the target's product. Returns the route (what arrives there), or throws a
    /// <see cref="WorkspacePublishException"/> naming the kind, the target, its product and the products the kind goes to (<see cref="DeployRoutes.RouteFor"/>).
    /// </summary>
    public static ProjectPublishRoute RouteFor(IProjectKind kind, PublishTarget target, string? what = null) =>
        WorkspacePublishException.Adapt(() => DeployRoutes.RouteFor(kind.Name, kind.Publishes, DeployTargetOf(target), what));

    /// <summary>A paired target as the shared deploy library names it.</summary>
    internal static DeployTarget DeployTargetOf(PublishTarget target) => new(target.Name, target.Url, target.InstanceId, target.Product);

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

    /// <summary>The target a publish names, its key's reference and the role the pairing lets it publish with (publisher, else admin). Refused, in words, when there is no key, no server paired with it or no role to publish with; <paramref name="what"/> says what is being published.</summary>
    private (PublishTarget Target, PublisherKeyRow Key, ServerRole Role) ResolveTarget(string targetName, string what)
    {
        if (store.PublisherKeys().FirstOrDefault(k => k.Name == targetName) is not { } key)
            throw new WorkspacePublishException($"There is no publisher key called '{targetName}'. An administrator adds one (a reference to the secret that holds the key) under Publisher keys.", 409);
        if (Find(targetName) is not { } target)
            throw new WorkspacePublishException($"The publisher key '{targetName}' has no Gateway paired with it. Pair it (`dbdataworkspace publisher pair {targetName} --url <gateway> --code <code>`; the code is made on the Gateway with `server pair --max-role publisher`).", 409);
        var ceiling = ServerRoles.TryParse(target.Roles, out var parsed) ? parsed : ServerRole.None;
        var role = ServerRoles.Allows(ceiling, ServerRole.Publisher) ? ServerRole.Publisher : ServerRoles.Allows(ceiling, ServerRole.Admin) ? ServerRole.Admin : ServerRole.None;
        if (role == ServerRole.None)
            throw new WorkspacePublishException($"This server is paired with {target.Url} with the roles {string.Join(", ", target.Roles)}, and publishing {what} needs the publisher role. Pair again with a code made by `server pair --max-role publisher` on the Gateway.", 409);

        return (target, key, role);
    }

    /// <summary>
    /// Publishes the app in <paramref name="appFolder"/> (a build's output) to the target <paramref name="targetName"/>. <paramref name="step"/> is told each step as it happens (the screen's progress). The Gateway first checks what publishing
    /// would do (its refusal, in its words, ends the publish before anything is stored); then the version is published. Returns what the Gateway answered.
    /// </summary>
    public async Task<JsonObject> PublishAsync(string targetName, string appFolder, string? note, string actor, Action<string> step, CancellationToken ct = default, string? previewInstance = null)
    {
        var (target, key, role) = ResolveTarget(targetName, "an app");
        var app = WorkspacePublishException.Adapt(() => AppDeploy.Read(appFolder, note, step));

        step("Read the publisher key '" + targetName + "' from the secret store.");
        var identity = await KeyAsync(targetName, key.SecretRef, ct);

        using var link = new PinnedLink(target, identity, "client:" + identity.Id, target.ClientName, role, TimeSpan.FromMinutes(5));
        return await WorkspacePublishException.AdaptAsync(() => AppDeploy.SendAsync(link, DeployTargetOf(target), app, step, ct, previewInstance));
    }

    /// <summary>
    /// A paired server as the shared deploy library reaches it: requests relative to <c>/api/v1/</c> over the pinned certificate, each with a fresh token signed with the publisher key and naming its client (<c>subject</c>),
    /// the display name and the role the pairing allows. Its name is the server's address, which messages name.
    /// </summary>
    internal sealed class PinnedLink(PublishTarget target, ConsoleIdentity identity, string subject, string displayName, ServerRole role, TimeSpan timeout) : IDefinitionsLink, IDisposable
    {
        private readonly HttpClient http = new(PairingClient.Pinned(target.Fingerprint)) { BaseAddress = new Uri(target.Url + "/"), Timeout = timeout };

        public string Name => target.Url;

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
        {
            var request = new HttpRequestMessage(method, "api/v1/" + path) { Content = content };
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, identity.Issuer().Issue(new ConsoleTokenRequest(target.InstanceId, subject, displayName, role)));
            return http.SendAsync(request, ct);
        }

        public void Dispose() => http.Dispose();
    }
}
