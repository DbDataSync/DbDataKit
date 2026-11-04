using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.Web.Trust;

namespace DbDataStore.Server;

/// <summary>A serving group (or a single Proxy) this server publishes its models to: its name here, its address, its instance id and the certificate fingerprint pinned when this server paired with it.</summary>
public sealed record PairedProxy(string Name, string Url, string InstanceId, string Fingerprint, DateTimeOffset PairedAt);

/// <summary>The serving groups this server is paired with, in <c>proxies.json</c> of its directory.</summary>
public sealed class StoreProxies(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string File => Path.Combine(directory, "proxies.json");

    public IReadOnlyList<PairedProxy> All()
    {
        if (!System.IO.File.Exists(File)) return [];
        try { return JsonSerializer.Deserialize<List<PairedProxy>>(System.IO.File.ReadAllText(File), Json) ?? []; }
        catch (JsonException) { throw new InvalidDataException($"{File} is not a list of paired proxies; remove it and pair again."); }
    }

    public PairedProxy? Find(string name) => All().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public void Add(PairedProxy proxy)
    {
        Directory.CreateDirectory(directory);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(All().Where(p => p.Name != proxy.Name).Append(proxy).OrderBy(p => p.Name, StringComparer.Ordinal), Json), new UTF8Encoding(false));
    }

    public bool Remove(string name)
    {
        var rest = All().Where(p => p.Name != name).ToList();
        if (rest.Count == All().Count) return false;
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(rest, Json), new UTF8Encoding(false));
        return true;
    }
}

/// <summary>What publishing a model came to: the versions the group answered, or why not.</summary>
public sealed record StorePublishResult(bool Ok, string Message, JsonObject? Detail = null);

/// <summary>
/// Publishes a model of this server to a serving group it is paired with as a publishing client (plan 20 O10), so every instance of the group serves the model and enforces its rules, with this server stopped or not.
/// Three calls, as a Build server publishes a semantic package: the write login of the model's connection is kept as a secret of the group (<c>PUT /api/v1/secrets</c>), a data source refers to it, and the model names
/// that data source (<c>PUT /api/v1/entity-models</c>). What is published is the model its tables have; changing the structure stays this server's (a plan, an approval, then the tables, then publishing again).
/// </summary>
public sealed class StorePublisher(StoreProxies proxies, ConsoleKeyStore identity, string directory, StoreConnections connections)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The encrypted store in this server's directory, which holds the key it signs with as a client.</summary>
    public static EncryptedFileSecretStore OpenSecrets(string directory)
    {
        IKeyProtector? protector = OperatingSystem.IsWindows() ? new DbDataKit.Secrets.Windows.DpapiKeyProtector(Encoding.UTF8.GetBytes("DbDataStore")) : null;
        return new EncryptedFileSecretStore(SecretKeys.StoreDirectory(directory), SecretKeys.Open(directory, new SecretKeyOptions(), protector));
    }

    public static StorePublisher For(string directory)
    {
        var secrets = OpenSecrets(directory);
        return new StorePublisher(new StoreProxies(directory), new ConsoleKeyStore(secrets, secrets), directory, new StoreConnections(directory));
    }

    /// <summary>Pairs this server with the group at <paramref name="url"/> as a publishing client, with the code its administrator made, and remembers it as <paramref name="name"/>.</summary>
    public async Task<PairedProxy> PairAsync(string name, Uri url, string code, CancellationToken ct)
    {
        var self = await identity.GetOrCreateAsync(ct);
        var paired = await PairingClient.PairAsync(url, code, self, ClientKind.Server, $"DbDataStore server {StoreServer.InstanceId(Path.Combine(directory, "server.yaml"))}", ct);
        var proxy = new PairedProxy(name, paired.Url.ToString().TrimEnd('/'), paired.InstanceId, paired.CertificateFingerprint, DateTimeOffset.UtcNow);
        proxies.Add(proxy);
        return proxy;
    }

    public async Task<StorePublishResult> PublishAsync(string modelName, string proxyName, string personId, string? personName, CancellationToken ct)
    {
        if (proxies.Find(proxyName) is not { } proxy) return new(false, $"This server is not paired with a proxy called '{proxyName}' (dbdatastore proxy add).");
        if (!StoreModels.IsValidName(modelName) || !File.Exists(StoreModels.FileOf(directory, modelName))) return new(false, $"There is no model '{modelName}'.");
        var text = (await File.ReadAllTextAsync(StoreModels.FileOf(directory, modelName), ct)).ReplaceLineEndings("\n");
        var check = StoreModels.Check(modelName, text);
        if (!check.Ok) return new(false, $"{modelName}.yaml is not a valid model, so nothing was published.");
        var connection = check.Model!.Connection;
        (string Engine, string ConnectionString) login;
        try { login = await connections.WriteConnectionStringAsync(connection, ct); }
        catch (StoreConnectionException ex) { return new(false, ex.Message); }

        var self = await identity.GetOrCreateAsync(ct);
        using var http = new HttpClient(PairingClient.Pinned(proxy.Fingerprint)) { BaseAddress = new Uri(proxy.Url), Timeout = TimeSpan.FromSeconds(60) };
        async Task<(bool Ok, JsonNode? Body, string Status)> Send(HttpMethod method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, self.Issuer().Issue(new ConsoleTokenRequest(proxy.InstanceId, personId, personName, ServerRole.Publisher)));
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            using var response = await http.SendAsync(request, ct);
            var answer = await response.Content.ReadAsStringAsync(ct);
            JsonNode? parsed = null;
            try { parsed = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer); } catch (JsonException) { }
            return (response.IsSuccessStatusCode, parsed, $"{(int)response.StatusCode} {(string?)parsed?["error"]}".Trim());
        }

        // 1. the write login, kept by the group (encrypted with its key) under this client's own name; the answer is the reference the data source writes
        var secret = await Send(HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(connection + "-write")}", new { value = login.ConnectionString });
        if (!secret.Ok || (string?)secret.Body?["reference"] is not { } reference) return new(false, $"The group did not keep the login of '{connection}': {secret.Status}.");

        // 2. the data source the model's tables are on, on the version the group has of it (none the first time)
        var source = $"schema: connection/1\nname: {connection}\nengine: {login.Engine}\nconnection_string_secret: '{reference}'\n";
        var existing = await Send(HttpMethod.Get, $"/api/v1/data-sources/{Uri.EscapeDataString(connection)}");
        var written = await Send(HttpMethod.Put, $"/api/v1/data-sources/{Uri.EscapeDataString(connection)}", new { text = source, expected_version = existing.Ok ? (string?)existing.Body?["version"] : null });
        if (!written.Ok) return new(false, $"The group did not take the data source '{connection}': {written.Status}.", written.Body as JsonObject);

        // 3. the model, on the version the group serves of it
        var held = await Send(HttpMethod.Get, $"/api/v1/entity-models/{Uri.EscapeDataString(modelName)}");
        var published = await Send(HttpMethod.Put, $"/api/v1/entity-models/{Uri.EscapeDataString(modelName)}",
            new Dictionary<string, object?> { ["model"] = text, ["data_source"] = connection, ["expected_version"] = held.Ok ? (int?)held.Body?["current_version"] : null });
        if (!published.Ok) return new(false, $"The group did not take the model '{modelName}': {published.Status}.", published.Body as JsonObject);
        return new(true, $"Published {modelName} to {proxyName} as version {(int?)published.Body?["version"]}{((bool?)published.Body?["unchanged"] == true ? " (unchanged)" : "")}.", published.Body as JsonObject);
    }
}
