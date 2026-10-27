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

    /// <summary>The engines the Proxy serves that a Build connection can be: SQL Server.</summary>
    private static string? ProxyEngineOf(ConnectionConfig connection) => connection.Engine == "sqlserver" && !connection.ReadOnly ? "sqlserver" : null;

    public async Task<PublishResult> PublishAsync(string proxyName, ProjectConfig project, string connection, string dataSource, ValidatedToken person, CancellationToken ct)
    {
        if (proxies.Find(proxyName) is not { } proxy) return Fail(404, $"This server is not paired with a proxy called '{proxyName}'.");
        if (!project.Connections.TryGetValue(connection, out var config)) return Fail(404, $"The project has no connection '{connection}'.");
        if (ProxyEngineOf(config) is not { } engine) return Fail(409, $"The connection '{connection}' is {config.Engine}; a proxy serves SQL Server from here.");

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
                else if (existing.StatusCode != System.Net.HttpStatusCode.NotFound) return await Refused(existing, "reading the data source");
            }
            var text = $"schema: connection/1\nname: {dataSource}\nengine: {engine}\naccess: readonly\nconnection_string_secret: '{reference}'\n";
            using var written = await Send(HttpMethod.Put, $"/api/v1/data-sources/{Uri.EscapeDataString(dataSource)}", new { text, expected_version = version });
            if (!written.IsSuccessStatusCode) return await Refused(written, "writing the data source");
            var answer = JsonNode.Parse(await written.Content.ReadAsStringAsync(ct))!;
            return new PublishResult(200, new JsonObject { ["proxy"] = proxyName, ["data_source"] = dataSource, ["created"] = (bool?)answer["created"] ?? version is null, ["version"] = (string?)answer["version"] });
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IncompatibleServerException)
        {
            return Fail(502, $"The proxy '{proxyName}' did not answer ({ex.GetType().Name}).");
        }
    }

    private static PublishResult Fail(int status, string message) => new(status, new JsonObject { ["error"] = message });

    /// <summary>The proxy's refusal, in its own words (they name the rule), under the status it gave.</summary>
    private static async Task<PublishResult> Refused(HttpResponseMessage response, string step)
    {
        var message = "";
        try { message = (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync())?["error"] ?? ""; } catch (JsonException) { }
        return Fail((int)response.StatusCode, $"The proxy refused {step}: {(message.Length > 0 ? message : response.ReasonPhrase)}");
    }
}
