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

/// <summary>
/// The groups each model is published to, in <c>publications.json</c> of the server's directory (plan 20 O10b). A published model is read and written in its groups and nowhere else: this server's own data API refuses it, so one
/// definition is ever enforced on its tables. Unpublishing it from every group gives that data path back.
/// </summary>
public sealed class StorePublications(string directory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private string File => Path.Combine(directory, "publications.json");

    private Dictionary<string, List<string>> Read()
    {
        if (!System.IO.File.Exists(File)) return new(StringComparer.Ordinal);
        try { return JsonSerializer.Deserialize<Dictionary<string, List<string>>>(System.IO.File.ReadAllText(File), Json) ?? new(StringComparer.Ordinal); }
        catch (JsonException) { throw new InvalidDataException($"{File} is not a list of publications; it says which groups serve each model, so restore it rather than remove it."); }
    }

    private void Write(Dictionary<string, List<string>> all)
    {
        Directory.CreateDirectory(directory);
        var temporary = File + ".tmp";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(new SortedDictionary<string, List<string>>(all.Where(p => p.Value.Count > 0).ToDictionary(), StringComparer.Ordinal), Json), new UTF8Encoding(false));
        System.IO.File.Move(temporary, File, overwrite: true);
    }

    /// <summary>The groups the model is published to (by the name this server knows them by); empty when it is not published.</summary>
    public IReadOnlyList<string> Of(string model) => Read().TryGetValue(model, out var groups) ? groups : [];

    public void Add(string model, string proxy)
    {
        var all = Read();
        var groups = all.TryGetValue(model, out var held) ? held : all[model] = [];
        if (!groups.Contains(proxy)) groups.Add(proxy);
        groups.Sort(StringComparer.Ordinal);
        Write(all);
    }

    public void Remove(string model, string proxy)
    {
        var all = Read();
        if (all.TryGetValue(model, out var groups)) groups.Remove(proxy);
        Write(all);
    }
}

/// <summary>What publishing a model came to: the versions the group answered, or why not.</summary>
public sealed record StorePublishResult(bool Ok, string Message, JsonObject? Detail = null);

/// <summary>
/// Publishes a model of this server to the serving groups it is paired with as a publishing client (plan 20 O10, O10b), so every instance of a group serves the model and enforces its rules, with this server stopped or not.
/// Publishing is the one act that makes the model being worked on available: its tables are brought to it (the migration step), this server's copy is pinned to it, and that version, never a model still being worked on,
/// is sent to the groups, each in three calls as a Build server publishes a semantic package: the write login of the model's connection as a secret of the group, a data source that refers to it, the model naming that data source.
/// </summary>
public sealed class StorePublisher(StoreProxies groups, ConsoleKeyStore identity, string directory, StoreConnections connections)
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
        groups.Add(proxy);
        return proxy;
    }

    /// <summary>What a publish would do, or did: the statements its tables needed, the version released, and each group's answer.</summary>
    public sealed record Release(string Version, IReadOnlyList<DbDataKit.DataModel.Storage.StorageStatement> Statements, bool Created);

    /// <summary>
    /// Publishes the model (plan 20 O10b, owner decision 2026-11-04): the one act that makes the model being worked on available. Its tables are brought to it first (created if there are none; otherwise only added to,
    /// and refused when that is not enough), the server's own copy is then pinned to that version, and that version is sent to <paramref name="proxies"/>, or else to every group it is published to. With
    /// <paramref name="dryRun"/> nothing changes: the answer says what would. A group that cannot be reached keeps the version it had, and the answer says so.
    /// </summary>
    public async Task<StorePublishResult> PublishAsync(string modelName, IReadOnlyList<string> proxies, string personId, string? personName, bool dryRun, CancellationToken ct)
    {
        if (!StoreModels.IsValidName(modelName) || !File.Exists(StoreModels.FileOf(directory, modelName))) return new(false, $"There is no model '{modelName}'.");
        var publications = new StorePublications(directory);
        var targets = proxies.Count > 0 ? proxies : publications.Of(modelName);
        if (targets.Count == 0) return new(false, $"The model '{modelName}' is not published to a group yet; name one with --proxy (dbdatastore gateway list).");
        foreach (var target in targets)
            if (groups.Find(target) is null) return new(false, $"This server is not paired with a proxy called '{target}' (dbdatastore gateway add).");

        var text = (await File.ReadAllTextAsync(StoreModels.FileOf(directory, modelName), ct)).ReplaceLineEndings("\n");
        var check = StoreModels.Check(modelName, text);
        if (!check.Ok) return new(false, $"{modelName}.yaml is not a valid model, so nothing was published: {string.Join(" ", check.Diagnostics.Select(d => d.ToString()))}");
        var model = check.Model!;
        // what is published (and pinned as this server's copy) is the current format of the file; the file itself stays as the person wrote it (plan 33 6d, rule 5)
        text = DbDataKit.DataModel.DataModelLoader.WithCurrentVersion(text);
        if (model.Instances != DbDataKit.DataModel.InstanceMode.None) return new(false, $"The model '{modelName}' keeps instances, and a group serves only a model's own copy so far; it cannot be published yet.");

        // the tables first: the version published is always the one they have
        Release release;
        try { release = await ReleaseAsync(model, text, dryRun, ct); }
        catch (StoreConnectionException ex) { return new(false, ex.Message); }
        catch (DbDataKit.DataModel.Storage.StorageException ex) { return new(false, $"The tables of '{modelName}' cannot be brought to this version by adding to them, so nothing was published: {ex.Message}"); }

        var detail = new JsonObject
        {
            ["model"] = modelName, ["released_version"] = release.Version, ["created_tables"] = release.Created, ["dry_run"] = dryRun,
            ["statements"] = new JsonArray([.. release.Statements.Select(st => (JsonNode)new JsonObject { ["object"] = st.Object, ["description"] = st.Description, ["text"] = st.Text })]),
            ["groups"] = new JsonArray(),
        };
        if (dryRun)
        {
            foreach (var target in targets) detail["groups"]!.AsArray().Add(new JsonObject { ["proxy"] = target, ["sent"] = false });
            return new(true, $"Publishing {modelName} would {(release.Statements.Count == 0 ? "change no table" : $"run {release.Statements.Count} statement(s) on its tables")} and send it to {string.Join(", ", targets)}.", detail);
        }

        var failures = new List<string>();
        foreach (var target in targets)
        {
            var sent = await SendAsync(modelName, model.Connection, text, groups.Find(target)!, personId, personName, ct);
            detail["groups"]!.AsArray().Add(new JsonObject { ["proxy"] = target, ["sent"] = sent.Ok, ["version"] = (int?)sent.Detail?["version"], ["unchanged"] = (bool?)sent.Detail?["unchanged"], ["error"] = sent.Ok ? null : sent.Message });
            if (sent.Ok) publications.Add(modelName, target);
            else failures.Add($"{target}: {sent.Message}");
        }
        return failures.Count == 0
            ? new(true, $"Published {modelName} to {string.Join(", ", targets)}.", detail)
            : new(false, $"{modelName} is released here (its tables and this server's copy are at the new version), but {string.Join("; ", failures)}. A group that did not take it keeps the version it had; publish again to send it.", detail);
    }

    /// <summary>Brings the model's own tables to <paramref name="text"/> (the migration step of publishing) and pins the server's copy to it.</summary>
    private async Task<Release> ReleaseAsync(DbDataKit.DataModel.DataModel model, string text, bool dryRun, CancellationToken ct)
    {
        var engine = connections.EngineOf(model.Connection);
        var copy = StoreInstances.Find(directory, model.Name, StoreInstances.Default);
        var version = StoreModels.VersionOf(text);
        if (copy is null)
        {
            var plan = DbDataKit.DataModel.Storage.ModelStorage.Plan(model, engine, model.Name);
            if (!dryRun)
            {
                await using var connection = await connections.OpenAsync(model.Connection, ct);
                await DbDataKit.DataModel.Storage.ModelStorage.CreateAsync(connection, plan, ct);
                StoreInstances.Record(directory, model.Name, StoreInstances.Default, DbDataKit.DataModel.Storage.ModelStorage.DefinitionHash(model, engine), StoreModels.Snapshot(directory, model.Name, text), DateTimeOffset.UtcNow);
            }
            return new Release(version, plan.Statements, Created: true);
        }
        await using (var connection = await connections.OpenAsync(model.Connection, ct))
        {
            var plan = DbDataKit.DataModel.Storage.ModelStorage.PlanExpand(model, engine, model.Name, await DbDataKit.DataModel.Storage.ModelStorage.ReadLiveAsync(connection, model.Name, ct));
            if (plan.Blocked.Count > 0) throw new DbDataKit.DataModel.Storage.StorageException(string.Join(" ", plan.Blocked));
            if (!dryRun)
            {
                if (plan.Statements.Count > 0) await DbDataKit.DataModel.Storage.ModelStorage.ApplyExpandAsync(connection, plan, ct);
                StoreInstances.Record(directory, model.Name, StoreInstances.Default, DbDataKit.DataModel.Storage.ModelStorage.DefinitionHash(model, engine), StoreModels.Snapshot(directory, model.Name, text), DateTimeOffset.UtcNow);
            }
            return new Release(version, plan.Statements, Created: false);
        }
    }

    /// <summary>Takes the model off a group (it stops serving it) and, when no group is left, gives this server's own data path back.</summary>
    public async Task<StorePublishResult> UnpublishAsync(string modelName, string proxyName, string personId, string? personName, CancellationToken ct)
    {
        if (groups.Find(proxyName) is not { } proxy) return new(false, $"This server is not paired with a proxy called '{proxyName}'.");
        var publications = new StorePublications(directory);
        if (!publications.Of(modelName).Contains(proxyName)) return new(false, $"The model '{modelName}' is not published to {proxyName}.");
        var (ok, _, status) = await CallAsync(proxy, personId, personName, HttpMethod.Delete, $"/api/v1/entity-models/{Uri.EscapeDataString(modelName)}", null, ct);
        // a group that no longer has it (removed there by its administrator) is as good as taken off
        if (!ok && !status.StartsWith("404", StringComparison.Ordinal)) return new(false, $"{proxyName} did not take '{modelName}' off: {status}.");
        publications.Remove(modelName, proxyName);
        var left = publications.Of(modelName);
        return new(true, left.Count == 0 ? $"{modelName} is no longer published; this server reads and writes it again." : $"{modelName} is no longer published to {proxyName}; it still is to {string.Join(", ", left)}.");
    }

    private async Task<(bool Ok, JsonNode? Body, string Status)> CallAsync(PairedProxy proxy, string personId, string? personName, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        var self = await identity.GetOrCreateAsync(ct);
        using var http = new HttpClient(PairingClient.Pinned(proxy.Fingerprint)) { BaseAddress = new Uri(proxy.Url), Timeout = TimeSpan.FromSeconds(60) };
        using var request = new HttpRequestMessage(method, path);
        request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, self.Issuer().Issue(new ConsoleTokenRequest(proxy.InstanceId, personId, personName, ServerRole.Publisher)));
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        try
        {
            using var response = await http.SendAsync(request, ct);
            var answer = await response.Content.ReadAsStringAsync(ct);
            JsonNode? parsed = null;
            try { parsed = string.IsNullOrWhiteSpace(answer) ? null : JsonNode.Parse(answer); } catch (JsonException) { }
            return (response.IsSuccessStatusCode, parsed, $"{(int)response.StatusCode} {(string?)parsed?["error"]}".Trim());
        }
        catch (HttpRequestException ex) { return (false, null, $"it could not be reached ({ex.HttpRequestError})"); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { return (false, null, "it did not answer in time"); }
    }

    /// <summary>Sends the released version to one group: the write login of its connection as the group's secret, a data source that refers to it, then the model, each on the version the group has.</summary>
    private async Task<StorePublishResult> SendAsync(string modelName, string connection, string text, PairedProxy proxy, string personId, string? personName, CancellationToken ct)
    {
        (string Engine, string ConnectionString) login;
        try { login = await connections.WriteConnectionStringAsync(connection, ct); }
        catch (StoreConnectionException ex) { return new(false, ex.Message); }
        Task<(bool Ok, JsonNode? Body, string Status)> Send(HttpMethod method, string path, object? body = null) => CallAsync(proxy, personId, personName, method, path, body, ct);

        var secret = await Send(HttpMethod.Put, $"/api/v1/secrets/{Uri.EscapeDataString(connection + "-write")}", new { value = login.ConnectionString });
        if (!secret.Ok || (string?)secret.Body?["reference"] is not { } reference) return new(false, $"the group did not keep the login of '{connection}' ({secret.Status})");
        var source = $"schema: {DbDataKit.Connections.ConnectionDefinition.CurrentSchemaId}\nname: {connection}\nengine: {login.Engine}\nconnection_string_secret: '{reference}'\n";
        var existing = await Send(HttpMethod.Get, $"/api/v1/data-sources/{Uri.EscapeDataString(connection)}");
        var written = await Send(HttpMethod.Put, $"/api/v1/data-sources/{Uri.EscapeDataString(connection)}", new { text = source, expected_version = existing.Ok ? (string?)existing.Body?["version"] : null });
        if (!written.Ok) return new(false, $"the group did not take the data source '{connection}' ({written.Status})", written.Body as JsonObject);
        var held = await Send(HttpMethod.Get, $"/api/v1/entity-models/{Uri.EscapeDataString(modelName)}");
        var published = await Send(HttpMethod.Put, $"/api/v1/entity-models/{Uri.EscapeDataString(modelName)}",
            new Dictionary<string, object?> { ["model"] = text, ["data_source"] = connection, ["expected_version"] = held.Ok ? (int?)held.Body?["current_version"] : null });
        if (!published.Ok) return new(false, $"the group did not take the model ({published.Status})", published.Body as JsonObject);
        return new(true, "sent", published.Body as JsonObject);
    }
}
