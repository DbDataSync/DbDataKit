using DbDataKit.Runner.Api;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using DbDataKit.Runner;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Server;

/// <summary>A call to a runner failed or was refused; the message is the server's own, or says what could not be done here.</summary>
public sealed class RunnerClientException(string message) : InvalidOperationException(message);

/// <summary>A runner this machine is paired with as a publisher: where it is, who it is, the certificate fingerprint every call pins and the roles the pairing allows.</summary>
public sealed record PairedRunner(string Name, string Url, string InstanceId, string Fingerprint, IReadOnlyList<string> Roles, DateTimeOffset PairedAt);

/// <summary>What <see cref="RunnerPublishClient.PublishAsync"/> did: the version the runner has now, and how many files it sent and how many it already had.</summary>
public sealed record ClientPublished(string Package, string Channel, int Version, bool Changed, string Mode, string Signature, int Uploaded, int Skipped, IReadOnlyList<string> Tasks);

/// <summary>
/// The client side of pairing for publishing (plan 28a R16): the runners this machine is paired with, in <c>publish.json</c> of the client directory, and the key it signs its tokens with, in the shared encrypted secret store
/// beside it (as the console keeps its own). The directory is <c>--client-dir</c>, <see cref="DirectoryVariable"/> or a folder under the user's local application data.
/// </summary>
public sealed class RunnerClientStore(string directory)
{
    public const string DirectoryVariable = "DBDATARUNNER_CLIENT_DIR";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string Directory { get; } = Path.GetFullPath(directory);

    private string ServersFile => Path.Combine(Directory, "publish.json");

    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(DirectoryVariable) is { Length: > 0 } named
            ? named
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatarunner", "client");

    public IReadOnlyList<PairedRunner> All()
    {
        if (!File.Exists(ServersFile)) return [];
        try { return JsonSerializer.Deserialize<List<PairedRunner>>(File.ReadAllText(ServersFile), Json) ?? []; }
        catch (JsonException) { throw new InvalidDataException($"{ServersFile} is not a list of paired runners; remove it and pair again."); }
    }

    /// <summary>The runner paired under this name or at this address.</summary>
    public PairedRunner? Find(string nameOrUrl) =>
        All().FirstOrDefault(r => string.Equals(r.Name, nameOrUrl, StringComparison.Ordinal) || string.Equals(r.Url.TrimEnd('/'), nameOrUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));

    public void Add(PairedRunner runner)
    {
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(ServersFile, JsonSerializer.Serialize(All().Where(r => r.Name != runner.Name).Append(runner).OrderBy(r => r.Name, StringComparer.Ordinal), Json), new UTF8Encoding(false));
    }

    public Task<ConsoleIdentity> IdentityAsync(Func<string, string?>? environment = null, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(Directory);
        IKeyProtector? protector = OperatingSystem.IsWindows() ? new DbDataKit.Secrets.Windows.DpapiKeyProtector(Encoding.UTF8.GetBytes("DbDataRunnerClient")) : null;
        var store = new EncryptedFileSecretStore(SecretKeys.StoreDirectory(Directory), SecretKeys.Open(Directory, new SecretKeyOptions(), protector, environment));
        return new ConsoleKeyStore(store, store).GetOrCreateAsync(ct);
    }
}

/// <summary>
/// Publishes a package to a runner over its management API as a paired server client with the role Publisher: pairs once (the single-use code from <c>dbdatarunner server pair --max-role publisher</c>), pins the
/// runner's certificate, asks <c>HEAD /api/v1/artifacts/{sha256}</c> for every file and uploads only those the runner does not have, then publishes the version in one call (<c>PUT /api/v1/definitions/integration/{name}</c>).
/// </summary>
public sealed class RunnerPublishClient(RunnerClientStore store, Func<string, string?>? environment = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Pairs with the runner at <paramref name="url"/> using its code and remembers it as <paramref name="name"/> (default: its address).</summary>
    public async Task<PairedRunner> PairAsync(Uri url, string code, string? name, CancellationToken ct = default)
    {
        var self = await store.IdentityAsync(environment, ct);
        PairedServer paired;
        try { paired = await PairingClient.PairAsync(url, code, self, ClientKind.Server, $"publisher {Environment.UserName}@{Environment.MachineName}", ct); }
        catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException) { throw new RunnerClientException(ex.Message); }
        var runner = new PairedRunner(name ?? url.Authority, paired.Url.ToString().TrimEnd('/'), paired.InstanceId, paired.CertificateFingerprint, ServerRoles.Names(paired.Ceiling), DateTimeOffset.UtcNow);
        store.Add(runner);
        return runner;
    }

    public async Task<ClientPublished> PublishAsync(string server, string? code, string manifestPath, string directory, string channel, bool stage, int? expectedVersion, Action<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(manifestPath)) throw new RunnerClientException($"The manifest '{manifestPath}' does not exist.");
        if (!Directory.Exists(directory)) throw new RunnerClientException($"The package directory '{directory}' does not exist.");
        var text = File.ReadAllText(manifestPath);
        var load = PackageManifestReader.Load(text, manifestPath);
        if (!load.Ok) throw new RunnerClientException(string.Join(Environment.NewLine, load.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => d.ToString())));
        var manifest = load.Manifest!;

        var runner = store.Find(server);
        if (runner is null)
        {
            if (code is null || !Uri.TryCreate(server, UriKind.Absolute, out var url)) throw new RunnerClientException($"This machine is not paired with a runner called or at '{server}'. Pair with --server <https address> --code <code> (made by 'dbdatarunner server pair --max-role publisher' on the runner).");
            runner = await PairAsync(url, code, null, ct);
            log?.Invoke($"Paired with {runner.Url} (instance {runner.InstanceId}); its certificate is pinned: {runner.Fingerprint}");
        }

        var self = await store.IdentityAsync(environment, ct);
        var role = ServerRoles.TryParse(runner.Roles, out var parsed) ? parsed : ServerRole.Publisher;
        using var http = new HttpClient(PairingClient.Pinned(runner.Fingerprint)) { BaseAddress = new Uri(runner.Url + "/"), Timeout = TimeSpan.FromMinutes(30) };
        var subject = "local:" + Environment.UserName;

        async Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent? content = null)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, self.Issuer().Issue(new ConsoleTokenRequest(runner.InstanceId, subject, Environment.UserName, role)));
            try { return await http.SendAsync(request, ct); }
            catch (HttpRequestException ex) { throw new RunnerClientException($"{runner.Url} could not be reached ({ex.Message})."); }
        }

        async Task<RunnerClientException> Refused(HttpResponseMessage response, string doing)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            string? error = null;
            try { error = JsonNode.Parse(body)?["error"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { /* not our JSON */ }
            return new RunnerClientException($"The runner refused {doing} ({(int)response.StatusCode}): {error ?? (body.Length > 0 ? body : response.ReasonPhrase)}");
        }

        var manifestFile = Path.GetFullPath(manifestPath);
        var files = new List<(string Path, string File, string Sha, long Size)>();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            if (Path.GetFullPath(path) == manifestFile) continue;
            using var stream = File.OpenRead(path);
            files.Add((Path.GetRelativePath(directory, path).Replace(Path.DirectorySeparatorChar, '/'), path, Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, ct)), stream.Length));
        }

        int uploaded = 0, skipped = 0;
        var done = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!done.Add(file.Sha)) { skipped++; continue; }
            using (var head = await Send(HttpMethod.Head, $"api/v1/artifacts/{file.Sha}"))
            {
                if (head.StatusCode == HttpStatusCode.OK) { skipped++; continue; }
                if (head.StatusCode != HttpStatusCode.NotFound) throw await Refused(head, $"to say whether it has {file.Path}");
            }

            await using var stream = File.OpenRead(file.File);
            using var content = new StreamContent(stream);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = file.Size;
            using var put = await Send(HttpMethod.Put, $"api/v1/artifacts/{file.Sha}", content);
            if (!put.IsSuccessStatusCode) throw await Refused(put, $"the file {file.Path}");
            uploaded++;
            log?.Invoke($"Uploaded {file.Path} ({file.Size} bytes).");
        }

        var body = new JsonObject
        {
            ["manifest"] = text,
            ["channel"] = channel,
            ["mode"] = stage ? "stage" : "serve",
            ["artifacts"] = new JsonArray([.. files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["sha256"] = f.Sha, ["size"] = f.Size })]),
        };
        if (expectedVersion is { } expected) body["expected_version"] = expected;
        using var publish = await Send(HttpMethod.Put, $"api/v1/definitions/integration/{Uri.EscapeDataString(manifest.Name)}", JsonContent.Create(body, options: Json));
        if (!publish.IsSuccessStatusCode) throw await Refused(publish, $"the package {manifest.Name}");
        var answer = JsonNode.Parse(await publish.Content.ReadAsStringAsync(ct))!;
        return new ClientPublished(manifest.Name, channel, (int)answer["version"]!, (bool)answer["changed"]!, (string)answer["mode"]!, (string)answer["signature"]!, uploaded, skipped,
            [.. answer["tasks"]!.AsArray().Select(t => (string)t!)]);
    }
}
