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

    /// <summary>
    /// Runs the manual-only task <paramref name="task"/> of a version a build staged on a Runner (plan 28c E21, step e: a draft's database setup applied before it is published): the version is held by the Runner and not served, so no schedule
    /// starts; the run is recorded for <paramref name="actor"/>, the person, with the publisher key's client beside them, and runs with the logins bound on the Runner's channel, never one of this server. The pairing must allow the operator
    /// role (a run is an operator's act on a Runner); a Runner that refuses says so in its words.
    /// </summary>
    public async Task<JsonObject> RunStagedTaskAsync(string targetName, string package, string channel, string version, string task, IReadOnlyDictionary<string, string>? inputs, string actor, Action<string> step, CancellationToken ct = default)
    {
        var (target, client) = await RunnerClientAsync(targetName, actor, ct);
        try
        {
            var runId = await client.RunVersionAsync(target.Name, null, package, channel, task, version, inputs, actor, step, ct)
                ?? throw new WorkspacePublishException($"{package}/{channel}/{task} is queued or running already on {target.Name}; wait for that run to end.", 409);
            step($"Started {task} of {package} version {version} on {channel}: run {runId}.");
            return new JsonObject { ["run_id"] = runId, ["task"] = task, ["package"] = package, ["channel"] = channel, ["version"] = version, ["target"] = target.Name, ["runner"] = target.Url, ["on_behalf_of"] = actor };
        }
        catch (RunnerClientException ex) { throw Refused(ex); }
    }

    /// <summary>A run on the Runner of <paramref name="targetName"/> by its id, with the steps named <paramref name="stepName"/> of its task recorded while it ran (their results as JSON); null when the Runner has no such run.</summary>
    public async Task<JsonObject?> RunOfAsync(string targetName, string package, string channel, Guid runId, string? stepName, string actor, CancellationToken ct = default)
    {
        var (target, client) = await RunnerClientAsync(targetName, actor, ct);
        try
        {
            if (await client.RunAsync(target.Name, null, runId, null, ct) is not { } run) return null;
            var answer = new JsonObject
            {
                ["id"] = run.Id, ["task"] = run.Task, ["outcome"] = run.Outcome, ["started"] = run.Started, ["finished"] = run.Finished, ["version"] = run.PackageVersion,
                ["failure"] = run.FailureMessage is null ? null : new JsonObject { ["kind"] = run.FailureKind, ["message"] = run.FailureMessage },
            };
            if (stepName is not null)
            {
                // a step is keyed by what it is about, not by the run: the steps of this run are those that started while it ran
                var steps = await client.StepsAsync(target.Name, null, package, channel, run.Task, stepName, 20, null, null, null, ct);
                answer["steps"] = new JsonArray([.. steps.Where(s => s.Started >= run.Started && (run.Finished is null || s.Started <= run.Finished)).OrderBy(s => s.Started)
                    .Select(s => (JsonNode?)new JsonObject { ["key"] = s.Key, ["started"] = s.Started, ["completed"] = s.Completed, ["result"] = s.Result is null ? null : JsonNode.Parse(s.Result) })]);
            }
            return answer;
        }
        catch (RunnerClientException ex) { throw Refused(ex); }
    }

    private async Task<(PublishTarget Target, RunnerPublishClient Client)> RunnerClientAsync(string targetName, string actor, CancellationToken ct)
    {
        var (target, key, _) = ResolveTarget(targetName, "a run of a task");
        if (target.Product != PublishProducts.Runner) throw new WorkspacePublishException($"'{targetName}' is a {target.Product} ({target.Url}), not a Runner; only a Runner runs a task.", 409);
        var identity = await KeyAsync(targetName, key.SecretRef, ct);
        var runner = new PairedRunner(target.Name, target.Url, target.InstanceId, target.Fingerprint, target.Roles, target.PairedAt);
        return (target, RunnerPublishClient.ForKey(runner, identity, "client:" + identity.Id, actor));
    }

    private static WorkspacePublishException Refused(RunnerClientException ex) =>
        new(ex.Message, ex.Message.Contains("could not be reached", StringComparison.Ordinal) ? 502 : ex.Message.Contains("(403)", StringComparison.Ordinal) ? 403 : ex.Message.Contains("(404)", StringComparison.Ordinal) ? 404 : 409);

    /// <summary>The paired Gateway as the shared publish API's caller reaches it (plan 33 SW01): requests relative to <c>/api/v1/</c> over the pinned channel, each with a fresh token naming the publisher key's client, the target's client name and the role the pairing allows.</summary>
    private sealed class GatewayLink(PublishTarget target, ConsoleIdentity identity, ServerRole role, HttpClient http) : DbDataKit.State.IDefinitionsLink
    {
        public string Name => target.Url;

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
        {
            var request = new HttpRequestMessage(method, "api/v1/" + path) { Content = content };
            request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, identity.Issuer().Issue(new ConsoleTokenRequest(target.InstanceId, "client:" + identity.Id, target.ClientName, role)));
            return http.SendAsync(request, ct);
        }
    }

    private static HttpClient GatewayHttp(PublishTarget target) => new(PairingClient.Pinned(target.Fingerprint)) { BaseAddress = new Uri(target.Url + "/"), Timeout = TimeSpan.FromMinutes(2) };

    /// <summary>A Store project's models to a Gateway as entity models, through the shared publish API (plan 33 SW01, <see cref="WorkspaceGatewayPublish"/>).</summary>
    private static async Task<JsonObject> PublishModelsAsync(PublishTarget target, ConsoleIdentity identity, ServerRole role, string artifactFolder, ArtifactPublishOptions options, Action<string> step, CancellationToken ct)
    {
        var files = Directory.Exists(Path.Combine(artifactFolder, "models")) ? Directory.EnumerateFiles(Path.Combine(artifactFolder, "models"), "*.yaml").Order(StringComparer.Ordinal).ToList() : [];
        if (files.Count == 0) throw new WorkspacePublishException("This build has no model to publish.", 409);
        using var http = GatewayHttp(target);
        try
        {
            var published = await new WorkspaceGatewayPublish(new GatewayLink(target, identity, role, http), identity, step, ct).PublishEntityModelsAsync([.. files.Select(f => (f, File.ReadAllText(f)))], options.DataSource, options.Adopt);
            return new JsonObject { ["models"] = published, ["target"] = target.Name, ["gateway"] = target.Url };
        }
        catch (HttpRequestException ex) { throw new WorkspacePublishException($"{target.Url} could not be reached ({ex.Message}).", 502); }
    }

    /// <summary>
    /// The compiled semantic models of a Build project (<c>gateway.json</c>, <c>models/</c>, <c>data-sources/</c> of the artifact; plan 33 SC20e) to a Gateway as data source and semantic model definitions, through the shared publish API (plan 33 SW01,
    /// <see cref="WorkspaceGatewayPublish"/>): no login is sent, and the Gateway's own rules come back in its words.
    /// </summary>
    private static async Task<JsonObject> PublishSemanticModelsAsync(PublishTarget target, ConsoleIdentity identity, ServerRole role, string folder, ArtifactPublishOptions options, Action<string> step, CancellationToken ct)
    {
        using var http = GatewayHttp(target);
        try
        {
            var (models, dataSources) = await new WorkspaceGatewayPublish(new GatewayLink(target, identity, role, http), identity, step, ct).PublishSemanticModelsAsync(folder, options.Adopt);
            return new JsonObject { ["models"] = models, ["data_sources"] = dataSources, ["target"] = target.Name, ["gateway"] = target.Url };
        }
        catch (HttpRequestException ex) { throw new WorkspacePublishException($"{target.Url} could not be reached ({ex.Message}).", 502); }
    }
}
