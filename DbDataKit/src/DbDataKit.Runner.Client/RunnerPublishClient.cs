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

namespace DbDataKit.Runner.Client;

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

/// <summary>What <see cref="RunnerPublishClient.PromoteAsync"/> did: the version <see cref="To"/> serves now and the state the runner answered with (<c>activated</c>, or <c>waitingforruns</c> while runs of the package in flight finish).</summary>
public sealed record ClientPromoted(string Package, string From, string To, int Version, string State, string Message);

/// <summary>A task's scheduling state as a runner reports it (plan 28c E09): who schedules it, since when and why, its position and pause, the items of it running and waiting, and the version its channel serves (null: no projection has it yet).</summary>
public sealed record ClientTaskSchedule(string Task, string Owner, string? OwnerSetBy, DateTimeOffset? OwnerSetAt, string? Reason, DateTimeOffset? HandledUpTo, bool Paused, string? PausedBy, DateTimeOffset? PausedAt, int Running, int Queued, string? Version);

/// <summary>The position a product's scheduler hands to the runner with a task: scheduled times up to <paramref name="HandledUpTo"/> are dealt with, and its pause if it had one.</summary>
public sealed record ClientSeed(DateTimeOffset HandledUpTo, string? PausedBy, DateTimeOffset? PausedAt);

/// <summary>How a task's last run on the runner ended, as the runner's status gives it (plan 28c E10).</summary>
public sealed record ClientLastRun(string Id, string Outcome, DateTimeOffset Started, DateTimeOffset? Finished, int Attempt, long? Rows);

/// <summary>A task as the runner's status lists it: who schedules it, the next time it is due, its last run, what is running or waiting there and whether it is paused (plan 28c E10).</summary>
public sealed record ClientTaskStatus(string Task, string Owner, DateTimeOffset? NextDue, ClientLastRun? LastRun, int Running, int Queued, bool Paused, string? PausedBy);

/// <summary>One scheduling decision of an observed task: <paramref name="Source"/> is <c>runner</c> (would have queued) or <c>old</c> (the product queued).</summary>
public sealed record ClientDecision(string Source, DateTimeOffset At, int Missed, bool Paused);

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

        var link = await OpenAsync(server, code, log, ct);
        using var _ = link;
        var runner = link.Runner;
        Task<HttpResponseMessage> Send(HttpMethod method, string path, HttpContent? content = null) => link.SendAsync(method, path, content, ct);
        Task<RunnerClientException> Refused(HttpResponseMessage response, string doing) => link.RefusedAsync(response, doing, ct);

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
        using var publish = await Send(HttpMethod.Put, $"api/v1/definitions/integration/{Uri.EscapeDataString(manifest.PackageName)}", JsonContent.Create(body, options: Json));
        if (!publish.IsSuccessStatusCode) throw await Refused(publish, $"the package {manifest.PackageName}");
        var answer = JsonNode.Parse(await publish.Content.ReadAsStringAsync(ct))!;
        return new ClientPublished(manifest.PackageName, channel, (int)answer["version"]!, (bool)answer["changed"]!, (string)answer["mode"]!, (string)answer["signature"]!, uploaded, skipped,
            [.. answer["tasks"]!.AsArray().Select(t => (string)t!)]);
    }

    /// <summary>The runner paired under <paramref name="server"/> (pairing with <paramref name="code"/> first when there is none), with a client that pins its certificate and signs each call.</summary>
    private async Task<Link> OpenAsync(string server, string? code, Action<string>? log, CancellationToken ct)
    {
        var runner = store.Find(server);
        if (runner is null)
        {
            if (code is null || !Uri.TryCreate(server, UriKind.Absolute, out var url)) throw new RunnerClientException($"This machine is not paired with a runner called or at '{server}'. Pair with --server <https address> --code <code> (made by 'dbdatarunner server pair --max-role publisher' on the runner).");
            runner = await PairAsync(url, code, null, ct);
            log?.Invoke($"Paired with {runner.Url} (instance {runner.InstanceId}); its certificate is pinned: {runner.Fingerprint}");
        }

        var self = await store.IdentityAsync(environment, ct);
        var role = ServerRoles.TryParse(runner.Roles, out var parsed) ? parsed : ServerRole.Publisher;
        var http = new HttpClient(PairingClient.Pinned(runner.Fingerprint)) { BaseAddress = new Uri(runner.Url + "/"), Timeout = TimeSpan.FromMinutes(30) };
        return new Link(runner, http, self, role);
    }

    private sealed class Link(PairedRunner runner, HttpClient http, ConsoleIdentity self, ServerRole role) : IDisposable
    {
        private readonly string _subject = "local:" + Environment.UserName;

        public PairedRunner Runner { get; } = runner;

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
        {
            using var request = new HttpRequestMessage(method, path) { Content = content };
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, self.Issuer().Issue(new ConsoleTokenRequest(Runner.InstanceId, _subject, Environment.UserName, role)));
            try { return await http.SendAsync(request, ct); }
            catch (HttpRequestException ex) { throw new RunnerClientException($"{Runner.Url} could not be reached ({ex.Message})."); }
        }

        public async Task<RunnerClientException> RefusedAsync(HttpResponseMessage response, string doing, CancellationToken ct)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            string? error = null;
            try { error = JsonNode.Parse(body)?["error"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { /* not our JSON */ }
            return new RunnerClientException($"The runner refused {doing} ({(int)response.StatusCode}): {error ?? (body.Length > 0 ? body : response.ReasonPhrase)}");
        }

        public void Dispose() => http.Dispose();
    }

    /// <summary>
    /// Binds the connection slots of <paramref name="package"/> in <paramref name="channel"/> (slot name to the name of what it is bound to, never a value): <c>PUT /api/v1/bindings/{channel}/integration/{name}</c>. A channel's slots are
    /// its own, so promoting a version to another channel binds that channel's connections here. <paramref name="expectedRevision"/> is the binding revision the caller saw (null does not check).
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string>> BindAsync(string server, string? code, string package, string channel, IReadOnlyDictionary<string, string> slots, int? expectedRevision = null, Action<string>? log = null, CancellationToken ct = default)
    {
        using var link = await OpenAsync(server, code, log, ct);
        var body = new JsonObject { ["slots"] = new JsonObject([.. slots.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => KeyValuePair.Create(s.Key, (JsonNode?)JsonValue.Create(s.Value)))]) };
        if (expectedRevision is { } revision) body["expected_revision"] = revision;
        using var response = await link.SendAsync(HttpMethod.Put, $"api/v1/bindings/{Uri.EscapeDataString(channel)}/integration/{Uri.EscapeDataString(package)}", JsonContent.Create(body, options: Json), ct);
        if (!response.IsSuccessStatusCode) throw await link.RefusedAsync(response, $"the bindings of {package} on {channel}", ct);
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return answer["bindings"]!.AsArray().ToDictionary(b => (string)b!["slot"]!, b => (string)b!["bound_to"]!, StringComparer.Ordinal);
    }

    /// <summary>
    /// Promotes the version <paramref name="from"/> serves to <paramref name="to"/> (plan 21, plan 30): the same stored version, not a copy, by <c>POST /api/v1/definitions/integration/{name}/switch</c>, which is an administrator's call
    /// on the runner (a pairing whose roles do not include it is refused with the runner's own words). The connection slots of <paramref name="to"/> are its own: bind them first (<see cref="BindAsync"/>), or the runner refuses naming the slot and the channel.
    /// </summary>
    public async Task<ClientPromoted> PromoteAsync(string server, string? code, string package, string from, string to, Action<string>? log = null, CancellationToken ct = default)
    {
        using var link = await OpenAsync(server, code, log, ct);
        int version;
        using (var described = await link.SendAsync(HttpMethod.Get, $"api/v1/definitions/integration/{Uri.EscapeDataString(package)}", null, ct))
        {
            if (described.StatusCode == HttpStatusCode.NotFound) throw new RunnerClientException($"The runner has no package '{package}': publish it to '{from}' first.");
            if (!described.IsSuccessStatusCode) throw await link.RefusedAsync(described, $"to describe {package}", ct);
            var channels = JsonNode.Parse(await described.Content.ReadAsStringAsync(ct))!["channels"]!.AsArray();
            var head = channels.FirstOrDefault(c => (string?)c!["channel"] == from);
            version = head?["active"]?.GetValue<int>() ?? 0;
            if (version <= 0)
                throw new RunnerClientException($"The package '{package}' has no active version in channel '{from}' to promote{(head?["staged"] is not null ? $" (version {head["staged"]} is staged there, not active yet)" : "")}.");
        }

        var body = new JsonObject { ["channel"] = to, ["version"] = version };
        using var response = await link.SendAsync(HttpMethod.Post, $"api/v1/definitions/integration/{Uri.EscapeDataString(package)}/switch", JsonContent.Create(body, options: Json), ct);
        if (!response.IsSuccessStatusCode) throw await link.RefusedAsync(response, $"to promote {package} version {version} from {from} to {to}", ct);
        var answer = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return new ClientPromoted(package, from, to, version, (string?)answer["state"] ?? "unknown", (string?)answer["message"] ?? "");
    }

    // ---- who schedules a task (plan 28c E09); these need the Operator role of the pairing, except reading

    private static string TaskPath(string package, string channel, string task) => $"api/v1/tasks/{Uri.EscapeDataString(package)}/{Uri.EscapeDataString(channel)}/{Uri.EscapeDataString(task)}";

    private static DateTimeOffset? Time(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var text) ? DateTimeOffset.Parse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal) : null;

    /// <summary>Who schedules the task on the runner, its position and pause, and whether anything of it is running or waiting there: <c>GET .../tasks/{package}/{channel}/{task}/schedule</c>.</summary>
    public async Task<ClientTaskSchedule> ScheduleOfAsync(string server, string? code, string package, string channel, string task, Action<string>? log = null, CancellationToken ct = default)
    {
        using var link = await OpenAsync(server, code, log, ct);
        using var response = await link.SendAsync(HttpMethod.Get, TaskPath(package, channel, task) + "/schedule", null, ct);
        if (!response.IsSuccessStatusCode) throw await link.RefusedAsync(response, $"to say who schedules {package}/{channel}/{task}", ct);
        var a = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return new ClientTaskSchedule((string)a["task"]!, (string)a["owner"]!, (string?)a["owner_set_by"], Time(a["owner_set_at"]), (string?)a["reason"], Time(a["handled_up_to"]), (bool)a["paused"]!, (string?)a["paused_by"], Time(a["paused_at"]),
            (int)a["running"]!, (int)a["queued"]!, (string?)a["version"]);
    }

    /// <summary>Sets who schedules the task on the runner (<c>old</c>, <c>observe</c> or <c>runner</c>) with the position the product's scheduler had reached: <c>PUT .../owner</c>, audited by the runner with the caller as the actor.</summary>
    public async Task SetOwnerAsync(string server, string? code, string package, string channel, string task, string owner, string? reason, ClientSeed? seed, Action<string>? log = null, CancellationToken ct = default)
    {
        using var link = await OpenAsync(server, code, log, ct);
        var body = new JsonObject { ["owner"] = owner };
        if (reason is not null) body["reason"] = reason;
        if (seed is not null)
        {
            body["handled_up_to"] = seed.HandledUpTo.UtcDateTime.ToString("O");
            if (seed.PausedBy is not null) body["paused_by"] = seed.PausedBy;
            if (seed.PausedAt is { } at) body["paused_at"] = at.UtcDateTime.ToString("O");
        }

        using var response = await link.SendAsync(HttpMethod.Put, TaskPath(package, channel, task) + "/owner", JsonContent.Create(body, options: Json), ct);
        if (!response.IsSuccessStatusCode) throw await link.RefusedAsync(response, $"to make {owner} the owner of {package}/{channel}/{task}", ct);
    }

    /// <summary>The decisions the runner recorded for an observed task, its own and the product's, oldest first: <c>GET .../decisions</c>.</summary>
    public async Task<IReadOnlyList<ClientDecision>> DecisionsAsync(string server, string? code, string package, string channel, string task, DateTimeOffset? since = null, Action<string>? log = null, CancellationToken ct = default)
    {
        using var link = await OpenAsync(server, code, log, ct);
        using var response = await link.SendAsync(HttpMethod.Get, TaskPath(package, channel, task) + "/decisions" + (since is { } from ? "?since=" + Uri.EscapeDataString(from.UtcDateTime.ToString("O")) : ""), null, ct);
        if (!response.IsSuccessStatusCode) throw await link.RefusedAsync(response, $"for the decisions of {package}/{channel}/{task}", ct);
        var a = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        return [.. a["decisions"]!.AsArray().Select(d => new ClientDecision((string)d!["source"]!, Time(d["at"])!.Value, (int)d["missed"]!, (bool)d["paused"]!))];
    }

    /// <summary>Tells the runner that the product's own scheduler queued a run of an observed task at <paramref name="at"/>, to be compared with what the runner would have done: <c>POST .../decisions</c>.</summary>
    public async Task ReportDecisionAsync(string server, string? code, string package, string channel, string task, DateTimeOffset at, int missed, bool paused, Action<string>? log = null, CancellationToken ct = default)
    {
        using var link = await OpenAsync(server, code, log, ct);
        var body = new JsonObject { ["at"] = at.UtcDateTime.ToString("O"), ["missed"] = missed, ["paused"] = paused };
        using var response = await link.SendAsync(HttpMethod.Post, TaskPath(package, channel, task) + "/decisions", JsonContent.Create(body, options: Json), ct);
        if (!response.IsSuccessStatusCode) throw await link.RefusedAsync(response, $"the decision for {package}/{channel}/{task}", ct);
    }

    /// <summary>Stops the task being queued at its scheduled times until it is resumed: <c>POST .../pause</c> (an operator of the pairing; the runner audits the paired client as the actor).</summary>
    public async Task PauseAsync(string server, string? code, string package, string channel, string task, Action<string>? log = null, CancellationToken ct = default)
    {
        using var link = await OpenAsync(server, code, log, ct);
        using var response = await link.SendAsync(HttpMethod.Post, TaskPath(package, channel, task) + "/pause", null, ct);
        if (!response.IsSuccessStatusCode) throw await link.RefusedAsync(response, $"to pause {package}/{channel}/{task}", ct);
    }

    /// <summary>Lets a paused task be queued again at its scheduled times: <c>POST .../resume</c>.</summary>
    public async Task ResumeAsync(string server, string? code, string package, string channel, string task, Action<string>? log = null, CancellationToken ct = default)
    {
        using var link = await OpenAsync(server, code, log, ct);
        using var response = await link.SendAsync(HttpMethod.Post, TaskPath(package, channel, task) + "/resume", null, ct);
        if (!response.IsSuccessStatusCode) throw await link.RefusedAsync(response, $"to resume {package}/{channel}/{task}", ct);
    }

    /// <summary>Queues a run of the task now, apart from its schedule: <c>POST .../run</c>. The id of the run, or null when one is queued or running already (409).</summary>
    public async Task<string?> RunNowAsync(string server, string? code, string package, string channel, string task, Action<string>? log = null, CancellationToken ct = default)
    {
        using var link = await OpenAsync(server, code, log, ct);
        using var response = await link.SendAsync(HttpMethod.Post, TaskPath(package, channel, task) + "/run", new StringContent("{}", Encoding.UTF8, "application/json"), ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return null;
        if (!response.IsSuccessStatusCode) throw await link.RefusedAsync(response, $"to run {package}/{channel}/{task}", ct);
        return (string?)JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!["run_id"];
    }

    /// <summary>The task's row of the runner's status (<c>GET /api/v1/status</c>: owner, next due, last run, running, queued, paused), or null when the runner lists no such task.</summary>
    public async Task<ClientTaskStatus?> TaskStatusAsync(string server, string? code, string package, string channel, string task, Action<string>? log = null, CancellationToken ct = default)
    {
        using var link = await OpenAsync(server, code, log, ct);
        using var response = await link.SendAsync(HttpMethod.Get, "api/v1/status", null, ct);
        if (!response.IsSuccessStatusCode) throw await link.RefusedAsync(response, "for its status", ct);
        var status = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
        foreach (var t in status["tasks"]?.AsArray() ?? [])
        {
            if ((string?)t!["package"] != package || (string?)t["channel"] != channel || (string?)t["task"] != task) continue;
            ClientLastRun? last = t["last_run"] is JsonObject r
                ? new ClientLastRun((string)r["id"]!, (string)r["outcome"]!, Time(r["started"])!.Value, Time(r["finished"]), (int?)r["attempt"] ?? 1, (long?)r["rows"])
                : null;
            return new ClientTaskStatus(task, (string?)t["owner"] ?? "runner", Time(t["next_due"]), last, (int?)t["running"] ?? 0, (int?)t["queued"] ?? 0, (bool?)t["paused"] ?? false, (string?)t["paused_by"]);
        }

        return null;
    }
}
