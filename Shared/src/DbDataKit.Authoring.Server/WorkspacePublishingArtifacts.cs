using DbDataKit.Lineage;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Deploy;
using DbDataKit.Web.Trust;

namespace DbDataKit.Authoring.Server;

/// <summary>What a person says when they publish what a kind made (plan 33 SC20d, SC21b, SC22b): for a Runner the <see cref="Channel"/> (and whether it serves at once, <see cref="Serve"/>, or is staged, the default; <see cref="ExpectedVersion"/> and the connections <see cref="Bind"/> fills, slot to connection name); for a Gateway the <see cref="DataSource"/> its tables are on, the models' own storage connection when none is named, and <see cref="Adopt"/> (plan 33 SC21f) the data sources made by hand that the person confirmed the models may go on (the Gateway records the adoption).</summary>
public sealed record ArtifactPublishOptions(string? Channel = null, bool Serve = false, int? ExpectedVersion = null, IReadOnlyDictionary<string, string>? Bind = null, string? DataSource = null, IReadOnlyList<string>? Adopt = null, string? Model = null, string? Note = null)
{
    /// <summary>The same options as the shared deploy library takes them.</summary>
    public DeployOptions ToDeploy() => new(Channel, Serve, ExpectedVersion, Bind, DataSource, Adopt, Model, Note);
}

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
        var folder = route.What == "model-package" ? ModelPackageFolder(artifactFolder, options.Model) : artifactFolder;
        // a Runner's call names the person (as the Runner package client always did); a Gateway's names the client the target was paired under
        using var link = route.Product == PublishProducts.Runner
            ? RunnerLink(target, identity, actor)
            : new PinnedLink(target, identity, "client:" + identity.Id, target.ClientName, role, TimeSpan.FromMinutes(2));
        return await WorkspacePublishException.AdaptAsync(() => ArtifactDeploy.DeployAsync(kind.Name, route, link, identity, DeployTargetOf(target), folder, options.ToDeploy(), actor, step, ct));
    }

    /// <summary>The folder of one model's deploy package in a Store project's build (plan 33 SC24): the model the person named, or the only one when the project has one.</summary>
    private static string ModelPackageFolder(string artifactFolder, string? model)
    {
        var root = Path.Combine(artifactFolder, StoreProjectKind.RunnerFolder);
        var models = Directory.Exists(root) ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToList() : [];
        if (models.Count == 0) throw new WorkspacePublishException("This build has no deploy package of a model. Build the draft again: a build of a Store project makes one per model.", 409);
        if (string.IsNullOrWhiteSpace(model))
            return models.Count == 1 ? Path.Combine(root, models[0]) : throw new WorkspacePublishException($"Name the model to stage (\"model\"): this build has {string.Join(", ", models)}.", 400);
        return models.Contains(model.Trim(), StringComparer.Ordinal) ? Path.Combine(root, model.Trim()) : throw new WorkspacePublishException($"This build has no model '{model.Trim()}'; it has {string.Join(", ", models)}.", 404);
    }

    /// <summary>
    /// Runs the manual-only task <paramref name="task"/> of a version a build staged on a Runner (plan 28c E21, step e: a draft's database setup applied before it is published): the version is held by the Runner and not served, so no schedule
    /// starts; the run is recorded for <paramref name="actor"/>, the person, with the publisher key's client beside them, and runs with the logins bound on the Runner's channel, never one of this server. The pairing must allow the operator
    /// role (a run is an operator's act on a Runner); a Runner that refuses says so in its words.
    /// </summary>
    public async Task<JsonObject> RunStagedTaskAsync(string targetName, string package, string channel, string version, string task, IReadOnlyDictionary<string, string>? inputs, string actor, Action<string> step, CancellationToken ct = default)
    {
        var (link, calls) = await RunnerCallsAsync(targetName, actor, ct);
        using (link) return await WorkspacePublishException.AdaptAsync(() => calls.RunStagedTaskAsync(package, channel, version, task, inputs, actor, step, ct));
    }

    /// <summary>
    /// Runs <paramref name="task"/> of the version the channel serves now, for <paramref name="actor"/> (plan 33 SC20o, SC22l): a scheduled task too (a replication's drain, a refresh), which <see cref="RunStagedTaskAsync"/> refuses because a
    /// version that is not served starts nothing. The Runner's Run now, asked from the project's page so nobody has to find the Runner first; the pairing must allow the operator role.
    /// </summary>
    public async Task<JsonObject> RunServedTaskAsync(string targetName, string package, string channel, string task, IReadOnlyDictionary<string, string>? inputs, string actor, Action<string> step, CancellationToken ct = default)
    {
        var (link, calls) = await RunnerCallsAsync(targetName, actor, ct);
        using (link) return await WorkspacePublishException.AdaptAsync(() => calls.RunServedTaskAsync(package, channel, task, inputs, actor, step, ct));
    }

    /// <summary>A run on the Runner of <paramref name="targetName"/> by its id, with the steps named <paramref name="stepName"/> of its task recorded while it ran (their results as JSON); null when the Runner has no such run.</summary>
    public async Task<JsonObject?> RunOfAsync(string targetName, string package, string channel, Guid runId, string? stepName, string actor, CancellationToken ct = default)
    {
        var (link, calls) = await RunnerCallsAsync(targetName, actor, ct);
        using (link) return await WorkspacePublishException.AdaptAsync(() => calls.RunOfAsync(package, channel, runId, stepName, ct));
    }

    /// <summary>
    /// The Runner's own test of a slot's binding (plan 33 SC22h, the Runner's UI08 call): the slot is bound in the channel, the Runner has the connection it names, the login resolves and the connection opens, tried on the Runner the way a run
    /// would, so the login is the published set's bound one and never reaches this server. The Runner asks for its administrator role; a pairing without it is refused by the Runner in its words, which this passes on.
    /// The answer is the Runner's (<c>slot, ok, step, message, connected</c>) with where it was tried; no value is in it.
    /// </summary>
    public async Task<JsonObject> TestBindingAsync(string targetName, string package, string channel, string slot, string role, string actor, CancellationToken ct = default)
    {
        var (link, calls) = await RunnerCallsAsync(targetName, actor, ct);
        using (link) return await WorkspacePublishException.AdaptAsync(() => calls.TestBindingAsync(package, channel, slot, role, ct));
    }

    private async Task<(PinnedLink Link, RunnerCalls Calls)> RunnerCallsAsync(string targetName, string actor, CancellationToken ct)
    {
        var (target, key, _) = ResolveTarget(targetName, "a run of a task");
        var deployTarget = DeployTargetOf(target);
        WorkspacePublishException.Adapt(() => RunnerCalls.Require(deployTarget));
        var identity = await KeyAsync(targetName, key.SecretRef, ct);
        var link = RunnerLink(target, identity, actor);
        return (link, new RunnerCalls(link, deployTarget));
    }

    /// <summary>A paired Runner as the publisher key's client, on behalf of <paramref name="actor"/>, with every role the pairing allows (the Runner decides what each call needs).</summary>
    private static PinnedLink RunnerLink(PublishTarget target, ConsoleIdentity identity, string actor) =>
        new(target, identity, "client:" + identity.Id, actor, ServerRoles.TryParse(target.Roles, out var held) ? held : ServerRole.Publisher, TimeSpan.FromMinutes(30));

    /// <summary>
    /// The lineage graph of each Gateway this server holds a publisher key for (plan 21 CH06, for a Store model's contract step, plan 33 SC24): <c>GET /api/v1/lineage</c> over the pinned channel as the key's client, with the role the pairing
    /// allows; one graph per Gateway (a Gateway paired under two keys is asked once). A Gateway that does not answer, or answers what is not a graph, is named in <c>Unreachable</c> with why, never skipped: a caller refuses a step it cannot check.
    /// </summary>
    public async Task<(IReadOnlyList<(string Server, LineageGraph Graph)> Graphs, IReadOnlyList<(string Server, string Why)> Unreachable)> GatewayLineageAsync(CancellationToken ct = default)
    {
        var sources = new List<GatewayLineageSource>();
        var asked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in store.PublisherKeys().OrderBy(k => k.Name, StringComparer.Ordinal))
        {
            if (Find(key.Name) is not { Product: PublishProducts.Gateway } paired || !asked.Add(paired.InstanceId)) continue;
            sources.Add(new GatewayLineageSource(key.Name, paired.Url, async ct =>
            {
                try
                {
                    var (target, _, role) = ResolveTarget(key.Name, "a read of its lineage");
                    var identity = await KeyAsync(key.Name, key.SecretRef, ct);
                    return new PinnedLink(target, identity, "client:" + identity.Id, target.ClientName, role, TimeSpan.FromMinutes(2));
                }
                catch (WorkspacePublishException ex) { throw new DeployException(ex.Message, ex.Status, ex.Adopt); }
            }));
        }
        return await GatewayCalls.LineageAsync(sources, ct);
    }
}
