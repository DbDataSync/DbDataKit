using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli.Mcp;
using DbDataBuild.Tui.Model;
using DbDataKit.Scheduling;
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
        new(projectRoot, all.Where(c => c.Name is not ("ui terminal" or "ui mcp")));

    /// <summary>The person a call is for, as the tracking tables record them: "Ada via Main console".</summary>
    internal static string ActorOf(ValidatedToken token) => $"{token.SubjectName ?? token.SubjectId} via {token.Client.Name}";

    /// <param name="projects">The projects served, read again on every call so one added or removed is served or gone at once.</param>
    /// <param name="commands">All the commands there are; each project is offered the read-only ones.</param>
    /// <param name="surfaceFor">Which of them a project is offered and how their inputs are checked; the read-only rules unless a test or a later slice says otherwise.</param>
    public static void Map(IEndpointRouteBuilder app, ProjectsFile projects, IReadOnlyList<CommandInfo> commands, IProjectCommandRunner runner, Func<string, IReadOnlyList<CommandInfo>, ToolSurface>? surfaceFor = null, BuildScheduler? scheduler = null, Workspaces? workspaces = null, WarmSessionRunner? workspaceRunner = null, ProxiesFile? proxies = null, ProxyPublisher? publisher = null, PairingCodes? pairingCodes = null, string? clientName = null)
    {
        surfaceFor ??= FullSurface;
        var specs = CommandSpecs.All.ToDictionary(s => s.Name);

        app.MapGet("/api/v1/projects", () => Results.Json(new
        {
            projects = projects.All().Select(p => new { id = p.Id, name = p.Id, source = p.IsRemote ? "git" : "path", branch = p.Branch }),
        }));

        // The schemas of the project's files and which file each describes, for the console's forms (the viewer role may read them: they are the tool's own, not the project's)
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

        // the scheduled plans of a project, whether or not this server runs them (the scheduler is opt-in): their schedule, the next time each is due and how the last run went
        app.MapGet("/api/v1/projects/{project}/schedule", (string project) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var now = TimeProvider.System.GetUtcNow();
            var jobs = scheduler is not null
                ? scheduler.Jobs(hosted)
                : [.. RefreshJobs.Discover(projects.RootOf(hosted)).Select(j => (j, (DateTimeOffset?)DbDataKit.Scheduling.ScheduleEvaluator.NextAfter(j.Schedule, now), (JobState?)null))];
            return Results.Json(new
            {
                enabled = scheduler is not null,
                jobs = jobs.Select(j => new
                {
                    connection = j.Job.Connection,
                    plan = j.Job.Plan,
                    schedule = j.Job.Schedule.Cron,
                    time_zone = j.Job.Schedule.TimeZoneId,
                    next_due = j.NextDue,
                    last_started = j.State?.LastStarted,
                    last_exit = j.State?.LastExit,
                    paused = scheduler?.PausedBy(hosted, j.Job) is not null,
                    paused_by = scheduler?.PausedBy(hosted, j.Job)?.By,
                }),
            });
        });

        // Pausing, resuming and running a scheduled plan now are an operator's (running a plan changes target data). They need the scheduler: without it nothing is queued, so there is nothing to pause or start.
        foreach (var action in new[] { "pause", "resume", "run" })
            app.MapPost("/api/v1/projects/{project}/schedule/{connection}/{plan}/" + action, (string project, string connection, string plan, HttpContext context) =>
            {
                if (projects.Find(project) is not { } hosted) return Results.NotFound();
                var token = context.ManagementToken();
                var decision = ManagementAuthorization.Authorize(token, ServerRole.Operator, hosted.Id);
                if (!decision.Allowed) return Results.Json(new { error = "This needs the operator role." }, statusCode: StatusCodes.Status403Forbidden);
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
                return Results.Json(new { error = $"`{command.Name}` needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);

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

            var result = await commandRunner.RunAsync(argv, projectRoot, ActorOf(context.ManagementToken()), context.RequestAborted);
            JsonNode? document = null;
            try { document = JsonNode.Parse(result.Out); } catch (JsonException) { }
            return Results.Json(new JsonObject { ["exit"] = result.Exit, ["document"] = document, ["text"] = document is null ? (result.Out.Length > 0 ? result.Out : result.Err) : null });
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
