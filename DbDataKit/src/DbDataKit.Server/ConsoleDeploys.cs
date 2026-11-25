using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Deploy;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>
/// What a person asks the console to deploy: a build (<see cref="Build"/>) of a workspace of a project on a Workspace server, to a registered Runner or Gateway (<see cref="Target"/>), with what the deploy of its kind takes: for a Runner
/// the channel, staged or served, the version the person saw and the slots to bind; for a Gateway the data source and the adoptions the person confirmed; the model of a Store build, and a note kept with the version.
/// </summary>
public sealed record DeployBuildRequest(string WorkspaceServer, string Project, string Workspace, string Build, string Target, string? Channel = null, bool Serve = false, int? ExpectedVersion = null,
    IReadOnlyDictionary<string, string>? Bind = null, string? DataSource = null, IReadOnlyList<string>? Adopt = null, string? Model = null, string? Note = null, bool ConfirmPreflight = false);

/// <summary>What a deploy answered: the HTTP status and the body (the steps, the target's answer and the deploy record; or the refusal in words, with the data source to confirm when an adoption is asked).</summary>
public sealed record DeployBuildOutcome(int Status, JsonObject Body);

/// <summary>
/// The control service's deploy of a build (Design: docs/publishing.md#deploying-a-build-from-the-control-service). The Workspace server never calls a Runner or a Gateway: the console reads what the build holds to send and its build
/// record from the Workspace server as the signed-in person (<c>GET .../builds/{b}/contents</c>, then each file by hash), lays the files out under their paths and checks them against the hashes the build lists, picks the item the
/// kind's route to the target's product takes (<see cref="DeployRoutes.RouteFor"/>), and deploys it through <see cref="ArtifactDeploy"/> (or <see cref="AppDeploy"/> for an app) as the person, over the link
/// <see cref="ConsolePromotions.LinkFor"/> makes: the target decides what the person may do and records them. Each version is signed with the console's publisher key (<see cref="ConsolePublisherKeyStore"/>) over its manifest, files and
/// the build record (<see cref="BuildRecord"/>), when the target trusts that key. Every deploy, taken or refused, is kept (<see cref="DeployStore"/>, by Workspace server, project, workspace and build) and audited (<c>build.deploy</c>).
/// <para>
/// The console asks only that the person may read the project on the Workspace server: it reads the project, and then the build, as the person, and the Workspace server decides (<see cref="ProjectRefusalAsync"/>). What the person may
/// do on the target is the target's to decide: a Runner or a Gateway asks for its publisher role to take a version, and refuses in its own words. Only a build that succeeded is deployed, which is one the kind's validators passed.
/// </para>
/// <para>
/// A Runner package served in <c>prod</c> whose package declares a preflight is staged held, checked by its preflight on the staged version and activated only when the preflight found no error, or the person confirmed what it found
/// (<see cref="ConsoleDeployPreflight"/>).
/// </para>
/// </summary>
public sealed class ConsoleDeploys(StateDatabase state, ConsoleInstances instances, ConsoleRelay relay, ConsolePromotions promotions, ConsolePublisherKeyStore keys, ConsoleKeyStore identity, AuditLog audit)
{
    public const string AuditVerb = "build.deploy";

    public DeployStore Store { get; } = new(state);

    /// <summary>How long a deploy waits for the preflight of a version it staged in production before it answers that the run goes on (the person activates it once the run has ended).</summary>
    public TimeSpan PreflightWait { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>The deploys of the builds of a Workspace server (by its id or name), newest first; each part after the server narrows the one before.</summary>
    public IReadOnlyList<DeployRecord> List(string workspaceServer, string? project = null, string? workspace = null, string? build = null) =>
        Store.List(instances.Registry.Find(workspaceServer)?.InstanceId ?? workspaceServer, project, workspace, build);

    private sealed class Refusal(int status, string message, JsonObject? adopt = null) : Exception(message)
    {
        public int Status { get; } = status;
        public JsonObject? Adopt { get; } = adopt;
    }

    public async Task<DeployBuildOutcome> DeployAsync(DeployBuildRequest request, RelayPerson person, CancellationToken ct)
    {
        var steps = new List<string>();
        RegisteredInstance? workspaceServer = null, target = null;
        string? kind = null, record = null;
        BuildItem? item = null;
        var folder = Path.Combine(Path.GetTempPath(), "dbdatakit-deploy-" + Guid.NewGuid().ToString("N"));
        try
        {
            workspaceServer = instances.Registry.Find(request.WorkspaceServer) ?? throw new Refusal(404, $"No server '{request.WorkspaceServer}' is registered.");
            if (await relay.ProductOfAsync(workspaceServer, ct) is var wsProduct && wsProduct != ServerProducts.Workspace)
                throw new Refusal(400, $"{workspaceServer.Name} is {(wsProduct is null ? "not answering" : "a " + wsProduct + " server")}; a build is deployed from a Workspace server.");
            if (await ProjectRefusalAsync(workspaceServer, request.Project, person, ct) is { } unreadable) throw new Refusal(unreadable.Status, unreadable.Error);
            target = instances.Registry.Find(request.Target) ?? throw new Refusal(404, $"No server '{request.Target}' is registered.");
            var product = await relay.ProductOfAsync(target, ct);
            var deployProduct = DeployProducts.OfServer(product) ?? throw new Refusal(400, $"{target.Name} is {(product is null ? "not answering" : "a " + product + " server")}; a build is deployed to a Runner or a Gateway.");
            var deployTarget = new DeployTarget(target.Name, target.Url.ToString().TrimEnd('/'), target.InstanceId, deployProduct);

            var buildPath = $"api/v1/projects/{Uri.EscapeDataString(request.Project)}/workspaces/{Uri.EscapeDataString(request.Workspace)}/builds/{Uri.EscapeDataString(request.Build)}";
            var contents = await JsonAsync(workspaceServer, person, buildPath + "/contents", $"the build {request.Build}", ct);
            var described = contents["record"] as JsonObject ?? throw new Refusal(502, $"{workspaceServer.Name} did not describe the build.");
            kind = (string?)described["kind"] ?? throw new Refusal(502, $"{workspaceServer.Name} did not say the kind of the project.");
            var status = (string?)described["status"];
            if (status != "succeeded") throw new Refusal(409, $"This build is {status}, so there is nothing to publish. Only a build that succeeded is published.");
            steps.Add($"Read build {request.Build} of {request.Project}/{request.Workspace} from {workspaceServer.Name}.");

            var kinds = await JsonAsync(workspaceServer, person, "api/v1/project-kinds", "the kinds of project", ct);
            var routes = (kinds["kinds"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(k => (string?)k["name"] == kind)?["publishes"] as JsonArray
                ?? throw new Refusal(409, $"{workspaceServer.Name} does not say where a {kind} project is published.");
            var publishes = routes.OfType<JsonObject>().Select(r => new ProjectPublishRoute((string?)r["product"] ?? "", (string?)r["what"] ?? "", (string?)r["description"] ?? "")).ToList();
            var route = DeployRoutes.RouteFor(kind, publishes, deployTarget);

            var items = (contents["items"] as JsonArray ?? []).OfType<JsonObject>().Select(BuildItem.FromJson).Where(i => i.Product == route.Product && i.What == route.What).ToList();
            item = PickItem(items, route, request.Model);
            record = BuildRecord.Text(described, item);

            var root = await BuildContents.FetchAsync(item, folder, (sha, token) => BytesAsync(workspaceServer, person, $"{buildPath}/files/{sha}", token), ct);
            steps.Add($"Read the {item.Files.Count} file(s) of {item.Id} and checked each against its SHA-256.");

            var link = promotions.LinkFor(target.InstanceId, person) ?? throw new Refusal(404, $"No server '{request.Target}' is registered.");
            var publisher = new ConsoleIdentity((await identity.GetOrCreateAsync(ct)).Id, await keys.GetOrCreateAsync(ct));
            var provenance = $"From workspace {request.Workspace} of project {request.Project}, build {request.Build}" + ((string?)described["commit"] is { Length: > 0 } sha ? $", commit {sha[..Math.Min(12, sha.Length)]}" : "")
                + (described["uncommitted_changes"]?.GetValue<bool>() == true ? " (with changes not yet committed)" : "") + $", deployed by {person.Name ?? person.Id}.";
            var note = (string.IsNullOrWhiteSpace(request.Note) ? provenance : request.Note.Trim() + " | " + provenance) + " " + DeployOrigin.Token(workspaceServer.InstanceId, request.Project, request.Workspace);
            // a package served in prod is checked first when it declares a preflight: staged held, the preflight run on the staged version, then activated (below)
            var preflightAsked = ConsoleDeployPreflight.AskedFor(request.Channel, request.Serve, deployProduct, route.What);
            var gated = preflightAsked && ArtifactDeploy.PackageTasks(root).Contains(DbDataKit.Runner.Preflight.TaskName);
            if (preflightAsked && !gated) steps.Add($"The package declares no {DbDataKit.Runner.Preflight.TaskName} task, so nothing is checked before it is served in {request.Channel}.");
            var options = new DeployOptions(request.Channel, request.Serve && !gated, request.ExpectedVersion, request.Bind, request.DataSource, request.Adopt, item.Model, note, gated ? ConsoleDeployPreflight.HoldReason(person) : null);
            JsonObject answer;
            try
            {
                answer = route.What == "app"
                    ? await AppDeploy.SendAsync(link, deployTarget, AppDeploy.Read(root, note, steps.Add), steps.Add, ct)
                    : await ArtifactDeploy.DeployAsync(kind, route, link, publisher, deployTarget, root, options, person.Name ?? person.Id, steps.Add, ct, record);
            }
            catch (DeployException ex) { throw new Refusal(ex.Status, ex.Message, ex.Adopt); }

            if (preflightAsked && !gated) answer["preflight"] = new JsonObject { ["declared"] = false };
            var kept = Keep(request, person, workspaceServer, target, deployProduct, kind, route.What, item, record, AuditEvent.Ok, null, answer, publisher.Key.Fingerprint);
            if (gated) return await PreflightThenActivateAsync(kept, answer, new RunnerCalls(link, deployTarget), target, request.ConfirmPreflight, person, steps, ct);
            return new DeployBuildOutcome(200, new JsonObject { ["steps"] = Steps(steps), ["deployed"] = answer, ["deploy"] = Describe(kept) });
        }
        catch (Exception ex) when (ex is Refusal or DeployException)
        {
            var (code, adopt) = ex is Refusal r ? (r.Status, r.Adopt) : (((DeployException)ex).Status, ((DeployException)ex).Adopt);
            var body = new JsonObject { ["error"] = ex.Message, ["steps"] = Steps(steps) };
            if (adopt is not null) body["adopt"] = adopt.DeepClone();
            if (workspaceServer is not null && target is not null && kind is not null)
                body["deploy"] = Describe(Keep(request, person, workspaceServer, target, DeployProducts.OfServer(await relay.ProductOfAsync(target, ct)) ?? "", kind, item?.What ?? "", item, record ?? "", AuditEvent.Refused, ex.Message, null));
            else
                audit.Record(new AuditEvent("user", person.Id, AuditVerb, AuditEvent.Refused, TargetKind: "server", TargetId: target?.InstanceId ?? request.Target,
                    Detail: JsonSerializer.Serialize(new { workspace_server = request.WorkspaceServer, project = request.Project, workspace = request.Workspace, build = request.Build, error = ex.Message })));
            return new DeployBuildOutcome(code is >= 400 and < 600 ? code : 502, body);
        }
        finally
        {
            try { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temporary copy; the next start of the machine clears it */ }
        }
    }

    /// <summary>
    /// The preflight of the version <paramref name="kept"/> staged held in production, then its activation: the preflight runs on the staged version as the person and is waited for (<see cref="PreflightWait"/>); with no error, or
    /// with the person's confirmation (<paramref name="confirmed"/>), the Runner activates it and keeps the reason. An error not confirmed answers 409 with the findings and leaves the version staged; a run still going answers 202. Either way
    /// the deploy record keeps the preflight, and <c>POST api/deploys/{id}/activate</c> activates it later.
    /// </summary>
    private async Task<DeployBuildOutcome> PreflightThenActivateAsync(DeployRecord kept, JsonObject answer, RunnerCalls calls, RegisteredInstance runner, bool confirmed, RelayPerson person, List<string> steps, CancellationToken ct)
    {
        var (package, channel, version) = (kept.DefinitionName!, kept.Channel!, kept.Version!.Value);
        steps.Add($"Version {version} waits on {runner.Name} for its preflight before it is activated in {channel}.");
        JsonObject report;
        try
        {
            var started = await calls.RunStagedTaskAsync(package, channel, version.ToString(System.Globalization.CultureInfo.InvariantCulture), DbDataKit.Runner.Preflight.TaskName, null, person.Name ?? person.Id, steps.Add, ct);
            var run = Guid.Parse((string)started["run_id"]!);
            report = ConsoleDeployPreflight.Report(run, await ConsoleDeployPreflight.WaitAsync(calls, package, channel, run, PreflightWait, ct), runner.InstanceId);
        }
        catch (DeployException ex)
        {
            // the preflight could not be started (one is running already, the Runner refused): the version stays staged and held, and the words say why
            answer["preflight"] = new JsonObject { ["declared"] = true, ["state"] = "not-started", ["error"] = ex.Message, ["activation"] = "refused" };
            Store.Revise(kept.Id, kept.Mode, answer.ToJsonString());
            return new DeployBuildOutcome(409, new JsonObject { ["error"] = $"The preflight of version {version} could not be started, so it was not activated in {channel}: {ex.Message}", ["steps"] = Steps(steps), ["deployed"] = answer, ["deploy"] = Describe(Store.Get(kept.Id)!) });
        }

        report["declared"] = true;
        steps.Add((string?)report["state"] == "finished"
            ? $"The preflight (run {report["run"]}) found {report["errors"]} error(s) and {report["warnings"]} warning(s)."
            : $"The preflight (run {report["run"]}) has not ended after {PreflightWait.TotalSeconds:0} seconds.");
        var activation = await ConsoleDeployPreflight.ActivateAsync(calls, package, channel, version, report, confirmed, person, ct);
        Settle(kept, answer, report, activation, confirmed, person, runner);
        steps.Add(activation.Message);
        // a preflight still running is not a refusal: the version is staged and waits, and the person activates it once the run has ended
        var status = (string?)report["state"] == "finished" ? activation.Status : 202;
        var body = new JsonObject { ["steps"] = Steps(steps), ["deployed"] = answer, ["deploy"] = Describe(Store.Get(kept.Id)!) };
        if (status >= 400) body["error"] = activation.Message;
        return new DeployBuildOutcome(status, body);
    }

    /// <summary>
    /// Keeps what became of an activation after a preflight in the deploy record (the report, the activation, who confirmed errors; the mode <c>serve</c> once it is active) and audits it as <see cref="ConsoleDeployPreflight.ActivateVerb"/>,
    /// with a confirmation of errors also as <see cref="ConsoleDeployPreflight.ConfirmVerb"/>.
    /// </summary>
    internal void Settle(DeployRecord kept, JsonObject answer, JsonObject report, PreflightActivation activation, bool confirmed, RelayPerson person, RegisteredInstance runner)
    {
        report["activation"] = activation.State;
        var confirming = confirmed && (int?)report["errors"] > 0 && activation.State is "activated" or "waiting";
        if (confirming) { report["confirmed_by"] = person.Id; report["confirmed_at"] = DateTimeOffset.UtcNow.UtcDateTime.ToString("O"); }
        answer["preflight"] = report;
        if (activation.State == "activated") { answer["mode"] = "serve"; answer["version"] = activation.Active; }
        Store.Revise(kept.Id, activation.State == "activated" ? "serve" : kept.Mode, answer.ToJsonString());

        if ((string?)report["state"] != "finished") return;   // nothing was asked of the Runner yet
        var outcome = activation.Status < 400 ? AuditEvent.Ok : AuditEvent.Refused;
        var detail = JsonSerializer.Serialize(new { deploy = kept.Id, package = kept.DefinitionName, channel = kept.Channel, version = kept.Version, preflight = (string?)report["run"], errors = (int?)report["errors"], activation = activation.State });
        audit.Record(new AuditEvent("user", person.Id, ConsoleDeployPreflight.ActivateVerb, outcome, TargetKind: "server", TargetId: runner.InstanceId, Detail: detail));
        if (confirming) audit.Record(new AuditEvent("user", person.Id, ConsoleDeployPreflight.ConfirmVerb, AuditEvent.Ok, TargetKind: "server", TargetId: runner.InstanceId, Detail: detail));
    }

    /// <summary>
    /// Whether the person may read <paramref name="project"/> on <paramref name="workspaceServer"/>, asked of the Workspace server itself as the person (<c>GET /api/v1/projects/{project}</c>): null when it answers with the project,
    /// otherwise the status and the words to refuse with. The Workspace server decides from the person's roles there and on the project; the console adds no role of its own, since the target of a deploy or a run checks the person's
    /// roles again (Design: docs/security.md#when-a-safety-rule-is-worth-adding).
    /// </summary>
    internal async Task<(int Status, string Error)?> ProjectRefusalAsync(RegisteredInstance workspaceServer, string project, RelayPerson person, CancellationToken ct)
    {
        var (status, body) = await relay.CallAsAsync(workspaceServer, person.Id, person.Name, person.Role, HttpMethod.Get, $"/api/v1/projects/{Uri.EscapeDataString(project)}", null, ct);
        if (status == 200) return null;
        var words = (body as JsonObject)?["error"] is JsonValue error && error.TryGetValue<string>(out var text) ? text : null;
        return status switch
        {
            404 => (404, $"{workspaceServer.Name} has no project '{project}'."),
            401 or 403 => (403, $"{workspaceServer.Name} does not let you read the project '{project}': {words ?? "it refused."}"),
            _ => (502, $"{workspaceServer.Name} answered {status} when asked for the project '{project}'{(words is null ? "." : ": " + words)}"),
        };
    }

    /// <summary>The item of the build that goes by <paramref name="route"/>: the only one, or for a Store build's model packages the model the person named (the only one when the build has one).</summary>
    private static BuildItem PickItem(IReadOnlyList<BuildItem> items, ProjectPublishRoute route, string? model)
    {
        if (route.What != "model-package")
            return items.FirstOrDefault() ?? throw new Refusal(409, $"This build has nothing to send to a {route.Product} ({route.What}).");
        var models = items.Select(i => i.Model ?? "").Order(StringComparer.Ordinal).ToList();
        if (models.Count == 0) throw new Refusal(409, "This build has no deploy package of a model. Build the draft again: a build of a Store project makes one per model.");
        if (string.IsNullOrWhiteSpace(model))
            return items.Count == 1 ? items[0] : throw new Refusal(400, $"Name the model to stage (\"model\"): this build has {string.Join(", ", models)}.");
        return items.FirstOrDefault(i => i.Model == model.Trim()) ?? throw new Refusal(404, $"This build has no model '{model.Trim()}'; it has {string.Join(", ", models)}.");
    }

    private async Task<JsonObject> JsonAsync(RegisteredInstance server, RelayPerson person, string path, string what, CancellationToken ct)
    {
        using var response = await relay.SendAsAsync(server, person.Id, person.Name, person.Role, HttpMethod.Get, path, null, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonObject? body = null;
        try { body = string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text) as JsonObject; }
        catch (JsonException) { /* not JSON: said below */ }
        if (response.IsSuccessStatusCode && body is not null) return body;
        var status = (int)response.StatusCode;
        throw new Refusal(status == 404 ? 404 : status is 401 or 403 ? 403 : 502,
            status == 404 && body?["error"] is null ? $"{server.Name} has no {what}." : $"{server.Name} answered {status} for {what}: {(string?)body?["error"] ?? response.ReasonPhrase}");
    }

    private async Task<byte[]> BytesAsync(RegisteredInstance server, RelayPerson person, string path, CancellationToken ct)
    {
        using var response = await relay.SendAsAsync(server, person.Id, person.Name, person.Role, HttpMethod.Get, path, null, ct);
        if (!response.IsSuccessStatusCode) throw new Refusal(502, $"{server.Name} answered {(int)response.StatusCode} for a file of the build ({path[(path.LastIndexOf('/') + 1)..]}).");
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    private DeployRecord Keep(DeployBuildRequest request, RelayPerson person, RegisteredInstance workspaceServer, RegisteredInstance target, string product, string kind, string route, BuildItem? item, string record, string outcome, string? error, JsonObject? answer, string? keyId = null)
    {
        // what the target answered: a Runner package's name, channel, version and mode; the names of a Gateway's models; an app's name and version
        string? name = (string?)answer?["package"] ?? (string?)answer?["app"]
            ?? (answer?["models"] is JsonArray models ? string.Join(",", models.OfType<JsonObject>().Select(m => (string?)m["model"] ?? (string?)m["name"]).Where(n => n is not null)) : null);
        int? version = answer?["version"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
        var kept = Store.Add(new DeployRecord(DeployStore.NewId(), workspaceServer.InstanceId, request.Project, request.Workspace, request.Build, item?.Id ?? "", kind, target.InstanceId, target.Name, product, route,
            string.IsNullOrEmpty(name) ? null : name, (string?)answer?["channel"], version, (string?)answer?["mode"], (string?)answer?["signature"], (string?)answer?["signature"] == "verified" ? keyId : null, record, person.Id, DateTimeOffset.UtcNow, outcome, error, answer?.ToJsonString()));
        audit.Record(new AuditEvent("user", person.Id, AuditVerb, outcome, TargetKind: "server", TargetId: target.InstanceId,
            Detail: JsonSerializer.Serialize(new { deploy = kept.Id, workspace_server = workspaceServer.InstanceId, project = request.Project, workspace = request.Workspace, build = request.Build, item = item?.Id, target = target.Name, channel = kept.Channel, version, error })));
        return kept;
    }

    private static JsonArray Steps(List<string> steps) => new([.. steps.Select(s => (JsonNode?)s)]);

    /// <summary>A deploy record as the console's answers show it; the build record is the object it was signed as, and <c>tasks</c> the tasks a Runner said the package has (empty for what is not a Runner package).</summary>
    public static JsonObject Describe(DeployRecord d) => new()
    {
        ["tasks"] = TasksOf(d.Answer),
        ["id"] = d.Id, ["workspace_server"] = d.WorkspaceServer, ["project"] = d.Project, ["workspace"] = d.Workspace, ["build"] = d.Build, ["item"] = d.Item, ["kind"] = d.ProjectKind,
        ["target"] = d.Target, ["target_name"] = d.TargetName, ["product"] = d.Product, ["route"] = d.Route, ["name"] = d.DefinitionName, ["channel"] = d.Channel, ["version"] = d.Version, ["mode"] = d.Mode,
        ["signature"] = d.Signature, ["build_record"] = BuildRecord.Read(d.BuildRecord), ["deployed_by"] = d.DeployedBy, ["deployed_at"] = d.DeployedAt.UtcDateTime.ToString("O"), ["outcome"] = d.Outcome, ["error"] = d.Error,
        ["preflight"] = PreflightOf(d.Answer),
    };

    /// <summary>The preflight the deploy waited for (its run, link, findings and what became of the activation), or null for a deploy that waited for none.</summary>
    public static JsonObject? PreflightOf(string? answer)
    {
        try { return (string.IsNullOrEmpty(answer) ? null : JsonNode.Parse(answer))?["preflight"]?.DeepClone() as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static JsonArray TasksOf(string? answer)
    {
        JsonNode? parsed = null;
        try { parsed = string.IsNullOrEmpty(answer) ? null : JsonNode.Parse(answer); }
        catch (JsonException) { /* an answer kept as it came; one that is not JSON has no tasks */ }
        return new JsonArray([.. ((parsed as JsonObject)?["tasks"] as JsonArray ?? []).Select(t => t is JsonValue v && v.TryGetValue<string>(out var name) ? (JsonNode?)name : null).Where(t => t is not null)]);
    }
}
