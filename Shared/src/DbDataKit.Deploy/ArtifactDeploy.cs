using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Runner;
using DbDataKit.Runner.Client;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Deploy;

/// <summary>
/// What a kind of project made (a build's <c>artifact/</c>), deployed by the route its kind takes to the target's product: a <c>package</c> or a Store model's <c>model-package</c> to a Runner, <c>model</c> (Store models) or
/// <c>semantic-model</c> (a Build project's models and views) to a Gateway. Each call goes through the link the caller gives, which says who is calling; Gateway definitions are signed with the key the caller gives.
/// </summary>
public static class ArtifactDeploy
{
    /// <summary>
    /// Deploys <paramref name="folder"/> by <paramref name="route"/> (from <see cref="DeployRoutes.RouteFor"/>) to <paramref name="target"/> through <paramref name="link"/>. For <c>model-package</c> the folder is the one model's deploy package
    /// the caller chose; for the others it is the build's artifact folder. The publisher a Runner records is <paramref name="actor"/>, the person who deployed. <paramref name="step"/> is told each step as it happens.
    /// <paramref name="build"/>, when given, is the record of the build the folder comes from: every version sent carries it, and the signature with <paramref name="publisher"/>'s key covers it.
    /// </summary>
    public static async Task<JsonObject> DeployAsync(string kindName, ProjectPublishRoute route, IDefinitionsLink link, ConsoleIdentity publisher, DeployTarget target, string folder, DeployOptions options, string actor, Action<string> step, CancellationToken ct = default, string? build = null) =>
        route.What switch
        {
            "package" or "model-package" => await PublishPackageAsync(link, target, folder, options, actor, step, ct, publisher, build),
            "model" => await PublishModelsAsync(link, publisher, target, folder, options, step, ct, build),
            "semantic-model" => await PublishSemanticModelsAsync(link, publisher, target, Path.Combine(folder, "gateway"), options, step, ct, build),
            _ => throw new DeployException($"A {kindName} project publishes '{route.What}' to a {route.Product}, which this server does not know how to send.", 409),
        };

    /// <summary>
    /// A Runner package (<c>package.yaml</c> and <c>files/</c> of <paramref name="artifactFolder"/>): the slots <see cref="DeployOptions.Bind"/> names are bound in the channel first, then the version is staged, or served with
    /// <see cref="DeployOptions.Serve"/>. Files the Runner holds already are not sent again. With <paramref name="signer"/> the version is signed when the Runner trusts its key (<see cref="PackageSigning"/>), over <paramref name="build"/>
    /// too when one is given; the Runner keeps the build record with the version.
    /// </summary>
    public static async Task<JsonObject> PublishPackageAsync(IDefinitionsLink link, DeployTarget target, string artifactFolder, DeployOptions options, string actor, Action<string> step, CancellationToken ct = default, ConsoleIdentity? signer = null, string? build = null)
    {
        if (string.IsNullOrWhiteSpace(options.Channel)) throw new DeployException("Name the channel of the Runner the package goes to (\"channel\").", 400);
        var manifestPath = Path.Combine(artifactFolder, "package.yaml");
        if (!File.Exists(manifestPath)) throw new DeployException("This build has no package to publish (package.yaml is not in it).", 409);
        var load = PackageManifestReader.Load(File.ReadAllText(manifestPath), "package.yaml");
        if (!load.Ok) throw new DeployException("The package of this build is not valid: " + string.Join(" ", load.Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.ToString())), 409);
        var package = load.Manifest!.PackageName;
        var client = RunnerPublishClient.ForLink(link);
        try
        {
            if (options.Bind is { Count: > 0 })
            {
                await client.BindAsync(target.Name, null, package, options.Channel, options.Bind, null, step, ct);
                step($"Bound {string.Join(", ", options.Bind.Keys.Order(StringComparer.Ordinal))} in {options.Channel}.");
            }
            var done = await client.PublishAsync(target.Name, null, manifestPath, Path.Combine(artifactFolder, "files"), options.Channel, stage: !options.Serve, options.ExpectedVersion, step, ct, options.Note,
                signer is null ? null : PackageSigning.For(link, signer.Key), build, options.Serve ? null : options.Hold);
            step($"{(done.Mode == "stage" ? "Staged" : "Published")} {package} on {done.Channel}: version {done.Version}{(done.Changed ? "" : " (the same content as before, nothing changed)")}.");
            return new JsonObject
            {
                ["package"] = done.Package, ["channel"] = done.Channel, ["mode"] = done.Mode, ["version"] = done.Version, ["changed"] = done.Changed, ["uploaded"] = done.Uploaded, ["skipped"] = done.Skipped,
                ["tasks"] = new JsonArray([.. done.Tasks.Select(t => (JsonNode?)t)]), ["target"] = target.Name, ["runner"] = target.Url, ["signature"] = done.Signature,
            };
        }
        catch (RunnerClientException ex) { throw RunnerCalls.Refused(ex, notFound: false); }
    }

    /// <summary>The names of the tasks the package of <paramref name="artifactFolder"/> declares (its <c>package.yaml</c>); empty when the folder has no package that reads.</summary>
    public static IReadOnlyList<string> PackageTasks(string artifactFolder)
    {
        var manifestPath = Path.Combine(artifactFolder, "package.yaml");
        if (!File.Exists(manifestPath)) return [];
        var load = PackageManifestReader.Load(File.ReadAllText(manifestPath), "package.yaml");
        return load.Ok ? [.. load.Manifest!.Tasks.Select(t => t.Name)] : [];
    }

    /// <summary>A Store project's models (<c>models/*.yaml</c> of <paramref name="artifactFolder"/>) to a Gateway as entity models (<see cref="GatewayDefinitionsPublish.PublishEntityModelsAsync"/>).</summary>
    public static async Task<JsonObject> PublishModelsAsync(IDefinitionsLink link, ConsoleIdentity publisher, DeployTarget target, string artifactFolder, DeployOptions options, Action<string> step, CancellationToken ct = default, string? build = null)
    {
        var files = Directory.Exists(Path.Combine(artifactFolder, "models")) ? Directory.EnumerateFiles(Path.Combine(artifactFolder, "models"), "*.yaml").Order(StringComparer.Ordinal).ToList() : [];
        if (files.Count == 0) throw new DeployException("This build has no model to publish.", 409);
        try
        {
            var published = await new GatewayDefinitionsPublish(link, publisher, step, ct, build).PublishEntityModelsAsync([.. files.Select(f => (f, File.ReadAllText(f)))], options.DataSource, options.Adopt);
            return new JsonObject { ["models"] = published, ["target"] = target.Name, ["gateway"] = target.Url, ["signature"] = SignatureOf(published) };
        }
        catch (HttpRequestException ex) { throw new DeployException($"{target.Url} could not be reached ({ex.Message}).", 502, inner: ex); }
    }

    /// <summary>
    /// The compiled semantic models of a Build project (<paramref name="folder"/> is <c>artifact/gateway/</c>: <c>gateway.json</c>, <c>models/</c>, <c>data-sources/</c>) to a Gateway as data source and semantic model definitions, then its
    /// dashboards and reports, after the models they query (<see cref="GatewayDefinitionsPublish"/>). No login is sent, and the Gateway's own rules come back in its words.
    /// </summary>
    public static async Task<JsonObject> PublishSemanticModelsAsync(IDefinitionsLink link, ConsoleIdentity publisher, DeployTarget target, string folder, DeployOptions options, Action<string> step, CancellationToken ct = default, string? build = null)
    {
        try
        {
            var publish = new GatewayDefinitionsPublish(link, publisher, step, ct, build);
            var (models, dataSources) = await publish.PublishSemanticModelsAsync(folder, options.Adopt);
            var views = await publish.PublishViewsAsync(folder);
            var answer = new JsonObject { ["models"] = models, ["data_sources"] = dataSources, ["target"] = target.Name, ["gateway"] = target.Url, ["signature"] = SignatureOf(models) };
            if (views.Count > 0) answer["views"] = views;
            return answer;
        }
        catch (HttpRequestException ex) { throw new DeployException($"{target.Url} could not be reached ({ex.Message}).", 502, inner: ex); }
    }

    /// <summary>What the Gateway said of the signatures of the models it took: <c>verified</c> when it verified every one, otherwise the first other word it gave (a model it took unsigned); null when it said nothing of one of them.</summary>
    private static string? SignatureOf(JsonArray models)
    {
        var words = models.OfType<JsonObject>().Select(m => (string?)m["signature"]).ToList();
        if (words.Count == 0 || words.Any(w => w is null)) return words.FirstOrDefault(w => w is not null and not "verified");
        return words.FirstOrDefault(w => w != "verified") ?? "verified";
    }
}
