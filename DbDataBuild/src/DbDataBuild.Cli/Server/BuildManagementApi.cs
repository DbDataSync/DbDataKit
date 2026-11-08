using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli.Mcp;
using DbDataBuild.Tui.Model;
using DbDataBuild.Cli.Runner;
using DbDataKit.Scheduling;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// What a Build server offers over its management API (plan 13, R01 and R03): the commands of a project, run in a child process with the same arguments and the same JSON document the command line gives.
/// <list type="bullet">
/// <item><c>GET /api/v1/projects</c>: the projects served (one for now).</item>
/// <item><c>GET /api/v1/projects/{project}/commands</c>: the commands on offer, each with the role it needs.</item>
/// <item><c>POST /api/v1/projects/{project}/commands/{command}</c>: runs one; the body is its inputs as a JSON object (the command's name with underscores, as the MCP server names it). The answer is <c>{exit, document}</c>, or <c>{exit, text}</c> when the command printed no document.</item>
/// </list>
/// Which commands, and which of their options, follow the rules the MCP server and the local web page use (<see cref="ToolSurface"/>): no option that shows values, no path outside the project. The role a command needs
/// comes from its effect class (<see cref="ManagementAuthorization"/>): a viewer reads, an author changes the project's files, an operator refreshes data, an approver applies a plan to a target, an admin changes the tracking tables. The caller's
/// token (<see cref="ManagementAuthentication"/>) has to carry it for this project. Approving is applying: the person who holds the approver role and applies a plan is the approver, and the command records them (and the console they came through) in the
/// tracking tables as the one who applied it (<see cref="Actor"/>).
/// </summary>
internal static class BuildManagementApi
{
    /// <summary>The commands a read-only server offers: those that change nothing, not even the project's own files.</summary>
    public static ToolSurface ReadOnlySurface(string projectRoot, IEnumerable<CommandInfo> all) =>
        new(projectRoot, all.Where(c => c.Impact == Impact.None), withholdWriteFlags: true);

    /// <summary>
    /// The commands a server offers by default: all of them but the ones that start a user interface on the server's own terminal or browser. Whether a call may run is the role its effect class needs, checked on every call; the
    /// options that show values and the paths outside the project stay refused. A server started with <c>--read-only</c> offers <see cref="ReadOnlySurface"/> instead.
    /// </summary>
    public static ToolSurface FullSurface(string projectRoot, IEnumerable<CommandInfo> all) =>
        new(projectRoot, all.Where(c => c.Name is not ("ui terminal" or "ui mcp")), alsoWithheld: ["connection refresh --check-results"]);

    /// <summary>The person a call is for, as the tracking tables record them: "Ada via Main console".</summary>
    internal static string ActorOf(ValidatedToken token) => $"{token.SubjectName ?? token.SubjectId} via {token.Client.Name}";

    /// <param name="projects">The projects served, read again on every call so one added or removed is served or gone at once.</param>
    /// <param name="commands">All the commands there are; each project is offered the read-only ones.</param>
    /// <param name="surfaceFor">Which of them a project is offered and how their inputs are checked; the read-only rules unless a test or a later slice says otherwise.</param>
    public static void Map(IEndpointRouteBuilder app, ProjectsFile projects, IReadOnlyList<CommandInfo> commands, IProjectCommandRunner runner, Func<string, IReadOnlyList<CommandInfo>, ToolSurface>? surfaceFor = null, BuildScheduler? scheduler = null, Workspaces? workspaces = null, WarmSessionRunner? workspaceRunner = null, ProxiesFile? proxies = null, ProxyPublisher? publisher = null, IPairingCodeStore? pairingCodes = null, string? clientName = null, ProjectCreator? creator = null, string? serverDirectory = null, Func<ScheduleRunnerLink, DbDataBuild.Cli.Runner.IRunnerTaskControl>? runnerControl = null, IAuditLog? audit = null, Func<ScheduleRunnerLink, DbDataBuild.Cli.Runner.IRunnerSchedule>? runnerSchedule = null, string? runnerClientDirectory = null)
    {
        surfaceFor ??= FullSurface;
        var specs = CommandSpecs.All.ToDictionary(s => s.Name);

        app.MapGet("/api/v1/projects", () => Results.Json(new
        {
            projects = projects.All().Select(p => new { id = p.Id, name = p.Id, source = p.IsRemote ? "git" : "path", branch = p.Branch }),
        }));

        // the connections of every hosted project and who uses them (plan 28c, E23): names, engines, secret references, never a value
        app.MapGet("/api/v1/connections", (HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Results.Json(new { error = "This needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Json(DbDataKit.Connections.Inventory.ConnectionInventoryJson.Write("dbdatabuild", BuildConnectionInventory.Of(projects.All().Select(p => (p.Id, projects.RootOf(p))))));
        });

        if (creator is not null) MapNewProjects(app, creator);

        // The schemas of the project's files and which file each describes, for the console's forms (the viewer role may read them: they are the tool's own, not the project's)
        // the lineage of every hosted project as one graph (plan 18, G09): a console joins it with the other servers' by identity
        if (serverDirectory is not null)
            app.MapGet("/api/v1/lineage", async (HttpContext context) =>
            {
                var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
                if (!decision.Allowed) return Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
                var (graph, problems) = await ServerLineage.MergedAsync(serverDirectory, null, context.RequestAborted, BuildServer.InstanceId(Path.Combine(serverDirectory, "server.yaml")));
                var document = DbDataKit.Lineage.LineageJson.Write(graph);
                document["problems"] = new System.Text.Json.Nodes.JsonArray(problems.Select(p => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(p)!).ToArray());
                return Results.Json(document);
            });

        // the results of the models' checks (plan 23a FQ17), in the form every server sends a console: Build evaluates after a refresh, so it cannot be asked to evaluate on request
        if (serverDirectory is not null)
        {
            var qualityState = new Lazy<StateDatabase>(() => BuildSchedulerSchema.Open(serverDirectory));
            app.MapGet("/api/v1/quality", (HttpContext context) =>
            {
                var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
                if (!decision.Allowed) return Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
                return Results.Json(QualityDocuments.Overview(new QualityStore(qualityState.Value), canRun: false,
                    runNote: "Build evaluates a model's checks after a refresh: run the refresh with `connection refresh <connection> <plan> --check-results <server directory>`."));
            });
            app.MapGet("/api/v1/quality/history", (HttpContext context, string? dataset, string? check, int? limit) =>
            {
                var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
                if (!decision.Allowed) return Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
                if (string.IsNullOrWhiteSpace(dataset) || string.IsNullOrWhiteSpace(check)) return Results.Json(new { error = "Say which check: `dataset` and `check`." }, statusCode: StatusCodes.Status400BadRequest);
                return Results.Json(QualityDocuments.History(new QualityStore(qualityState.Value), dataset, check, limit ?? QualityDocuments.HistoryLimit));
            });
        }

        app.MapGet("/api/v1/schemas", () => Results.Text(FileSchemas.Document().ToJsonString(), "application/json"));

        app.MapGet("/api/v1/projects/{project}/commands", (string project, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var projectId = hosted.Id;
            var surface = surfaceFor(projects.RootOf(hosted), commands);
            var token = context.ManagementToken();
            return Results.Json(new
            {
                commands = surface.Tools.Values.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => new
                {
                    tool = ToolSurface.ToolName(c),
                    name = c.Name,
                    purpose = c.Purpose,
                    effect = c.Effect,
                    role = specs.TryGetValue(c.Name, out var spec) ? ManagementAuthorization.RoleFor(spec.Effect).ToString().ToLowerInvariant() : null,
                    allowed = specs.TryGetValue(c.Name, out var s) && ManagementAuthorization.Authorize(token, s, projectId).Allowed,
                }),
            });
        });

        if (workspaces is not null) MapWorkspaces(app, projects, workspaces, workspaceRunner);
        // T11: the code a console asks for so that another server can pair with this one as a publisher
        if (pairingCodes is not null)
            app.MapPost(PairingCodeIssuer.Path, async (HttpContext context) =>
            {
                using var reader = new StreamReader(context.Request.Body);
                var (status, body) = PairingCodeIssuer.Issue(pairingCodes, ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed, await reader.ReadToEndAsync(context.RequestAborted));
                return Results.Json(body, statusCode: status);
            });
        if (proxies is not null && publisher is not null) MapProxies(app, projects, proxies, publisher, clientName ?? "Build server");

        // who schedules a plan (plan 28c E09) is the server's file; for a plan the runner owns this server forwards to the runner (E10, QE5)
        BuildScheduleSettings OwnersNow() => serverDirectory is null ? BuildScheduleSettings.Default : BuildScheduleSettings.Read(serverDirectory);
        DbDataBuild.Cli.Runner.IRunnerTaskControl? ControlOf(BuildScheduleSettings owners) => owners.Runner is { } link
            ? (runnerControl ?? (l => DbDataBuild.Cli.Runner.ClientRunnerTaskControl.For(l, Environment.GetEnvironmentVariable)))(link)
            : null;

        // the scheduled plans of a project, whether or not this server runs them (the scheduler is opt-in): their schedule, the next time each is due and how the last run went.
        // A plan the runner owns carries owner "runner" and the runner's own row (pause, next due, last run): what this server's scheduler recorded is not what runs it.
        app.MapGet("/api/v1/projects/{project}/schedule", async (string project, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var now = TimeProvider.System.GetUtcNow();
            BuildScheduleSettings owners;
            try { owners = OwnersNow(); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
            var jobs = scheduler is not null
                ? scheduler.Jobs(hosted)
                : [.. RefreshJobs.Discover(projects.RootOf(hosted)).Select(j => (j, (DateTimeOffset?)DbDataKit.Scheduling.ScheduleEvaluator.NextAfter(j.Schedule, now), (JobState?)null))];
            var control = ControlOf(owners);
            var rows = new List<object>();
            foreach (var j in jobs)
            {
                var owner = owners.OwnerOf(hosted.Id, j.Job.Subject);
                var (pausedHere, pausedByHere) = (scheduler?.PausedBy(hosted, j.Job) is not null, scheduler?.PausedBy(hosted, j.Job)?.By);
                if (owner != TaskOwners.Runner || control is null)
                {
                    rows.Add(new
                    {
                        connection = j.Job.Connection, plan = j.Job.Plan, schedule = j.Job.Schedule.Cron, time_zone = j.Job.Schedule.TimeZoneId,
                        owner,
                        next_due = j.NextDue, last_started = j.State?.LastStarted, last_exit = j.State?.LastExit,
                        paused = pausedHere, paused_by = pausedByHere,
                    });
                    continue;
                }

                // the runner schedules this plan: the row is the runner's (a runner that cannot be asked leaves the row saying so, and nothing of this server's own record stands in for it)
                object? last = null;
                DateTimeOffset? nextDue = null, lastStarted = null;
                int? lastExit = null;
                bool paused = false;
                string? pausedBy = null, runnerError = null;
                try
                {
                    var status = await control.StatusAsync(RunnerNaming.PackageOf(hosted.Id), RunnerNaming.TaskOf(j.Job.Connection, j.Job.Plan), context.RequestAborted);
                    if (status is null) runnerError = "the runner lists no such task (publish the project, or the channel does not serve it).";
                    else
                    {
                        (nextDue, paused, pausedBy, lastStarted) = (status.NextDue, status.Paused, status.PausedBy, status.LastRun?.Started);
                        // the console's monitoring row reads last_exit: 0 for a run that succeeded, 1 for one that ended any other way, none while it is under way (the outcome itself is in runner.last_run)
                        lastExit = status.LastRun?.Outcome switch { null or "running" => null, "succeeded" => 0, _ => 1 };
                        last = status.LastRun is null ? null : new { id = status.LastRun.Id, outcome = status.LastRun.Outcome, started = status.LastRun.Started, finished = status.LastRun.Finished, attempt = status.LastRun.Attempt, rows = status.LastRun.Rows };
                    }
                }
                catch (DbDataKit.Runner.Client.RunnerClientException ex) { runnerError = ex.Message; }
                rows.Add(new
                {
                    connection = j.Job.Connection, plan = j.Job.Plan, schedule = j.Job.Schedule.Cron, time_zone = j.Job.Schedule.TimeZoneId,
                    owner,
                    next_due = nextDue, last_started = lastStarted, last_exit = lastExit,
                    paused, paused_by = pausedBy,
                    runner = new { server = owners.Runner!.Server, channel = owners.Runner.Channel, last_run = last, error = runnerError },
                });
            }

            return Results.Json(new { enabled = scheduler is not null, jobs = rows });
        });

        // Pausing, resuming and running a scheduled plan now are an operator's (running a plan changes target data). For a plan this server's scheduler owns they need the scheduler: without it nothing is queued, so there is nothing to
        // pause or start. For a plan the runner owns the call is forwarded to the runner (a server that runs no scheduler forwards too), and what the runner answers is what the caller is told; the audit line names the caller.
        foreach (var action in new[] { "pause", "resume", "run" })
            app.MapPost("/api/v1/projects/{project}/schedule/{connection}/{plan}/" + action, async (string project, string connection, string plan, HttpContext context) =>
            {
                if (projects.Find(project) is not { } hosted) return Results.NotFound();
                var token = context.ManagementToken();
                var decision = ManagementAuthorization.Authorize(token, ServerRole.Operator, hosted.Id);
                if (!decision.Allowed) return Results.Json(new { error = "This needs the operator role." }, statusCode: StatusCodes.Status403Forbidden);
                BuildScheduleSettings owners;
                try { owners = OwnersNow(); }
                catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
                var owner = owners.OwnerOf(hosted.Id, connection, plan);
                if (owner == TaskOwners.Runner && ControlOf(owners) is { } control)
                {
                    if ((scheduler?.Find(hosted, connection, plan) ?? RefreshJobs.Discover(projects.RootOf(hosted)).FirstOrDefault(j => j.Connection == connection && j.Plan == plan)) is null) return Results.NotFound();
                    var (package, task) = (RunnerNaming.PackageOf(hosted.Id), RunnerNaming.TaskOf(connection, plan));
                    var named = $"{owners.Runner!.Server} ({package}/{owners.Runner.Channel}/{task})";
                    var actor = token.SubjectName ?? token.SubjectId;
                    void Audit(string outcome) => audit?.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), "command", "dbdatabuild server", token.SubjectId, actor, false, "schedule." + action, BuildScheduleSettings.KeyOf(hosted.Id, connection, plan), outcome, "runner " + named));
                    try
                    {
                        switch (action)
                        {
                            case "pause": await control.PauseAsync(package, task, context.RequestAborted); Audit("ok"); return Results.Json(new { paused = true, owner, runner = owners.Runner.Server });
                            case "resume": await control.ResumeAsync(package, task, context.RequestAborted); Audit("ok"); return Results.Json(new { paused = false, owner, runner = owners.Runner.Server });
                            default:
                                if (await control.RunNowAsync(package, task, context.RequestAborted) is not { } runId)
                                {
                                    Audit("refused: queued or running");
                                    return Results.Json(new { error = $"A run of this plan is already queued or going on the runner {named}." }, statusCode: StatusCodes.Status409Conflict);
                                }

                                Audit("ok");
                                return Results.Json(new { queued = true, run_id = runId, owner, runner = owners.Runner.Server });
                        }
                    }
                    catch (DbDataKit.Runner.Client.RunnerClientException ex)
                    {
                        Audit("failed");
                        return Results.Json(new { error = $"This plan is scheduled by the runner {named}, which did not take the {action}: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway);
                    }
                }

                if (scheduler is null) return Results.Json(new { error = "This server does not run schedules (start it with --scheduler)." }, statusCode: StatusCodes.Status409Conflict);
                if (scheduler.Find(hosted, connection, plan) is not { } job) return Results.NotFound();
                switch (action)
                {
                    case "pause": scheduler.Pause(hosted, job, token.SubjectName ?? token.SubjectId); return Results.Json(new { paused = true });
                    case "resume": scheduler.Resume(hosted, job); return Results.Json(new { paused = false });
                    default:
                        return scheduler.RunNow(hosted, job)
                            ? Results.Json(new { queued = true })
                            : Results.Json(new { error = "A run of this plan is already queued or going." }, statusCode: StatusCodes.Status409Conflict);
                }
            });

        // the runners this server is paired with as a publisher (plan 33, UE05): what `project publish --server` takes by name, with the roles each pairing allows. Never a key or a code; a viewer may read it.
        app.MapGet("/api/v1/runners", (HttpContext context) =>
        {
            if (!ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer).Allowed) return Results.Json(new { error = "This needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden);
            try
            {
                var store = new DbDataKit.Runner.Client.RunnerClientStore(runnerClientDirectory ?? DbDataKit.Runner.Client.RunnerClientStore.DefaultDirectory(Environment.GetEnvironmentVariable));
                return Results.Json(new { runners = store.All().Select(r => new { name = r.Name, url = r.Url, instance_id = r.InstanceId, roles = r.Roles, paired_at = r.PairedAt }) });
            }
            catch (InvalidDataException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        // Who schedules a plan (plan 33, UE06): the moves `server schedule observe|cutover|rollback` make, as one call. The order of the steps is BuildOwnership's (this server's file and the runner's record move together, never both scheduling); an operator's call.
        app.MapPut("/api/v1/projects/{project}/schedule/{connection}/{plan}/owner", async (string project, string connection, string plan, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var token = context.ManagementToken();
            if (!ManagementAuthorization.Authorize(token, ServerRole.Operator, hosted.Id).Allowed) return Results.Json(new { error = "Moving a plan's schedule needs the operator role." }, statusCode: StatusCodes.Status403Forbidden);
            if (serverDirectory is null) return Results.Json(new { error = "This server keeps no settings to record an owner in." }, statusCode: StatusCodes.Status409Conflict);
            JsonObject? body;
            try { body = await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { body = null; }
            string? Text(string name) => body?[name] is JsonValue v && v.TryGetValue<string>(out var t) && t.Length > 0 ? t : null;
            var owner = Text("owner");
            if (!TaskOwners.IsOwner(owner)) return Results.Json(new { error = $"The body is a JSON object with \"owner\": one of {string.Join(", ", TaskOwners.All)} (and optionally \"reason\", \"wait_seconds\", \"runner\" and \"channel\")." }, statusCode: StatusCodes.Status400BadRequest);
            TimeSpan? wait = null;
            if (body?["wait_seconds"] is { } waitNode)
            {
                if (waitNode is not JsonValue wv || !wv.TryGetValue<double>(out var seconds) || seconds < 0 || seconds > 3600) return Results.Json(new { error = "wait_seconds is a number from 0 to 3600." }, statusCode: StatusCodes.Status400BadRequest);
                wait = TimeSpan.FromSeconds(seconds);
            }

            BuildScheduleSettings settings;
            try { settings = BuildScheduleSettings.Read(serverDirectory); }
            catch (InvalidOperationException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
            var link = settings.Runner;
            if (Text("runner") is not null || Text("channel") is not null)
                link = new ScheduleRunnerLink(Text("runner") ?? link?.Server ?? "", Text("channel") ?? link?.Channel ?? "", link?.ClientDirectory);
            if (link is null || link.Server.Length == 0 || link.Channel.Length == 0)
                return Results.Json(new { error = "Say where the runner is: \"runner\" (a paired runner's name) and \"channel\", or schedule.runner in this server's settings." }, statusCode: StatusCodes.Status400BadRequest);

            var actor = ActorOf(token);
            try
            {
                var schedule = (runnerSchedule ?? (l => new DbDataBuild.Cli.Runner.ClientRunnerSchedule(new DbDataKit.Runner.Client.RunnerPublishClient(new DbDataKit.Runner.Client.RunnerClientStore(l.ClientDirectory ?? runnerClientDirectory ?? DbDataKit.Runner.Client.RunnerClientStore.DefaultDirectory(Environment.GetEnvironmentVariable)), Environment.GetEnvironmentVariable), l)))(link);
                var ownership = new DbDataBuild.Cli.Runner.BuildOwnership(serverDirectory, projects, BuildSchedulerSchema.Open(serverDirectory), TimeProvider.System, schedule, audit ?? new FileAuditLog(Path.Combine(serverDirectory, "management-audit.log")), actor);
                var reason = Text("reason");
                var change = owner switch
                {
                    TaskOwners.Observe => await ownership.ObserveAsync(hosted.Id, connection, plan, link, reason, context.RequestAborted),
                    TaskOwners.Runner => await ownership.CutOverAsync(hosted.Id, connection, plan, link, reason, wait, context.RequestAborted),
                    _ => await ownership.RollBackAsync(hosted.Id, connection, plan, link, reason, wait, context.RequestAborted),
                };
                return Results.Json(new { owner = change.To, from = change.From, runner = link.Server, channel = link.Channel, handled_up_to = change.HandledUpTo?.UtcDateTime.ToString("O"), paused_by = change.PausedBy, waited = change.Waited });
            }
            catch (DbDataBuild.Cli.Runner.OwnershipException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
            catch (DbDataKit.Runner.Client.RunnerClientException ex) { return Results.Json(new { error = $"The runner {link.Server} did not take it: {ex.Message}" }, statusCode: StatusCodes.Status502BadGateway); }
        });

        app.MapPost("/api/v1/projects/{project}/commands/{tool}", async (string project, string tool, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var projectId = hosted.Id;
            var projectRoot = projects.RootOf(hosted);
            var commandRunner = runner;
            if (context.Request.Query["workspace"] is { Count: > 0 } asked && workspaces is not null)
            {
                if (workspaces.Find(projectId, asked.ToString()) is not { } draft) return Results.Json(new { error = $"There is no workspace '{asked}' in this project." }, statusCode: StatusCodes.Status404NotFound);
                if (draft.Owner != context.ManagementToken().SubjectId) return Results.Json(new { error = "A workspace is its owner's." }, statusCode: StatusCodes.Status403Forbidden);
                projectRoot = workspaces.RootOf(draft);
                commandRunner = workspaceRunner ?? runner;
            }
            if (!Directory.Exists(projectRoot))
                return Results.Json(new { error = $"The project '{projectId}' has no working copy on this server yet. Run: dbdatabuild server project pull {projectId}" }, statusCode: StatusCodes.Status409Conflict);
            var surface = surfaceFor(projectRoot, commands);
            if (!surface.Tools.TryGetValue(tool, out var command) || !specs.TryGetValue(command.Name, out var spec))
                return Results.Json(new { error = $"`{tool}` is not a command this server runs." }, statusCode: StatusCodes.Status404NotFound);

            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), spec, projectId);
            if (!decision.Allowed)
            {
                if (command.Name == "connection refresh") RecordRefresh(audit, context.ManagementToken(), projectId, null, "refused: needs the " + decision.Needed.ToString().ToLowerInvariant() + " role", "");
                return Results.Json(new { error = $"`{command.Name}` needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
            }

            JsonObject arguments;
            try
            {
                arguments = context.Request.ContentLength is 0 or null && !context.Request.HasJsonContentType()
                    ? new JsonObject()
                    : await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted) ?? new JsonObject();
            }
            catch (JsonException)
            {
                return Results.Json(new { error = "The body is a JSON object of the command's inputs." }, statusCode: StatusCodes.Status400BadRequest);
            }

            if (!surface.TryBuildArguments(command, arguments, out var argv, out var problem))
                return Results.Json(new { error = problem }, statusCode: StatusCodes.Status400BadRequest);

            // a command that reads a connection (a viewer's) but writes the project's files when a flag says so (`project import --write`, `project model update --write`) needs the role of someone who changes files
            if (command.Impact == Impact.RepoFiles && WriteRules.Changes(command, flag => argv.Contains(flag), repoFiles: true))
            {
                var writing = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Author, projectId);
                if (!writing.Allowed) return Results.Json(new { error = $"Writing the project's files with `{command.Name}` needs the author role." }, statusCode: StatusCodes.Status403Forbidden);
            }

            // UE09: a refresh run from a screen evaluates the checks stored with the models that loaded and records the results where the console reads them, this server's own directory (the option is withheld from a caller, so
            // no one names another place). A dry run evaluates nothing.
            var isRefresh = command.Name == "connection refresh";
            if (isRefresh && serverDirectory is not null) { argv.Add("--check-results"); argv.Add(serverDirectory); }

            var result = await commandRunner.RunAsync(argv, projectRoot, ActorOf(context.ManagementToken()), context.RequestAborted);
            JsonNode? document = null;
            try { document = JsonNode.Parse(result.Out); } catch (JsonException) { }
            // a refresh changes the target's data: who ran which plan, and how it ended, is in the server's audit log (a refusal for a missing role is recorded too, above)
            if (isRefresh) RecordRefresh(audit, context.ManagementToken(), projectId, arguments, result.Exit == 0 ? "ok" : $"exit {result.Exit}", argv.Contains("--dry-run") ? "dry run" : "");
            return Results.Json(new JsonObject { ["exit"] = result.Exit, ["document"] = document, ["text"] = document is null ? (result.Out.Length > 0 ? result.Out : result.Err) : null });
        });
    }
    /// <summary>The audit line of a refresh run through the commands route: <c>refresh.run</c> on <c>project/connection/plan</c> (the plan is <c>default</c> when none was named), by the person the console vouched for.</summary>
    private static void RecordRefresh(IAuditLog? audit, ValidatedToken token, string project, JsonObject? arguments, string outcome, string detail)
    {
        if (audit is null) return;
        string Named(string key) => arguments?[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text : "";
        var (connection, plan) = (Named("connection"), Named("plan"));
        audit.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), "command", "dbdatabuild server", token.SubjectId, token.SubjectName ?? token.SubjectId, false, "refresh.run",
            $"{project}/{(connection.Length > 0 ? connection : "(default connection)")}/{(plan.Length > 0 ? plan : "default")}", outcome, detail));
    }

    /// <summary>
    /// New projects from templates (plan 13, W07): <c>GET /api/v1/templates</c> lists the templates built into the server, each with its name, description and file count, and says whether the caller may make a project (<c>can_create</c>);
    /// <c>POST /api/v1/projects</c> (<c>{"id", "template"}</c>) makes one. Making a project adds it to what the server hosts, so it needs the author role on the server itself (a role held in one project only does not reach it). The CLI's own
    /// <c>project create</c> cannot be run through the commands route, which keeps every path inside a project that already exists.
    /// </summary>
    private static void MapNewProjects(IEndpointRouteBuilder app, ProjectCreator creator)
    {
        app.MapGet("/api/v1/templates", (HttpContext context) => Results.Json(new
        {
            templates = TemplateStore.All.Select(t => new { name = t.Name, description = t.Description, files = t.Files.Count }),
            can_create = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Author).Allowed,
        }));

        app.MapPost("/api/v1/projects", async (HttpContext context) =>
        {
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, ServerRole.Author);
            if (!decision.Allowed) return Results.Json(new { error = "Making a project needs the author role on this server." }, statusCode: StatusCodes.Status403Forbidden);
            string? id, template;
            try
            {
                var body = await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted);
                (id, template) = (body?["id"]?.GetValue<string>(), body?["template"]?.GetValue<string>());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { (id, template) = (null, null); }
            if (id is null || template is null) return Results.Json(new { error = "The body is a JSON object with the project's \"id\" and the \"template\" to make it from." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var made = creator.Create(id, template, new DbDataKit.Config.Git.GitAuthor(token.SubjectName ?? token.SubjectId, $"{token.SubjectId}@dbdatabuild.local"));
                return Results.Json(new { id = made.Id, name = made.Id, source = "path", branch = (string?)null, template }, statusCode: StatusCodes.Status201Created);
            }
            catch (ProjectCreationException ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
        });
    }

    /// <summary>
    /// <c>GET|POST /api/v1/projects/{project}/workspaces</c> and <c>DELETE .../workspaces/{id}</c> (plan 13, W02). Listing is a viewer's; making one and abandoning one needs the author role, and only the owner abandons theirs.
    /// A command runs in a workspace when the call says <c>?workspace=&lt;id&gt;</c>, and only for its owner; without it the command runs in the project's own checkout, which is what the scheduler reads.
    /// </summary>
    private static void MapWorkspaces(IEndpointRouteBuilder app, ProjectsFile projects, Workspaces workspaces, WarmSessionRunner? sessions)
    {
        static object Describe(Workspace w, string? caller) => new { id = w.Id, name = w.Name, owner = w.OwnerName, mine = w.Owner == caller, branch = w.Branch, @base = w.Base, created = w.Created };
        static IResult Refused(AuthorizationDecision decision) => Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);

        app.MapGet("/api/v1/projects/{project}/workspaces", (string project, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, ServerRole.Viewer, hosted.Id);
            return decision.Allowed ? Results.Json(new { workspaces = workspaces.List(hosted.Id).Select(w => Describe(w, token.SubjectId)) }) : Refused(decision);
        });

        app.MapPost("/api/v1/projects/{project}/workspaces", async (string project, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, ServerRole.Author, hosted.Id);
            if (!decision.Allowed) return Refused(decision);
            string? name;
            try { name = (await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted))?["name"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { name = null; }
            if (name is null) return Results.Json(new { error = "The body is a JSON object with a \"name\"." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var made = workspaces.Create(hosted.Id, projects.RootOf(hosted), token.SubjectId, token.SubjectName ?? token.SubjectId, name);
                return Results.Json(Describe(made, token.SubjectId), statusCode: StatusCodes.Status201Created);
            }
            catch (ArgumentException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest); }
            catch (DbDataKit.Config.Git.GitWorktreeException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        app.MapDelete("/api/v1/projects/{project}/workspaces/{id}", (string project, string id, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, ServerRole.Author, hosted.Id);
            if (!decision.Allowed) return Refused(decision);
            if (workspaces.Find(hosted.Id, id) is not { } workspace) return Results.NotFound();
            if (workspace.Owner != token.SubjectId) return Results.Json(new { error = "A workspace is abandoned by its owner." }, statusCode: StatusCodes.Status403Forbidden);
            sessions?.End(workspaces.RootOf(workspace));    // its process has the folder open
            try { workspaces.Abandon(workspace, projects.RootOf(hosted)); }
            catch (DbDataKit.Config.Git.GitWorktreeException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
            return Results.NoContent();
        });

        MapWorkspaceGit(app, projects, workspaces, sessions);
    }

    /// <summary>
    /// The proxies this server is paired with, and publishing a connection to one (plan 13, T10): <c>GET /api/v1/proxies</c> (viewer) and <c>POST /api/v1/projects/{project}/proxies/{proxy}/publish</c> (<c>{"connection", "data_source"?}</c>; the approver role, because it
    /// puts a database login on a server people outside reach). Only the connection's read login is sent; the call to the proxy carries the Publisher role and the name of the person who asked.
    /// </summary>
    private static void MapProxies(IEndpointRouteBuilder app, ProjectsFile projects, ProxiesFile proxies, ProxyPublisher publisher, string clientName)
    {
        // T11: a console brokering a pairing sends the Proxy's address and a code the Proxy just made for this server; pairing is configuration, so it is an administrator's
        app.MapPost("/api/v1/proxies", async (HttpContext context) =>
        {
            if (!ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin).Allowed) return Results.Json(new { error = "This needs the admin role." }, statusCode: StatusCodes.Status403Forbidden);
            string? name, address, code;
            try
            {
                var body = await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted);
                (name, address, code) = (body?["name"]?.GetValue<string>(), body?["url"]?.GetValue<string>(), body?["code"]?.GetValue<string>());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { name = address = code = null; }
            if (string.IsNullOrWhiteSpace(name) || !ProjectsFile.IsValidId(name) || string.IsNullOrWhiteSpace(code) || !Uri.TryCreate(address, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps)
                return Results.Json(new { error = "The body is a JSON object with a \"name\" (lower-case letters, digits, '-' and '_'), the proxy's https \"url\" and a pairing \"code\"." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var (paired, _) = await publisher.PairAsync(name, url, code, clientName, context.RequestAborted);
                return Results.Json(new { name = paired.Name, url = paired.Url, instance_id = paired.InstanceId, paired_at = paired.PairedAt });
            }
            catch (Exception ex) when (ex is PairingFailedException or IncompatibleServerException)
            {
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        app.MapGet("/api/v1/proxies", () => Results.Json(new { proxies = proxies.All().Select(p => new { name = p.Name, url = p.Url, instance_id = p.InstanceId, paired_at = p.PairedAt }) }));

        app.MapPost("/api/v1/projects/{project}/proxies/{proxy}/publish", async (string project, string proxy, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var token = context.ManagementToken();
            if (!ManagementAuthorization.Authorize(token, ServerRole.Approver, hosted.Id).Allowed) return Results.Json(new { error = "This needs the approver role." }, statusCode: StatusCodes.Status403Forbidden);
            string? connection, dataSource;
            try
            {
                var body = await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted);
                (connection, dataSource) = (body?["connection"]?.GetValue<string>(), body?["data_source"]?.GetValue<string>());
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { connection = dataSource = null; }
            if (string.IsNullOrWhiteSpace(connection)) return Results.Json(new { error = "The body is a JSON object with the \"connection\" to publish (and, if it should be called otherwise on the proxy, a \"data_source\")." }, statusCode: StatusCodes.Status400BadRequest);

            var config = ProjectContext.Load(projects.RootOf(hosted)).Config;
            var result = await publisher.PublishAsync(proxy, config, connection, string.IsNullOrWhiteSpace(dataSource) ? connection : dataSource, token, context.RequestAborted);
            return Results.Json(result.Body, statusCode: result.Status);
        });

        // J12: a semantic model of the project, with the connections it reads, to the same proxy
        app.MapPost("/api/v1/projects/{project}/proxies/{proxy}/publish-model", async (string project, string proxy, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var token = context.ManagementToken();
            if (!ManagementAuthorization.Authorize(token, ServerRole.Approver, hosted.Id).Allowed) return Results.Json(new { error = "This needs the approver role." }, statusCode: StatusCodes.Status403Forbidden);
            string? model;
            try
            {
                var body = await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted);
                model = body?["model"]?.GetValue<string>();
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { model = null; }
            if (string.IsNullOrWhiteSpace(model)) return Results.Json(new { error = "The body is a JSON object with the \"model\" (the name of a semantic model of the project) to publish." }, statusCode: StatusCodes.Status400BadRequest);

            var loaded = ProjectContext.Load(projects.RootOf(hosted));
            if (loaded.Semantic.Models.FirstOrDefault(m => m.Name == model) is not { } file) return Results.Json(new { error = $"The project has no semantic model '{model}'." }, statusCode: StatusCodes.Status404NotFound);
            var result = await publisher.PublishSemanticAsync(proxy, file, loaded.Config, token, context.RequestAborted);
            return Results.Json(result.Body, statusCode: result.Status);
        });
    }

    /// <summary>
    /// Git for a workspace (plan 13, W08, D15): <c>GET .../changes</c> (what differs from where the draft started, with patches and, for a text file, both whole texts) and <c>GET .../history</c> are a viewer's; <c>POST .../commit</c> (<c>{"message"}</c>) and <c>POST .../push</c> (which answers <c>pull_request_url</c> for a remote at GitHub, Azure DevOps or GitLab, and presents the project's <c>git_credential</c>, W12) are the owner's and need the
    /// author role; <c>POST .../merge</c> needs the approver role, because it changes what the scheduler and the deploys read. A project with a git remote is pushed to (a branch, for a pull request opened where the remote lives) and never merged inside
    /// the server, since the server's copy of a remote is reset to the remote's branch; a project without one is merged inside the server, in the author's name. A merge that conflicts changes nothing and answers 409 with the files. A merged workspace is abandoned.
    /// </summary>
    private static void MapWorkspaceGit(IEndpointRouteBuilder app, ProjectsFile projects, Workspaces workspaces, WarmSessionRunner? sessions)
    {
        static DbDataKit.Config.Git.GitAuthor AuthorOf(ValidatedToken token) => new(token.SubjectName ?? token.SubjectId, $"{token.SubjectId}@dbdatabuild.local");

        // the project, the workspace, and whether this caller may do this to it; the failure is the answer
        IResult? Resolve(string project, string id, HttpContext context, ServerRole needed, bool ownerOnly, out HostedProject hosted, out Workspace workspace)
        {
            hosted = null!;
            workspace = null!;
            if (projects.Find(project) is not { } found) return Results.NotFound();
            hosted = found;
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, needed, hosted.Id);
            if (!decision.Allowed) return Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
            if (workspaces.Find(hosted.Id, id) is not { } w) return Results.NotFound();
            workspace = w;
            return ownerOnly && w.Owner != token.SubjectId ? Results.Json(new { error = "A workspace is its owner's." }, statusCode: StatusCodes.Status403Forbidden) : null;
        }

        static IResult Failed(DbDataKit.Config.Git.GitOperationException ex) => Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict);

        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/changes", (string project, string id, HttpContext context) =>
        {
            if (Resolve(project, id, context, ServerRole.Viewer, false, out _, out var workspace) is { } refused) return refused;
            try
            {
                var root = workspaces.RootOf(workspace);
                var changes = DbDataKit.Config.Git.GitWorkspaceOperations.Changes(root, workspace.Base).Select(c => new { path = c.Path, status = c.Status, patch = c.Patch, before = c.Before, after = c.After, sides = c.Sides }).ToList();
                // whether a commit has not taken some of them yet: a merge takes what is committed
                return Results.Json(new { changes, uncommitted = DbDataKit.Config.Git.GitWorkspaceOperations.HasUncommitted(root) });
            }
            catch (DbDataKit.Config.Git.GitOperationException ex) { return Failed(ex); }
        });

        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/history", (string project, string id, HttpContext context) =>
        {
            if (Resolve(project, id, context, ServerRole.Viewer, false, out _, out var workspace) is { } refused) return refused;
            return Results.Json(new { commits = DbDataKit.Config.Git.GitWorkspaceOperations.History(workspaces.RootOf(workspace), workspace.Base).Select(c => new { sha = c.Sha, message = c.Message, author = c.AuthorName, when = c.WhenUtc }) });
        });

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/commit", async (string project, string id, HttpContext context) =>
        {
            if (Resolve(project, id, context, ServerRole.Author, true, out _, out var workspace) is { } refused) return refused;
            string? message;
            try { message = (await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted))?["message"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { message = null; }
            if (string.IsNullOrWhiteSpace(message)) return Results.Json(new { error = "The body is a JSON object with a \"message\"." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var sha = DbDataKit.Config.Git.GitWorkspaceOperations.Commit(workspaces.RootOf(workspace), message, AuthorOf(context.ManagementToken()));
                return Results.Json(new { committed = sha is not null, sha });
            }
            catch (DbDataKit.Config.Git.GitOperationException ex) { return Failed(ex); }
        });

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/push", (string project, string id, HttpContext context) =>
        {
            if (Resolve(project, id, context, ServerRole.Author, true, out var hosted, out var workspace) is { } refused) return refused;
            if (!hosted.IsRemote) return Results.Json(new { error = "This project has no git remote to push to; merge it inside the server instead." }, statusCode: StatusCodes.Status409Conflict);
            try
            {
                var root = workspaces.RootOf(workspace);
                DbDataKit.Config.Git.GitWorkspaceOperations.Push(root, workspace.Branch, projects.CredentialFor(hosted));
                // where the person opens the pull request: a link built from the remote's address (GitHub, Azure DevOps, GitLab), null for any other remote
                return Results.Json(new { pushed = true, branch = workspace.Branch, pull_request_url = DbDataKit.Config.Git.GitPullRequestLink.For(hosted.Git, workspace.Branch) });
            }
            catch (DbDataKit.Config.Git.GitOperationException ex) { return Failed(ex); }
            catch (DbDataKit.Config.Git.GitRemoteException ex) { return Failed(new DbDataKit.Config.Git.GitOperationException(ex.Message, ex)); }
        });

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/merge", (string project, string id, HttpContext context) =>
        {
            if (Resolve(project, id, context, ServerRole.Approver, false, out var hosted, out var workspace) is { } refused) return refused;
            if (hosted.IsRemote) return Results.Json(new { error = "This project follows a git remote, so its changes go there: push the workspace and merge it where the remote lives." }, statusCode: StatusCodes.Status409Conflict);
            try
            {
                var merged = DbDataKit.Config.Git.GitWorkspaceOperations.Merge(projects.RootOf(hosted), workspace.Branch, AuthorOf(context.ManagementToken()));
                if (!merged.Merged) return Results.Json(new { error = "The workspace conflicts with the project's branch; nothing was merged.", conflicts = merged.Conflicts }, statusCode: StatusCodes.Status409Conflict);
                sessions?.End(workspaces.RootOf(workspace));
                workspaces.Abandon(workspace, projects.RootOf(hosted));
                return Results.Json(new { merged = true, sha = merged.Sha, fast_forward = merged.FastForward });
            }
            catch (DbDataKit.Config.Git.GitOperationException ex) { return Failed(ex); }
            catch (DbDataKit.Config.Git.GitWorktreeException ex) { return Failed(new DbDataKit.Config.Git.GitOperationException(ex.Message, ex)); }
        });
    }
}
