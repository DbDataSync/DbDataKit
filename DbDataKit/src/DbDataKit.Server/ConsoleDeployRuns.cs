using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Deploy;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>What a run call of <see cref="ConsoleDeployRuns"/> answered: the HTTP status and the body (the run, the test or the refusal in words).</summary>
public sealed record DeployRunOutcome(int Status, JsonObject Body)
{
    public static DeployRunOutcome Error(int status, string message) => new(status, new JsonObject { ["error"] = message });
}

/// <summary>
/// The runs of what the console deployed from a build (Design: docs/publishing.md#runs-of-a-deployed-build): a run of a manual task of the version a deploy staged, or of any task of the version the channel serves; a run and the steps it
/// recorded; the Runner's test of a slot's binding; and a Store model's approved contract, after the Gateways the console manages were asked who reads what it drops. Each call names a deploy the console keeps (<see cref="DeployStore"/>):
/// the deploy says the Runner, the package, the channel and the version, so the screens of a Workspace project never ask the Workspace server where a build went, and the Workspace server never calls the Runner. The calls go to the
/// Runner as the signed-in person (<see cref="ConsolePromotions.LinkFor"/>), who must hold the role the Runner asks for there (operator to run, administrator to test a login or activate); of the Workspace server the console asks only
/// that the person may read the project the deploy came from (<see cref="ConsoleDeploys.ProjectRefusalAsync"/>).
/// </summary>
public sealed class ConsoleDeployRuns(ConsoleDeploys deploys, ConsoleInstances instances, ConsoleRelay relay, ConsolePromotions promotions, AuditLog audit)
{
    /// <summary>The audit verbs (the words of the Workspace server's own run routes, in the form an audit verb takes).</summary>
    public const string RunVerb = "build.run";
    public const string BindingTestVerb = "build.binding_test";
    public const string ContractVerb = "build.contract";
    public const string AllowConsumerVerb = "build.contract.allow_consumer";

    private sealed class Refusal(int status, string message) : Exception(message)
    {
        public int Status { get; } = status;
    }

    /// <summary>A deploy the calls run against: the record, the Runner it went to and the Workspace server it came from.</summary>
    private sealed record Deployed(DeployRecord Record, string Package, string Channel, int Version, RegisteredInstance Runner, DeployTarget Target, RegisteredInstance WorkspaceServer)
    {
        public string What(string task, string version) => $"{Record.WorkspaceServer}/{Record.Project}/{Record.Workspace}/{Record.Build}: {Package}/{Channel}/{task} {version} on {Runner.Name}";
    }

    /// <summary>
    /// The deploy <paramref name="deployId"/>, when it put a version of a package on a channel of a Runner the console still manages, and the Workspace server it came from lets the person read its project; a refusal in words
    /// otherwise.
    /// </summary>
    private async Task<Deployed> FindAsync(string deployId, RelayPerson person, CancellationToken ct)
    {
        var record = deploys.Store.Get(deployId) ?? throw new Refusal(404, $"The console has no deploy '{deployId}'.");
        var workspaceServer = instances.Registry.Find(record.WorkspaceServer) ?? throw new Refusal(404, $"The Workspace server {record.WorkspaceServer} of this deploy is not registered any more.");
        if (await deploys.ProjectRefusalAsync(workspaceServer, record.Project, person, ct) is { } unreadable) throw new Refusal(unreadable.Status, unreadable.Error);
        if (record.Outcome != AuditEvent.Ok)
            throw new Refusal(409, $"This deploy was refused ({record.Error}), so nothing of it is on {record.TargetName}. Publish the build again.");
        if (record.Product != DeployProducts.Runner || record.DefinitionName is not { Length: > 0 } package || record.Channel is not { Length: > 0 } channel || record.Version is not { } version)
            throw new Refusal(409, $"This deploy sent {record.Item} to {record.TargetName}, which is not a package on a channel of a Runner; only a Runner runs a task.");
        var runner = instances.Registry.Find(record.Target) ?? throw new Refusal(404, $"The Runner {record.TargetName} of this deploy is not registered any more.");
        var target = new DeployTarget(runner.Name, runner.Url.ToString().TrimEnd('/'), runner.InstanceId, DeployProducts.Runner);
        return new Deployed(record, package, channel, version, runner, target, workspaceServer);
    }

    private RunnerCalls CallsOf(Deployed deployed, RelayPerson person) =>
        new(promotions.LinkFor(deployed.Runner.InstanceId, person) ?? throw new Refusal(404, $"The Runner {deployed.Runner.Name} of this deploy is not registered any more."), deployed.Target);

    private static string Actor(RelayPerson person) => person.Name ?? person.Id;

    /// <summary>
    /// Starts <paramref name="task"/> with <paramref name="inputs"/>: of the version the deploy staged (a manual-only task, such as a Sync set's <c>provision</c> or a Store model's <c>deploy</c>), or with <paramref name="served"/> of the
    /// version the deploy's channel serves now (a replication's drain, a refresh). An approved <c>contract</c> goes through <see cref="ContractAsync"/>, which asks the Gateways first.
    /// </summary>
    public async Task<DeployRunOutcome> StartAsync(string deployId, string task, IReadOnlyDictionary<string, string> inputs, bool served, RelayPerson person, CancellationToken ct, bool answer = false)
    {
        if (task == ContractPlans.Task && inputs.TryGetValue("approve", out var approval) && approval.Trim().Length > 0)
            return DeployRunOutcome.Error(409, "An approved contract is run through .../contract, which checks with the Gateways the console manages that no published consumer reads what it drops. Run contract here without \"approve\" to read its plan.");
        Deployed? deployed = null;
        var steps = new List<string>();
        try
        {
            deployed = await FindAsync(deployId, person, ct);
            var calls = CallsOf(deployed, person);
            var run = served
                ? await calls.RunServedTaskAsync(deployed.Package, deployed.Channel, task, inputs, Actor(person), steps.Add, ct)
                : await calls.RunStagedTaskAsync(deployed.Package, deployed.Channel, deployed.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), task, inputs, Actor(person), steps.Add, ct);
            Audit(person, RunVerb, deployed, task, served, AuditEvent.Ok, new { run = (string?)run["run_id"] });
            if (answer && Guid.TryParse((string?)run["run_id"], out var started))
                return await WaitForAnswerAsync(deployed, calls, started, person, AnswerWait, ct);
            return new(202, new JsonObject { ["steps"] = Steps(steps), ["run"] = run, ["deploy"] = ConsoleDeploys.Describe(deployed.Record) });
        }
        catch (Exception ex) when (ex is Refusal or DeployException)
        {
            if (deployed is not null) Audit(person, RunVerb, deployed, task, served, AuditEvent.Refused, new { error = ex.Message });
            return new(StatusOf(ex), new JsonObject { ["error"] = ex.Message, ["steps"] = Steps(steps) });
        }
    }

    /// <summary>How long a run started for its answer is waited for before the call answers that it is still going.</summary>
    public static readonly TimeSpan AnswerWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The answer of the run <paramref name="runId"/> the person started on the deploy's Runner (the rows of a preview, a strategy's candidates), taken once from the Runner's memory and passed on, never kept by the console: not in its
    /// audit, its log or the deploy's record (Design: docs/authoring-metadata.md#rows-and-trials-are-answered-never-kept).
    /// </summary>
    public async Task<DeployRunOutcome> AnswerAsync(string deployId, Guid runId, RelayPerson person, CancellationToken ct)
    {
        try
        {
            var deployed = await FindAsync(deployId, person, ct);
            return await WaitForAnswerAsync(deployed, CallsOf(deployed, person), runId, person, TimeSpan.Zero, ct);
        }
        catch (Exception ex) when (ex is Refusal or DeployException) { return DeployRunOutcome.Error(StatusOf(ex), ex.Message); }
    }

    /// <summary>
    /// Asks the Runner for the run's answer until it is there or <paramref name="wait"/> is over: 200 <c>done</c> with <c>answer</c>; 202 <c>running</c>; 422 <c>failed</c> with <c>error</c> (the run failed, or its answer is an
    /// error, which says why in words only the person sees); 410 <c>gone</c> (taken already, or not taken in time).
    /// </summary>
    private async Task<DeployRunOutcome> WaitForAnswerAsync(Deployed deployed, RunnerCalls calls, Guid runId, RelayPerson person, TimeSpan wait, CancellationToken ct)
    {
        var until = DateTimeOffset.UtcNow + wait;
        while (true)
        {
            var (status, body) = await calls.AnswerOfAsync(runId, Actor(person), ct);
            var state = (string?)body["state"];
            if (state != "running" || DateTimeOffset.UtcNow >= until)
            {
                var given = body["answer"]?.DeepClone();
                var error = given is JsonObject failed && failed["error"] is JsonValue reason ? (string?)reason : (string?)body["error"];
                var failedAnswer = given is JsonObject o && o["error"] is JsonValue;
                var outcome = new JsonObject
                {
                    ["state"] = failedAnswer ? "failed" : state, ["run_id"] = runId.ToString("D"), ["runner"] = deployed.Runner.InstanceId, ["runner_name"] = deployed.Runner.Name, ["channel"] = deployed.Channel,
                    ["production"] = deployed.Channel == ConsoleCatalogRead.Production, ["link"] = $"/servers/{Uri.EscapeDataString(deployed.Runner.InstanceId)}/runs?run={runId:D}",
                };
                if (state == "done" && !failedAnswer) outcome["answer"] = given;
                else if (error is not null) outcome["error"] = error;
                return new(failedAnswer ? 422 : status, outcome);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(400), ct);
        }
    }

    /// <summary>The run <paramref name="runId"/> on the deploy's Runner, with the steps named <paramref name="step"/> its task recorded while it ran; 404 when the Runner has no such run.</summary>
    public async Task<DeployRunOutcome> RunAsync(string deployId, Guid runId, string? step, RelayPerson person, CancellationToken ct)
    {
        try
        {
            var deployed = await FindAsync(deployId, person, ct);
            return await CallsOf(deployed, person).RunOfAsync(deployed.Package, deployed.Channel, runId, string.IsNullOrWhiteSpace(step) ? null : step, ct) is { } found
                ? new(200, new JsonObject { ["run"] = found })
                : DeployRunOutcome.Error(404, $"{deployed.Runner.Name} has no run {runId}.");
        }
        catch (Exception ex) when (ex is Refusal or DeployException) { return DeployRunOutcome.Error(StatusOf(ex), ex.Message); }
    }

    /// <summary>
    /// The newest run of <paramref name="task"/> in the deploy's channel (of any version: a preflight before activation, or a Re-check of the version served later), with the steps named <paramref name="step"/> it recorded; <c>run</c> is
    /// null when the task never ran there. What a screen shows as the newest check of a channel.
    /// </summary>
    public async Task<DeployRunOutcome> NewestAsync(string deployId, string task, string? step, RelayPerson person, CancellationToken ct)
    {
        try
        {
            var deployed = await FindAsync(deployId, person, ct);
            var found = await CallsOf(deployed, person).NewestRunAsync(deployed.Package, deployed.Channel, task, string.IsNullOrWhiteSpace(step) ? null : step, ct);
            return new(200, new JsonObject
            {
                ["package"] = deployed.Package, ["channel"] = deployed.Channel, ["task"] = task, ["runner"] = deployed.Runner.InstanceId, ["runner_name"] = deployed.Runner.Name,
                ["link"] = found is null ? null : $"/servers/{Uri.EscapeDataString(deployed.Runner.InstanceId)}/runs?run={found["id"]?.ToJsonString().Trim('"')}",
                ["run"] = found,
            });
        }
        catch (Exception ex) when (ex is Refusal or DeployException) { return DeployRunOutcome.Error(StatusOf(ex), ex.Message); }
    }

    /// <summary>The Runner's test of the login the deploy's channel binds to <paramref name="slot"/> (<paramref name="role"/>: default, read or write), tried on the Runner; no value comes back.</summary>
    public async Task<DeployRunOutcome> TestBindingAsync(string deployId, string slot, string role, RelayPerson person, CancellationToken ct)
    {
        Deployed? deployed = null;
        try
        {
            deployed = await FindAsync(deployId, person, ct);
            var answer = await CallsOf(deployed, person).TestBindingAsync(deployed.Package, deployed.Channel, slot, role, ct);
            Audit(person, BindingTestVerb, deployed, slot, false, (bool)answer["ok"]! ? AuditEvent.Ok : AuditEvent.Failed, new { step = (string?)answer["step"] });
            return new(200, new JsonObject { ["test"] = answer });
        }
        catch (Exception ex) when (ex is Refusal or DeployException)
        {
            if (deployed is not null) Audit(person, BindingTestVerb, deployed, slot, false, AuditEvent.Refused, new { error = ex.Message });
            return DeployRunOutcome.Error(StatusOf(ex), ex.Message);
        }
    }

    /// <summary>
    /// Activates the version a deploy staged in production for its preflight (<see cref="ConsoleDeployPreflight"/>), once the preflight has ended: with no error, or with <paramref name="confirm"/> when it found errors. The Runner keeps the
    /// confirmation in its record of the activation. A deploy that waited for no preflight, or whose version is active already, is refused in words.
    /// </summary>
    public async Task<DeployRunOutcome> ActivateAsync(string deployId, bool confirm, RelayPerson person, CancellationToken ct)
    {
        Deployed? deployed = null;
        try
        {
            deployed = await FindAsync(deployId, person, ct);
            JsonObject answer;
            try { answer = JsonNode.Parse(deployed.Record.Answer ?? "{}") as JsonObject ?? []; }
            catch (JsonException) { answer = []; }
            if (answer["preflight"] is not JsonObject before || (bool?)before["declared"] != true)
                throw new Refusal(409, $"This deploy did not stage version {deployed.Version} of {deployed.Package} for a preflight, so the console has nothing of it to activate. A version is activated by the deploy, or on the Runner's Packages screen.");
            if ((string?)before["activation"] is "activated" or "already-served")
                throw new Refusal(409, $"Version {deployed.Version} of {deployed.Package} was activated in {deployed.Channel} already.");
            var calls = CallsOf(deployed, person);
            JsonObject report;
            if ((string?)before["run"] is { } runText && Guid.TryParse(runText, out var run))
                report = ConsoleDeployPreflight.Report(run, await calls.RunOfAsync(deployed.Package, deployed.Channel, run, DbDataKit.Runner.Preflight.StepName, ct), deployed.Runner.InstanceId);
            else
                // the preflight could not be started: nothing is known of what it would find, which only a confirmation lets past
                report = new JsonObject
                {
                    ["run"] = null, ["state"] = "finished", ["errors"] = 1, ["warnings"] = 0,
                    ["findings"] = new JsonArray(new JsonObject { ["severity"] = DbDataKit.Runner.Preflight.Error, ["message"] = $"No preflight ran: {(string?)before["error"]}", ["about"] = null }),
                };
            report["declared"] = true;
            var activation = await ConsoleDeployPreflight.ActivateAsync(calls, deployed.Package, deployed.Channel, deployed.Version, report, confirm, person, ct);
            deploys.Settle(deployed.Record, answer, report, activation, confirm, person, deployed.Runner);
            var body = new JsonObject { ["activation"] = activation.State, ["message"] = activation.Message, ["active"] = activation.Active, ["preflight"] = report.DeepClone(), ["deploy"] = ConsoleDeploys.Describe(deploys.Store.Get(deployed.Record.Id)!) };
            if (activation.Status >= 400) body["error"] = activation.Message;
            return new(activation.Status, body);
        }
        catch (Exception ex) when (ex is Refusal or DeployException)
        {
            if (deployed is not null) Audit(person, ConsoleDeployPreflight.ActivateVerb, deployed, DbDataKit.Runner.Preflight.TaskName, false, AuditEvent.Refused, new { error = ex.Message });
            return DeployRunOutcome.Error(StatusOf(ex), ex.Message);
        }
    }

    /// <summary>
    /// Runs the contract plan of a Store model a person read and approved: <paramref name="planRun"/> is the run of <c>contract</c> without an approval whose report has the hash <paramref name="approve"/>. The columns that report drops are
    /// checked against the lineage of every Gateway the console manages, read as the person: a published consumer that reads one refuses the step by name, unless <paramref name="allowed"/> names it (<c>&lt;name&gt;@&lt;channel&gt;</c>,
    /// recorded in the audit); a Gateway that does not answer refuses it too (<c>gateway:&lt;name&gt;</c> allows that). Then <c>contract</c> runs with that hash on the version the deploy staged, which runs exactly that plan or nothing.
    /// </summary>
    public async Task<DeployRunOutcome> ContractAsync(string deployId, Guid planRun, string approve, IReadOnlyCollection<string> allowed, RelayPerson person, CancellationToken ct)
    {
        Deployed? deployed = null;
        var steps = new List<string>();
        try
        {
            deployed = await FindAsync(deployId, person, ct);
            var calls = CallsOf(deployed, person);
            if (await calls.RunOfAsync(deployed.Package, deployed.Channel, planRun, ContractPlans.ReportStep, ct) is not { } read || (string?)read["task"] != ContractPlans.Task)
                throw new Refusal(409, $"{deployed.Runner.Name} has no run {planRun} of contract of {deployed.Package}. Read the plan again.");
            var reading = ContractPlans.Read(read, planRun, approve);
            if (reading.Plan is not { } plan)
            {
                var refused = new JsonObject { ["error"] = reading.Refusal };
                if (reading.Blocked) refused["hash"] = approve;
                Audit(person, ContractVerb, deployed, ContractPlans.Task, false, AuditEvent.Refused, new { error = reading.Refusal });
                return new(409, refused);
            }

            var (graphs, unreachable) = plan.Dropped.Count == 0 ? ([], []) : await GatewayCalls.LineageAsync(await GatewaysAsync(person, ct), ct);
            var verdict = ContractPlans.CheckConsumers(plan, graphs, unreachable, allowed);
            if (verdict.Refusals.Count > 0)
            {
                Audit(person, ContractVerb, deployed, ContractPlans.Task, false, AuditEvent.Refused, new { consumers = verdict.Refusals });
                return new(409, new JsonObject { ["error"] = string.Join(" ", verdict.Refusals), ["consumers_refused"] = new JsonArray([.. verdict.Refusals.Select(r => (JsonNode?)r)]), ["hash"] = approve });
            }
            foreach (var used in verdict.AllowancesUsed) Audit(person, AllowConsumerVerb, deployed, ContractPlans.Task, false, AuditEvent.Ok, new { allowed = used });

            var run = await calls.RunStagedTaskAsync(deployed.Package, deployed.Channel, deployed.Version.ToString(System.Globalization.CultureInfo.InvariantCulture), ContractPlans.Task, ContractPlans.Inputs(plan, approve), Actor(person), steps.Add, ct);
            Audit(person, ContractVerb, deployed, ContractPlans.Task, false, AuditEvent.Ok, new { run = (string?)run["run_id"], approve = approve[..Math.Min(12, approve.Length)] });
            return new(202, new JsonObject { ["steps"] = Steps(steps), ["run"] = run, ["checked"] = plan.Dropped.Count, ["gateways"] = graphs.Count });
        }
        catch (Exception ex) when (ex is Refusal or DeployException)
        {
            if (deployed is not null) Audit(person, ContractVerb, deployed, ContractPlans.Task, false, AuditEvent.Refused, new { error = ex.Message });
            return new(StatusOf(ex), new JsonObject { ["error"] = ex.Message, ["steps"] = Steps(steps) });
        }
    }

    /// <summary>
    /// The Gateways the console manages, each read through a link as the person. A registered server whose product the console does not know (it has not answered since the console started) is listed too: it may be a Gateway, and a
    /// contract that cannot ask it is refused with its name, which <c>gateway:&lt;name&gt;</c> allows.
    /// </summary>
    private async Task<IReadOnlyList<GatewayLineageSource>> GatewaysAsync(RelayPerson person, CancellationToken ct)
    {
        var sources = new List<GatewayLineageSource>();
        foreach (var instance in instances.Registry.All())
        {
            var product = await relay.ProductOfAsync(instance, ct);
            if (product == ServerProducts.Gateway)
                sources.Add(new GatewayLineageSource(instance.Name, instance.Url.ToString().TrimEnd('/'), _ => Task.FromResult(promotions.LinkFor(instance.InstanceId, person)
                    ?? throw new DeployException($"{instance.Name} is not registered any more.", 404))));
            else if (product is null)
                sources.Add(new GatewayLineageSource(instance.Name, instance.Url.ToString().TrimEnd('/'), _ => throw new DeployException($"{instance.Name} is not answering, so the console does not know whether it is a Gateway.", 502)));
        }
        return sources;
    }

    private static int StatusOf(Exception ex) => (ex is Refusal r ? r.Status : ((DeployException)ex).Status) is var code and >= 400 and < 600 ? code : 502;

    private static JsonArray Steps(List<string> steps) => new([.. steps.Select(s => (JsonNode?)s)]);

    private void Audit(RelayPerson person, string verb, Deployed deployed, string what, bool served, string outcome, object detail) =>
        audit.Record(new AuditEvent("user", person.Id, verb, outcome, TargetKind: "server", TargetId: deployed.Runner.InstanceId,
            Detail: JsonSerializer.Serialize(new { deploy = deployed.Record.Id, what = deployed.What(what, served ? "as served" : "v" + deployed.Version), result = detail })));
}
