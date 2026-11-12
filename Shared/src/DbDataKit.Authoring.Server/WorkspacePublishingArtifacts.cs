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
