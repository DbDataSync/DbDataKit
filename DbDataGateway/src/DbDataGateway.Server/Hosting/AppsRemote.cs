using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Server.Hosting;

/// <summary>A call to a Gateway failed or was refused; the message is the Gateway's own, or says what could not be done on this machine.</summary>
public sealed class AppsRemoteException(string message) : InvalidOperationException(message);

/// <summary>A Gateway (or serving group) this machine is paired with: where it is, its instance id, the certificate fingerprint every call pins, the roles the pairing allows and the name the client was paired under.</summary>
public sealed record PairedGatewayServer(string Name, string Url, string InstanceId, string Fingerprint, IReadOnlyList<string> Roles, string ClientName, DateTimeOffset PairedAt);

/// <summary>
/// The client side of pairing for <c>apps ... --to</c> (plan 26, AP26), the same shape as the runner's (<c>RunnerClientStore</c>) and the Store's (<c>StoreProxies</c>): the Gateways this machine is paired with in <c>apps-servers.json</c> of the
/// client directory, and the key it signs its tokens with in the encrypted secret store beside it. The directory is <c>--client-dir</c>, <see cref="DirectoryVariable"/> or a folder under the user's local application data.
/// </summary>
public sealed class AppsClientStore(string directory)
{
    public const string DirectoryVariable = "DBDATAKIT_GATEWAY_CLIENT_DIR";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Directory { get; } = Path.GetFullPath(directory);

    private string ServersFile => Path.Combine(Directory, "apps-servers.json");

    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(DirectoryVariable) is { Length: > 0 } named
            ? named
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatagateway", "client");

    public IReadOnlyList<PairedGatewayServer> All()
    {
        if (!File.Exists(ServersFile)) return [];
        try { return JsonSerializer.Deserialize<List<PairedGatewayServer>>(File.ReadAllText(ServersFile), Json) ?? []; }
        catch (JsonException) { throw new InvalidDataException($"{ServersFile} is not a list of paired gateways; remove it and pair again."); }
    }

    /// <summary>The Gateway paired under this name or at this address.</summary>
    public PairedGatewayServer? Find(string nameOrUrl) =>
        All().FirstOrDefault(p => string.Equals(p.Name, nameOrUrl, StringComparison.Ordinal) || string.Equals(p.Url.TrimEnd('/'), nameOrUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    public void Add(PairedGatewayServer gateway)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(ServersFile, JsonSerializer.Serialize(All().Where(p => p.Name != gateway.Name).Append(gateway).OrderBy(p => p.Name, StringComparer.Ordinal), Json), new UTF8Encoding(false));
    }

    public Task<ConsoleIdentity> IdentityAsync(Func<string, string?>? environment = null, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        IKeyProtector? protector = OperatingSystem.IsWindows() ? new DbDataKit.Secrets.Windows.DpapiKeyProtector(Encoding.UTF8.GetBytes("DbDataGatewayClient")) : null;
        var store = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(Directory), SecretKeys.Open(Directory, new SecretKeyOptions(), protector, environment));
        return new ConsoleKeyStore(store, store).GetOrCreateAsync(ct);
    }
}

/// <summary>The words of an answer: the Gateway's version of what it did.</summary>
public sealed record RemoteAnswer(JsonObject Body);

/// <summary>
/// Publishes apps to a Gateway over its management API as a paired server client (plan 26, AP26): it pairs once with a single-use code (<c>dbdatagateway server pair --max-role publisher</c> on the Gateway), pins the certificate, and signs each
/// call with the client's own key. The token names the paired client (its subject is <c>client:&lt;id&gt;</c>, its name is the one it was paired under), and that is the author the Gateway records, never the operating-system user here. A pairing has a
/// ceiling: publishing, instances and rollback ask for the publisher role, approving for the approver role (a second pairing), and a call whose role the pairing lacks is refused here, naming the pairing to make, before it is sent.
/// </summary>
public sealed class AppsRemote(AppsClientStore store, Func<string, string?>? environment = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Pairs with the Gateway at <paramref name="url"/> using its code and remembers it as <paramref name="name"/> (default: its address) with the client name <paramref name="clientName"/> (default <c>dbdatagateway-cli@&lt;machine&gt;</c>).</summary>
    public async Task<PairedGatewayServer> PairAsync(Uri url, string code, string? name, string? clientName, CancellationToken ct = default)
    {
        var self = await store.IdentityAsync(environment, ct);
        var label = clientName is { Length: > 0 } ? clientName : $"dbdatagateway-cli@{Environment.MachineName}";
        PairedServer paired;
        try { paired = await PairingClient.PairAsync(url, code, self, ClientKind.Server, label, ct); }
        catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException) { throw new AppsRemoteException(ex.Message); }
        var gateway = new PairedGatewayServer(name is { Length: > 0 } ? name : url.Authority, paired.Url.ToString().TrimEnd('/'), paired.InstanceId, paired.CertificateFingerprint, ServerRoles.Names(paired.Ceiling), label, DateTimeOffset.UtcNow);
        store.Add(gateway);
        return gateway;
    }

    /// <summary>Publishes a version: <c>PUT /api/v1/apps/{name}</c>.</summary>
    public async Task<RemoteAnswer> PublishAsync(string server, string? code, string? alias, string? clientName, AppPackage package, string? note, Action<string>? log = null, CancellationToken ct = default)
    {
        var name = NameOf(package);
        return await CallAsync(server, code, alias, clientName, ServerRole.Publisher, HttpMethod.Put, $"api/v1/apps/{Uri.EscapeDataString(name)}", Encode(package, new JsonObject { ["note"] = note }), $"the app {name}", log, ct);
    }

    /// <summary>Makes or changes an instance: <c>PUT /api/v1/app-instances/{name}</c>.</summary>
    public Task<RemoteAnswer> SaveInstanceAsync(string server, string? code, string? alias, string? clientName, AppInstance instance, Action<string>? log = null, CancellationToken ct = default) =>
        CallAsync(server, code, alias, clientName, ServerRole.Publisher, HttpMethod.Put, $"api/v1/app-instances/{Uri.EscapeDataString(instance.Name)}",
            new JsonObject
            {
                ["app"] = instance.App, ["pin"] = instance.Pin, ["title"] = instance.Title,
                ["settings"] = JsonSerializer.SerializeToNode(instance.Settings, Json), ["bindings"] = JsonSerializer.SerializeToNode(instance.Bindings, Json),
            }, $"the instance {instance.Name}", log, ct);

    /// <summary>Moves the version an app serves: <c>POST /api/v1/apps/{name}/rollback</c>.</summary>
    public Task<RemoteAnswer> RollbackAsync(string server, string? code, string? alias, string? clientName, string app, int? to, Action<string>? log = null, CancellationToken ct = default) =>
        CallAsync(server, code, alias, clientName, ServerRole.Publisher, HttpMethod.Post, $"api/v1/apps/{Uri.EscapeDataString(app)}/rollback", new JsonObject { ["to"] = to }, $"the rollback of {app}", log, ct);

    /// <summary>The published apps: <c>GET /api/v1/apps</c>.</summary>
    public Task<RemoteAnswer> ListAsync(string server, string? code, string? alias, string? clientName, Action<string>? log = null, CancellationToken ct = default) =>
        CallAsync(server, code, alias, clientName, ServerRole.Publisher, HttpMethod.Get, "api/v1/apps", null, "the list of apps", log, ct);

    /// <summary>Approves exact bytes, as <paramref name="by"/>, a user of the identity home who holds the Approver role there: <c>POST /api/v1/app-approvals</c>. Needs a pairing with the approver role.</summary>
    public Task<RemoteAnswer> ApproveAsync(string server, string? code, string? alias, string? clientName, JsonObject what, string by, string? note, string? egress, Action<string>? log = null, CancellationToken ct = default)
    {
        what["by"] = by;
        what["note"] = note;
        what["egress"] = egress;
        return CallAsync(server, code, alias, clientName, ServerRole.Approver, HttpMethod.Post, "api/v1/app-approvals", what, "the approval", log, ct);
    }

    /// <summary>The body for a package: its manifest and each file as base64.</summary>
    public static JsonObject Encode(AppPackage package, JsonObject? extra = null)
    {
        var files = new JsonObject();
        foreach (var (path, bytes) in package.Files.OrderBy(f => f.Key, StringComparer.Ordinal)) files[path] = Convert.ToBase64String(bytes);
        var body = new JsonObject { ["manifest"] = package.ManifestYaml, ["files"] = files };
        foreach (var (key, value) in extra ?? []) body[key] = value?.DeepClone();
        return body;
    }

    private static string NameOf(AppPackage package)
    {
        var load = AppManifestLoader.Load(package.ManifestYaml);
        return load.Ok ? load.Manifest!.Name : throw new AppsRemoteException("The manifest is not valid: " + string.Join(" ", load.Diagnostics.Select(d => d.ToString())));
    }

    private async Task<RemoteAnswer> CallAsync(string server, string? code, string? alias, string? clientName, ServerRole needed, HttpMethod method, string path, JsonObject? body, string doing, Action<string>? log, CancellationToken ct)
    {
        var gateway = store.Find(server);
        if (gateway is null)
        {
            if (code is null || !Uri.TryCreate(server, UriKind.Absolute, out var url))
                throw new AppsRemoteException($"This machine is not paired with a gateway called or at '{server}'. Pair with --to <https address> --code <code> (a single-use code made by 'dbdatagateway server pair --max-role publisher' on the gateway; for approving, --max-role approver).");
            gateway = await PairAsync(url, code, alias, clientName, ct);
            log?.Invoke($"Paired with {gateway.Url} (instance {gateway.InstanceId}) as '{gateway.ClientName}' with the roles {string.Join(", ", gateway.Roles)}; its certificate is pinned: {gateway.Fingerprint}");
        }
        else if (code is not null)
            log?.Invoke($"This machine is already paired with {gateway.Url}; --code is not needed and was not used.");

        var ceiling = ServerRoles.TryParse(gateway.Roles, out var parsed) ? parsed : ServerRole.None;
        var role = needed switch
        {
            ServerRole.Publisher => ServerRoles.Allows(ceiling, ServerRole.Publisher) ? ServerRole.Publisher : ServerRoles.Allows(ceiling, ServerRole.Admin) ? ServerRole.Admin : ServerRole.None,
            _ => ServerRoles.Allows(ceiling, needed) ? needed : ServerRole.None,
        };
        if (role == ServerRole.None)
            throw new AppsRemoteException(needed == ServerRole.Approver
                ? $"This machine is paired with {gateway.Url} with the roles {string.Join(", ", gateway.Roles)}, and approving an app needs the approver role. Pair again with a code made by 'dbdatagateway server pair --max-role approver' on the gateway (--to <address> --code <code>)."
                : $"This machine is paired with {gateway.Url} with the roles {string.Join(", ", gateway.Roles)}, and publishing apps needs the publisher role (or admin). Pair again with a code made by 'dbdatagateway server pair --max-role publisher' on the gateway.");

        var self = await store.IdentityAsync(environment, ct);
        using var http = new HttpClient(PairingClient.Pinned(gateway.Fingerprint)) { BaseAddress = new Uri(gateway.Url + "/"), Timeout = TimeSpan.FromMinutes(5) };
        using var request = new HttpRequestMessage(method, path);
        // who acts: the paired client, by id and by the name it was paired under (the Gateway records this as the author)
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, self.Issuer().Issue(new ConsoleTokenRequest(gateway.InstanceId, "client:" + self.Id, gateway.ClientName, role)));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (HttpRequestException ex) { throw new AppsRemoteException($"{gateway.Url} could not be reached ({ex.Message})."); }
        using (response)
        {
            var answer = await response.Content.ReadAsStringAsync(ct);
            JsonNode? parsedAnswer = null;
            try { parsedAnswer = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer); }
            catch (JsonException) { /* not our JSON */ }
            if (!response.IsSuccessStatusCode)
                throw new AppsRemoteException($"The gateway refused {doing} ({(int)response.StatusCode}): {(string?)parsedAnswer?["error"] ?? (answer.Length > 0 ? answer : response.ReasonPhrase)}");
            return new RemoteAnswer(parsedAnswer as JsonObject ?? []);
        }
    }
}
