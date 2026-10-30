using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Models;
using DbDataKit.Connections;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.Web.Trust;

namespace DbDataBuild.Cli.Server;

/// <summary>A Proxy this Build server is paired with (plan 13, T10): where it is, who it is and the certificate fingerprint every later call pins.</summary>
internal sealed record PairedProxy(string Name, string Url, string InstanceId, string Fingerprint, DateTimeOffset PairedAt);

/// <summary>The Proxies this server is paired with, in <c>build/proxies.json</c> of its directory.</summary>
internal sealed class ProxiesFile(string serverDirectory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string Path => System.IO.Path.Combine(serverDirectory, "build", "proxies.json");

    public IReadOnlyList<PairedProxy> All()
    {
        if (!File.Exists(Path)) return [];
        try { return JsonSerializer.Deserialize<List<PairedProxy>>(File.ReadAllText(Path), Json) ?? []; }
        catch (JsonException) { throw new InvalidDataException($"{Path} is not a list of paired proxies; remove it and pair again."); }
    }

    public PairedProxy? Find(string name) => All().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.Ordinal));

    public void Add(PairedProxy proxy)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.WriteAllText(Path, JsonSerializer.Serialize(All().Where(p => p.Name != proxy.Name).Append(proxy).OrderBy(p => p.Name, StringComparer.Ordinal), Json), new UTF8Encoding(false));
    }

    public bool Remove(string name)
    {
        var rest = All().Where(p => p.Name != name).ToList();
        if (rest.Count == All().Count) return false;
        File.WriteAllText(Path, JsonSerializer.Serialize(rest, Json), new UTF8Encoding(false));
        return true;
    }
}

/// <summary>What publishing a data source to a Proxy came to, or why not.</summary>
internal sealed record PublishResult(int Status, JsonObject Body);

/// <summary>
/// Publishes a connection of a project to a paired Proxy as a read-only data source (plan 13, T10, "Server-to-server clients"). Only the <b>read</b> login goes: the secret <c>dbdata:build:connection:&lt;connection&gt;:read</c> is sent once, over the
/// pinned channel, to the Proxy's own store, and the data source file refers to it; the write login is never read here. The call carries a token that names the person who asked (their Build user) and the role Publisher, so the Proxy applies its owner
/// rules to it: it may create data sources and change or remove only the ones this server made.
/// </summary>
internal sealed class ProxyPublisher(ProxiesFile proxies, ConsoleKeyStore identity, EncryptedFileSecretStore secrets)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The engines the Proxy serves (its providers: SQL Server, ODBC and DuckDB) and what a Build connection's driver is called there: the same word. The read login of such a connection is a connection string, which the Proxy takes whole as
    /// <c>connection_string_secret</c> whatever the engine; <c>access: readonly</c> is what is written, because only the read login goes. A driver the Proxy has no provider for (postgres, fabric, flightsql) is not published.
    /// </summary>
    private static string? ProxyEngineOf(ConnectionConfig connection) => connection.Kind.Driver is "sqlserver" or "odbc" or "duckdb" ? connection.Kind.Driver : null;

    /// <summary>
    /// Pairs this server with the Proxy at <paramref name="url"/> as a server client, with the code its administrator made (or a console asked it for, T11), and remembers it as <paramref name="name"/>. The key this server signs with is its own, kept in its secret store.
    /// </summary>
    public async Task<(PairedProxy Proxy, ServerRole Ceiling)> PairAsync(string name, Uri url, string code, string clientName, CancellationToken ct)
    {
        var self = await identity.GetOrCreateAsync(ct);
        var paired = await PairingClient.PairAsync(url, code, self, ClientKind.Server, clientName, ct);
        var proxy = new PairedProxy(name, paired.Url.ToString().TrimEnd('/'), paired.InstanceId, paired.CertificateFingerprint, DateTimeOffset.UtcNow);
        proxies.Add(proxy);
        return (proxy, paired.Ceiling);
    }

    public async Task<PublishResult> PublishAsync(string proxyName, ProjectConfig project, string connection, string dataSource, ValidatedToken person, CancellationToken ct)
    {
        if (proxies.Find(proxyName) is not { } proxy) return Fail(404, $"This server is not paired with a proxy called '{proxyName}'.");
        if (!project.Connections.TryGetValue(connection, out var config)) return Fail(404, $"The project has no connection '{connection}'.");
        if (ProxyEngineOf(config) is not { } engine) return Fail(409, $"The connection '{connection}' is {config.Kind.Describe()}; a proxy serves sqlserver, odbc and duckdb connections, not {config.Kind.Driver}.");

        using var login = await secrets.TryResolveAsync(ServerSecrets.NameOf(connection, LoginRole.Read).ToRef(), ct);
        if (login is null || login.IsEmpty) return Fail(409, $"No read login is stored for '{connection}' (dbdatabuild secret set {ServerSecrets.NameOf(connection, LoginRole.Read)}); there is nothing to publish.");

        var self = await identity.GetOrCreateAsync(ct);
        using var http = new HttpClient(PairingClient.Pinned(proxy.Fingerprint)) { BaseAddress = new Uri(proxy.Url), Timeout = TimeSpan.FromSeconds(60) };
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, self.Issuer().Issue(new ConsoleTokenRequest(proxy.InstanceId, person.SubjectId, person.SubjectName, ServerRole.Publisher)));
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            return await http.SendAsync(request, ct);
        }

        try
        {
            // 1. the login, once, into the proxy's own store
            using var stored = await Send(HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(dataSource + "-read")}", new { value = login.RevealString() });
            if (!stored.IsSuccessStatusCode) return await Refused(stored, "storing the login");
            var reference = (string?)JsonNode.Parse(await stored.Content.ReadAsStringAsync(ct))?["reference"] ?? "";

            // 2. the data source: new, or the one this server made before (its version is read first)
            string? version = null;
            using (var existing = await Send(HttpMethod.Get, $"/api/v1/data-sources/{Uri.EscapeDataString(dataSource)}"))
            {
                if (existing.IsSuccessStatusCode) version = (string?)JsonNode.Parse(await existing.Content.ReadAsStringAsync(ct))?["version"];
                else if (existing.StatusCode != System.Net.HttpStatusCode.NotFound) return await Refused(existing, "reading the data source", AdoptHint(self.Id, dataSource));
            }
            var text = $"schema: connection/1\nname: {dataSource}\nengine: {engine}\naccess: readonly\nconnection_string_secret: '{reference}'\n";
            using var written = await Send(HttpMethod.Put, $"/api/v1/data-sources/{Uri.EscapeDataString(dataSource)}", new { text, expected_version = version });
            if (!written.IsSuccessStatusCode) return await Refused(written, "writing the data source", AdoptHint(self.Id, dataSource));
            var answer = JsonNode.Parse(await written.Content.ReadAsStringAsync(ct))!;
            return new PublishResult(200, new JsonObject { ["proxy"] = proxyName, ["data_source"] = dataSource, ["created"] = (bool?)answer["created"] ?? version is null, ["version"] = (string?)answer["version"] });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IncompatibleServerException)
        {
            return Fail(502, $"The proxy '{proxyName}' did not answer ({ex.GetType().Name}).");
        }
    }

    /// <summary>
    /// Publishes a semantic model of a project to a paired Proxy (plan 19, J12): the connections it reads go first, each as a read-only data source of the same name (only the read login leaves, as for <see cref="PublishAsync"/>), then the
    /// model itself, the catalog with its generated physical layer and the data source each of its sources reads. The Proxy checks the catalog, that its data sources answer and that this client made them, writes a new version and switches to it
    /// only when all of that holds; a refusal leaves the version that was serving. The model is published over the version this server last published (read first), so two servers cannot silently replace each other's.
    /// </summary>
    public async Task<PublishResult> PublishSemanticAsync(string proxyName, DbDataBuild.Semantic.SemanticModelFile model, ProjectConfig project, ValidatedToken person, CancellationToken ct)
    {
        if (proxies.Find(proxyName) is not { } proxy) return Fail(404, $"This server is not paired with a proxy called '{proxyName}'.");
        if (model.Compilation is not { } compiled) return Fail(409, $"The semantic model '{model.Name}' ({model.File}) does not compile; fix it first (project compile shows why).");

        var published = new JsonArray();
        foreach (var connection in model.Connections.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var result = await PublishAsync(proxyName, project, connection, connection, person, ct);
            if (result.Status != 200) return new PublishResult(result.Status, new JsonObject { ["error"] = $"Publishing the connection '{connection}' failed, so the model was not published: {(string?)result.Body["error"]}" });
            published.Add((string?)result.Body["data_source"]);
        }

        var self = await identity.GetOrCreateAsync(ct);
        using var http = new HttpClient(PairingClient.Pinned(proxy.Fingerprint)) { BaseAddress = new Uri(proxy.Url), Timeout = TimeSpan.FromSeconds(60) };
        async Task<HttpResponseMessage> Send(HttpMethod method, string path, object? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, self.Issuer().Issue(new ConsoleTokenRequest(proxy.InstanceId, person.SubjectId, person.SubjectName, ServerRole.Publisher)));
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            return await http.SendAsync(request, ct);
        }
        try
        {
            var path = $"/api/v1/semantic-models/{Uri.EscapeDataString(model.Name)}";
            int? current = null;
            using (var existing = await Send(HttpMethod.Get, path))
            {
                if (existing.IsSuccessStatusCode) current = (int?)JsonNode.Parse(await existing.Content.ReadAsStringAsync(ct))?["current_version"];
                else if (existing.StatusCode != System.Net.HttpStatusCode.NotFound) return await Refused(existing, "reading the model");
            }
            var sources = model.Connections.ToDictionary(c => c.Key, c => c.Value, StringComparer.Ordinal);
            using var written = await Send(HttpMethod.Put, path, new { catalog = compiled.Json.ToJsonString(), sources, expected_version = current });
            if (!written.IsSuccessStatusCode) return await Refused(written, "publishing the model");
            var answer = JsonNode.Parse(await written.Content.ReadAsStringAsync(ct))!;
            return new PublishResult(200, new JsonObject { ["proxy"] = proxyName, ["model"] = model.Name, ["version"] = (int?)answer["version"], ["created"] = (bool?)answer["created"] ?? current is null, ["data_sources"] = published });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IncompatibleServerException)
        {
            return Fail(502, $"The proxy '{proxyName}' did not answer ({ex.GetType().Name}).");
        }
    }

    private static PublishResult Fail(int status, string message) => new(status, new JsonObject { ["error"] = message });

    /// <summary>
    /// What a person can do about a data source this server may not touch (plan 13, T12): taking it over is an administrator's act on the proxy (<c>POST /api/v1/data-sources/{name}/adopt</c>, never a server client's, so Build cannot do it for them), which needs
    /// this server's client id on the proxy. The id and the call are returned with the refusal.
    /// </summary>
    private static JsonObject AdoptHint(string clientId, string dataSource) => new() { ["client"] = clientId, ["data_source"] = dataSource, ["call"] = $"POST /api/v1/data-sources/{dataSource}/adopt", ["body"] = new JsonObject { ["client"] = clientId } };

    /// <summary>The proxy's refusal, in its own words (they name the rule), under the status it gave.</summary>
    private static async Task<PublishResult> Refused(HttpResponseMessage response, string step, JsonObject? adopt = null)
    {
        var message = "";
        try { message = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["error"] ?? ""; } catch (JsonException) { }
        var refused = Fail((int)response.StatusCode, $"The proxy refused {step}: {(message.Length > 0 ? message : response.ReasonPhrase)}");
        // a refusal on owner grounds (403) can be settled by a person taking the object over; say how
        if (adopt is not null && response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            refused.Body["error"] = $"{(string?)refused.Body["error"]} An administrator of the proxy can hand this data source to this server (adopt it, client {(string?)adopt["client"]}); a server cannot adopt for itself.";
            refused.Body["adopt"] = adopt;
        }
        return refused;
    }
}
