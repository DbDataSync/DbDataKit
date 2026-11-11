using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.DataModel;
using DbDataKit.Runner;
using DbDataKit.Runner.Client;
using DbDataKit.Web.Trust;

namespace DbDataKit.Authoring.Server;

/// <summary>What a person says when they publish what a kind made (plan 33 SC20d, SC21b, SC22b): for a Runner the <see cref="Channel"/> (and whether it serves at once, <see cref="Serve"/>, or is staged, the default; <see cref="ExpectedVersion"/> and the connections <see cref="Bind"/> fills, slot to connection name); for a Gateway the <see cref="DataSource"/> its tables are on, the models' own storage connection when none is named, and <see cref="Adopt"/> (plan 33 SC21f) the data sources made by hand that the person confirmed the models may go on (the Gateway records the adoption).</summary>
public sealed record ArtifactPublishOptions(string? Channel = null, bool Serve = false, int? ExpectedVersion = null, IReadOnlyDictionary<string, string>? Bind = null, string? DataSource = null, IReadOnlyList<string>? Adopt = null);

public sealed partial class WorkspacePublishing
{
    /// <summary>
    /// Publishes what a kind made (<paramref name="artifactFolder"/>, a build's <c>artifact/</c>) to the target <paramref name="targetName"/> by the route its product has in <paramref name="kind"/>: a <c>package</c> to a Runner, <c>models</c> to a Gateway.
    /// The key is the target's publisher key, read here and never returned; the publisher a Runner records is <paramref name="actor"/>, the person who published. <paramref name="step"/> is told each step as it happens.
    /// </summary>
    public async Task<JsonObject> PublishArtifactAsync(IProjectKind kind, string targetName, string artifactFolder, ArtifactPublishOptions options, string actor, Action<string> step, CancellationToken ct = default)
    {
        var (target, key, role) = ResolveTarget(targetName, "what this project makes");
        var route = RouteFor(kind, target);
        step("Read the publisher key '" + targetName + "' from the secret store.");
        var identity = await KeyAsync(targetName, key.SecretRef, ct);
        return route.What switch
        {
            "package" => await PublishPackageAsync(target, identity, artifactFolder, options, actor, step, ct),
            "model" => await PublishModelsAsync(target, identity, role, artifactFolder, options, step, ct),
            "semantic-model" => await PublishSemanticModelsAsync(target, identity, role, Path.Combine(artifactFolder, "gateway"), options, step, ct),
            _ => throw new WorkspacePublishException($"A {kind.Name} project publishes '{route.What}' to a {route.Product}, which this server does not know how to send.", 409),
        };
    }

    private static async Task<JsonObject> PublishPackageAsync(PublishTarget target, ConsoleIdentity identity, string artifactFolder, ArtifactPublishOptions options, string actor, Action<string> step, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Channel)) throw new WorkspacePublishException("Name the channel of the Runner the package goes to (\"channel\").", 400);
        var manifestPath = Path.Combine(artifactFolder, "package.yaml");
        if (!File.Exists(manifestPath)) throw new WorkspacePublishException("This build has no package to publish (package.yaml is not in it).", 409);
        var load = PackageManifestReader.Load(File.ReadAllText(manifestPath), "package.yaml");
        if (!load.Ok) throw new WorkspacePublishException("The package of this build is not valid: " + string.Join(" ", load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.ToString())), 409);
        var package = load.Manifest!.PackageName;
        var runner = new PairedRunner(target.Name, target.Url, target.InstanceId, target.Fingerprint, target.Roles, target.PairedAt);
        var client = RunnerPublishClient.ForKey(runner, identity, "client:" + identity.Id, actor);
        try
        {
            if (options.Bind is { Count: > 0 })
            {
                await client.BindAsync(target.Name, null, package, options.Channel, options.Bind, null, step, ct);
                step($"Bound {string.Join(", ", options.Bind.Keys.Order(StringComparer.Ordinal))} in {options.Channel}.");
            }
            var done = await client.PublishAsync(target.Name, null, manifestPath, Path.Combine(artifactFolder, "files"), options.Channel, stage: !options.Serve, options.ExpectedVersion, step, ct);
            step($"{(done.Mode == "stage" ? "Staged" : "Published")} {package} on {done.Channel}: version {done.Version}{(done.Changed ? "" : " (the same content as before, nothing changed)")}.");
            return new JsonObject
            {
                ["package"] = done.Package, ["channel"] = done.Channel, ["mode"] = done.Mode, ["version"] = done.Version, ["changed"] = done.Changed, ["uploaded"] = done.Uploaded, ["skipped"] = done.Skipped,
                ["tasks"] = new JsonArray([.. done.Tasks.Select(t => (JsonNode?)t)]), ["target"] = target.Name, ["runner"] = target.Url,
            };
        }
        catch (RunnerClientException ex)
        {
            throw new WorkspacePublishException(ex.Message, ex.Message.Contains("could not be reached", StringComparison.Ordinal) ? 502 : ex.Message.Contains("(403)", StringComparison.Ordinal) ? 403 : 409);
        }
    }

    private static async Task<JsonObject> PublishModelsAsync(PublishTarget target, ConsoleIdentity identity, ServerRole role, string artifactFolder, ArtifactPublishOptions options, Action<string> step, CancellationToken ct)
    {
        var files = Directory.Exists(Path.Combine(artifactFolder, "models")) ? Directory.EnumerateFiles(Path.Combine(artifactFolder, "models"), "*.yaml").Order(StringComparer.Ordinal).ToList() : [];
        if (files.Count == 0) throw new WorkspacePublishException("This build has no model to publish.", 409);
        using var http = new HttpClient(PairingClient.Pinned(target.Fingerprint)) { BaseAddress = new Uri(target.Url + "/"), Timeout = TimeSpan.FromMinutes(2) };
        async Task<(int Status, JsonObject? Body, string Text)> Call(HttpMethod method, string path, JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, identity.Issuer().Issue(new ConsoleTokenRequest(target.InstanceId, "client:" + identity.Id, target.ClientName, role)));
            if (body is not null) request.Content = JsonContent.Create(body);
            try
            {
                using var response = await http.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                JsonObject? answer = null;
                try { answer = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject; }
                catch (JsonException) { /* not JSON: the text is the answer */ }
                return ((int)response.StatusCode, answer, text);
            }
            catch (HttpRequestException ex) { throw new WorkspacePublishException($"{target.Url} could not be reached ({ex.Message}).", 502); }
        }

        var published = new JsonArray();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var loaded = DataModelLoader.Load(text, Path.GetFileName(file));
            if (loaded.Model is not { } model) throw new WorkspacePublishException($"{Path.GetFileName(file)} is not a model that loads.", 409);
            var dataSource = string.IsNullOrWhiteSpace(options.DataSource) ? model.Connection : options.DataSource.Trim();
            // the person's confirmation names a data source; it holds for the data source the models go on, and nothing else (plan 33 SC21f)
            if (options.Adopt is { Count: > 0 } && !options.Adopt.Contains(dataSource, StringComparer.Ordinal))
                throw new WorkspacePublishException($"The confirmation names {string.Join(", ", options.Adopt.Select(a => "'" + a + "'"))}, and {model.Name} goes on '{dataSource}': confirm the data source it goes on.", 400);
            var path = $"api/v1/entity-models/{Uri.EscapeDataString(model.Name)}";
            step($"Asked {target.Url} for the version of {model.Name} it serves.");
            var held = await Call(HttpMethod.Get, path);
            if (held.Status is not (200 or 404)) throw Refused(held, $"reading the model {model.Name}");
            int? current = held.Status == 200 ? (int?)held.Body?["current_version"] : null;
            var sent = await Call(HttpMethod.Put, path, new JsonObject { ["model"] = text, ["data_source"] = dataSource, ["expected_version"] = current, ["adopt"] = options.Adopt is { Count: > 0 } ? dataSource : null });
            if (sent.Status is < 200 or >= 300) throw Refused(sent, $"the model {model.Name} on the data source '{dataSource}'");
            step($"Published {model.Name} to {target.Url} on the data source '{dataSource}'.");
            published.Add(new JsonObject { ["model"] = model.Name, ["data_source"] = dataSource, ["version"] = (int?)sent.Body?["version"], ["created"] = (bool?)sent.Body?["created"], ["unchanged"] = (bool?)sent.Body?["unchanged"], ["adopted"] = (string?)sent.Body?["adopted"] });
        }
        return new JsonObject { ["models"] = published, ["target"] = target.Name, ["gateway"] = target.Url };
    }

    /// <summary>
    /// The compiled semantic models of a Build project (<c>gateway.json</c>, <c>models/</c> of the artifact; plan 33 SC20e) to a Gateway: for each, <c>GET /api/v1/semantic-models/{name}</c> for the version it serves, then <c>PUT</c> with <c>{catalog, sources,
    /// expected_version}</c> as the paired client. The sources name data sources the Gateway already has (the connections' names): no login and no data source is sent, and the Gateway's own rules (the data source must exist, answer, and be one this client made)
    /// come back in its words.
    /// </summary>
    private static async Task<JsonObject> PublishSemanticModelsAsync(PublishTarget target, ConsoleIdentity identity, ServerRole role, string folder, ArtifactPublishOptions options, Action<string> step, CancellationToken ct)
    {
        var manifestPath = Path.Combine(folder, "gateway.json");
        if (!File.Exists(manifestPath)) throw new WorkspacePublishException("This build has no semantic model to publish (gateway.json is not in it).", 409);
        JsonObject manifest;
        try { manifest = JsonNode.Parse(File.ReadAllText(manifestPath)) as JsonObject ?? throw new JsonException(); }
        catch (JsonException) { throw new WorkspacePublishException("gateway.json of this build is not a document.", 409); }
        if ((string?)manifest["schema"] != "gateway-publish/1") throw new WorkspacePublishException($"gateway.json of this build says '{(string?)manifest["schema"]}', which this server does not read (gateway-publish/1).", 409);
        var entries = (manifest["models"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        if (entries.Count == 0) throw new WorkspacePublishException("This build has no semantic model to publish.", 409);

        using var http = new HttpClient(PairingClient.Pinned(target.Fingerprint)) { BaseAddress = new Uri(target.Url + "/"), Timeout = TimeSpan.FromMinutes(2) };
        async Task<(int Status, JsonObject? Body, string Text)> Call(HttpMethod method, string path, JsonObject? body = null)
        {
            using var request = new HttpRequestMessage(method, path);
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, identity.Issuer().Issue(new ConsoleTokenRequest(target.InstanceId, "client:" + identity.Id, target.ClientName, role)));
            if (body is not null) request.Content = JsonContent.Create(body);
            try
            {
                using var response = await http.SendAsync(request, ct);
                var text = await response.Content.ReadAsStringAsync(ct);
                JsonObject? answer = null;
                try { answer = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject; }
                catch (JsonException) { /* not JSON: the text is the answer */ }
                return ((int)response.StatusCode, answer, text);
            }
            catch (HttpRequestException ex) { throw new WorkspacePublishException($"{target.Url} could not be reached ({ex.Message}).", 502); }
        }

        var published = new JsonArray();
        foreach (var entry in entries)
        {
            var name = (string?)entry["name"] ?? throw new WorkspacePublishException("gateway.json names a model with no name.", 409);
            var file = (string?)entry["file"] ?? "";
            var full = Path.GetFullPath(Path.Combine(folder, file));
            if (!full.StartsWith(Path.GetFullPath(folder) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(full)) throw new WorkspacePublishException($"The catalog of {name} ({file}) is not in this build.", 409);
            var sources = new JsonObject();
            if (entry["sources"] is JsonObject map) foreach (var (source, dataSource) in map) sources[source] = (string?)dataSource;
            var path = $"api/v1/semantic-models/{Uri.EscapeDataString(name)}";
            step($"Asked {target.Url} for the version of {name} it serves.");
            var held = await Call(HttpMethod.Get, path);
            if (held.Status is not (200 or 404)) throw Refused(held, $"reading the model {name}");
            int? current = held.Status == 200 ? (int?)held.Body?["current_version"] : null;
            var sent = await Call(HttpMethod.Put, path, new JsonObject { ["catalog"] = File.ReadAllText(full), ["sources"] = sources, ["expected_version"] = current, ["adopt"] = options.Adopt is { Count: > 0 } ? new JsonArray([.. options.Adopt.Select(a => (JsonNode?)a)]) : null });
            if (sent.Status is < 200 or >= 300) throw Refused(sent, $"the model {name} on the data sources {string.Join(", ", sources.Select(s => "'" + (string?)s.Value + "'").Distinct())}");
            step($"Published {name} to {target.Url}.");
            published.Add(new JsonObject { ["model"] = name, ["data_sources"] = sources.DeepClone(), ["version"] = (int?)sent.Body?["version"], ["created"] = (bool?)sent.Body?["created"], ["unchanged"] = (bool?)sent.Body?["unchanged"], ["adopted"] = sent.Body?["adopted"]?.DeepClone() });
        }
        return new JsonObject { ["models"] = published, ["target"] = target.Name, ["gateway"] = target.Url };
    }

    private static WorkspacePublishException Refused((int Status, JsonObject? Body, string Text) answer, string doing)
    {
        var words = (string?)answer.Body?["error"] ?? (answer.Text.Length > 0 ? answer.Text : "no reason given");
        if (answer.Body?["diagnostics"] is JsonArray found && found.Count > 0) words += " " + string.Join(" ", found.Select(d => d is JsonValue v && v.TryGetValue<string>(out var text) ? text : d?.ToJsonString()));
        return new WorkspacePublishException($"The Gateway refused {doing} ({answer.Status}): {words}", answer.Status == 403 ? 403 : 409, answer.Body?["adopt"] is JsonObject hint ? (JsonObject)hint.DeepClone() : null);
    }
}
