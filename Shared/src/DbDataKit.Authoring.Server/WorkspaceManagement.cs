using DbDataKit.DataModel.Runner;
using DbDataKit.Deploy;
using DbDataKit.Catalog;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Config.Git;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Authoring.Server;

/// <summary>
/// The management API of the Workspace server (plan 22a GP31). Every call needs a token a paired console issued, and the role the call says:
/// <list type="bullet">
/// <item><c>GET /api/v1/info</c>: who the server is (any role).</item>
/// <item><c>GET /api/v1/projects</c> (viewer) and <c>POST /api/v1/projects</c> (admin, <c>{"id", "title"?, "kind"?}</c>): the projects hosted, and a new one. <c>GET /api/v1/projects/{project}</c> (viewer of that project): one project and the
/// package it becomes on a Runner (<c>package</c>, null for a kind that makes none).</item>
/// <item>the workspace calls of <see cref="DbDataKit.Authoring.WorkspaceApi"/> (list, create, abandon, the files of a draft: list, read, write, move, delete; changes, history, commit, push, merge), the same as every kind of project has, and <c>GET /api/v1/projects/{project}/workspaces/{id}/events</c> (viewer): what happened to a draft, from the state module.</item>
/// <item><c>GET /api/v1/app-build</c> (viewer): whether this server can build an app, and what it lacks. <c>POST .../workspaces/{id}/builds</c> (author) asks for a build of a draft; <c>GET .../builds</c>, <c>.../builds/{build}</c> and <c>.../builds/{build}/preview[/{path}]</c> (viewer) read them and show the built app, read only
/// (<see cref="WorkspaceBuilds"/>, <see cref="WorkspacePreview"/>). This server sends a build nowhere: the console reads it and deploys it.</item>
/// <item><c>GET .../builds/{build}/contents</c> and <c>.../builds/{build}/files/{sha256}</c> (viewer): what a build holds to send and its record (<see cref="BuildContents"/>, <see cref="BuildRecord"/>), and each file by hash, which the control service reads to deploy the build.</item>
/// <item>the routes that sent a build, ran its tasks, read a Gateway's catalog or kept publisher keys answer 410 naming the console's call (<see cref="RemovedRoutes"/>).</item>
/// </list>
/// What happens to a draft is recorded in the state module as it happens; a record that cannot be written is logged and never turns a finished git operation into an error.
/// </summary>
public static class WorkspaceManagement
{
    /// <summary>What the route that tested a script on this server answers now: where a script is tested.</summary>
    public const string ScriptTestMoved = "A script is tested on a Runner, not on the Workspace server: Test on the Scripts tab builds the draft, stages it in a channel that is not production through the console, and runs the task script-test there.";

    /// <summary>How each removed answer starts: this server calls no Runner or Gateway and holds no publisher key, and the console does the work as the signed-in person.</summary>
    public const string ConsoleDoesIt = "The Workspace server calls no Runner or Gateway and holds no publisher key; the console does this, as the signed-in person: ";

    /// <summary>What the console deploys with, in place of a publisher key of this server.</summary>
    private const string ConsoleSigns = "the console signs each version it deploys with its own publisher key, which every Runner and Gateway group it manages trusts.";

    /// <summary>
    /// The routes this server had for sending a build to a Runner or a Gateway, running its tasks there, reading a Gateway's catalog and keeping publisher keys. Each answers
    /// 410 with <see cref="ConsoleDoesIt"/> and the console's call that took its place, so a caller of the old route learns where it went.
    /// </summary>
    public static readonly IReadOnlyList<(string Method, string Route, string Answer)> RemovedRoutes =
    [
        ("POST", "/api/v1/projects/{project}/workspaces/{id}/builds/{build}/publish", ConsoleDoesIt + "deploy the build from the Builds tab, or with the console's POST /api/deploys."),
        ("POST", "/api/v1/projects/{project}/workspaces/{id}/builds/{build}/preview-data", ConsoleDoesIt + "Preview with data on the Builds tab, or the console's POST /api/deploys with \"previewInstance\"."),
        ("POST", "/api/v1/projects/{project}/workspaces/{id}/builds/{build}/run", ConsoleDoesIt + "run a task of what the console deployed with POST /api/deploys/{id}/runs (the Runs, Provisioning and Deploy tabs)."),
        ("GET", "/api/v1/projects/{project}/workspaces/{id}/builds/{build}/runs/{run}", ConsoleDoesIt + "read a run with GET /api/deploys/{id}/runs/{run}."),
        ("POST", "/api/v1/projects/{project}/workspaces/{id}/builds/{build}/binding-test", ConsoleDoesIt + "test a binding with POST /api/deploys/{id}/binding-test."),
        ("POST", "/api/v1/projects/{project}/workspaces/{id}/builds/{build}/contract", ConsoleDoesIt + "approve a model's contract with POST /api/deploys/{id}/contract (the Deploy tab)."),
        ("POST", "/api/v1/projects/{project}/workspaces/{id}/catalog/refresh", ConsoleDoesIt + "refresh the catalog from a Gateway on the Catalog tab, or with the console's POST /api/workspace-catalog/{workspace}/{project}/gateway-read."),
        ("GET", "/api/v1/publish-targets", ConsoleDoesIt + "it lists the Runners and Gateways it manages (GET /api/instances)."),
        ("GET", "/api/v1/publisher-keys", ConsoleDoesIt + ConsoleSigns),
        ("PUT", "/api/v1/publisher-keys/{name}", ConsoleDoesIt + ConsoleSigns),
        ("DELETE", "/api/v1/publisher-keys/{name}", ConsoleDoesIt + ConsoleSigns),
        ("POST", "/api/v1/publisher-keys/{name}/pair", ConsoleDoesIt + ConsoleSigns),
    ];

    public static void Map(IEndpointRouteBuilder app, string instanceId, string product, string version, WorkspaceStore store, WorkspaceProjects projects, IAuditLog audit, Action<string> log, Workspaces workspaces, WorkspaceBuilds builds)
    {
        app.MapGet("/api/v1/info", () => Results.Json(new JsonObject
        {
            ["product"] = product,
            ["version"] = version,
            ["instance_id"] = instanceId,
        }));

        static IResult Refused(AuthorizationDecision decision) => DbDataKit.Authoring.WorkspaceApi.Refused(decision);
        static string Actor(ValidatedToken token) => token.SubjectName ?? token.SubjectId;
        void Audit(ValidatedToken token, string command, string? target, string outcome) =>
            audit.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), token.Client.Id, token.Client.Name, token.SubjectId, token.SubjectName, token.IsConsole, command, target, outcome, token.TokenId));
        static async Task<JsonObject?> Body(HttpContext context)
        {
            try { return await JsonSerializer.DeserializeAsync<JsonObject>(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException) { return null; }
        }

        // ---- projects ----

        app.MapGet("/api/v1/projects", (HttpContext context) =>
        {
            var token = context.ManagementToken();
            // a token with no role on the server and none on any project reads nothing; one scoped to some projects lists those and no others
            if (ManagementAuthorization.Authorize(token, ServerRole.Viewer) is { Allowed: false } refused && !token.Projects.Keys.Any(p => ManagementAuthorization.Authorize(token, ServerRole.Viewer, p).Allowed)) return Refused(refused);
            var drafts = store.Workspaces().GroupBy(w => w.Project).ToDictionary(g => g.Key, g => g.Count());
            // a token scoped to some projects sees those and no others
            return Results.Json(new
            {
                projects = store.Projects().Where(p => ManagementAuthorization.Authorize(token, ServerRole.Viewer, p.Id).Allowed)
                    .Select(p => new { id = p.Id, kind = p.Kind, title = p.Title, workspaces = drafts.GetValueOrDefault(p.Id), created = p.CreatedAt }),
            });
        });

        app.MapPost("/api/v1/projects", async (HttpContext context) =>
        {
            var token = context.ManagementToken();
            if (ManagementAuthorization.Authorize(token, ServerRole.Admin) is { Allowed: false } refused) return Refused(refused);
            var body = await Body(context);
            string? id, title, kind, template;
            try { (id, title, kind, template) = (body?["id"]?.GetValue<string>(), body?["title"]?.GetValue<string>(), body?["kind"]?.GetValue<string>() ?? "app", body?["template"]?.GetValue<string>()); }
            catch (InvalidOperationException) { id = title = kind = template = null; }
            if (id is null || kind is null) return Results.Json(new { error = "The body is a JSON object with the project's \"id\" (and optionally a \"title\", a \"kind\", app by default, and a \"template\" of that kind)." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var made = projects.Create(id, kind, title, new GitAuthor(Actor(token), $"{token.SubjectId}@dbdataworkspace.local"), token.SubjectId, template);
                Audit(token, "project.create", id, "ok");
                return Results.Json(new { id = made.Id, kind = made.Kind, title = made.Title, workspaces = 0, created = made.CreatedAt }, statusCode: StatusCodes.Status201Created);
            }
            catch (WorkspaceProjectException ex)
            {
                Audit(token, "project.create", id, "refused: " + ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        });

        // ---- workspaces: the shared calls, and the history this server keeps ----

        DbDataKit.Authoring.WorkspaceApi.Map(app, projects, workspaces, new WorkspaceApiOptions("dbdataworkspace.local", Changed: change =>
        {
            try
            {
                switch (change.Kind)
                {
                    case "created":
                        store.RecordCreated(new WorkspaceRow(change.Workspace.Project, change.Workspace.Id, change.Workspace.Owner, change.Workspace.OwnerName, change.Workspace.Name, change.Workspace.Branch, change.Workspace.Base, change.Workspace.Created));
                        break;
                    case "merged" or "abandoned":
                        store.RecordEnded(change.Workspace.Project, change.Workspace.Id, change.Kind, change.ActorId, change.ActorName, change.Detail);
                        break;
                    case "file-written" or "file-moved" or "file-deleted":
                        break;      // a save is not history: the commit that takes it is (the module's kinds are the git ones)
                    default:
                        store.RecordEvent(change.Workspace.Project, change.Workspace.Id, change.Kind, change.ActorId, change.ActorName, change.Detail);
                        break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log($"The record of '{change.Kind}' on workspace {change.Workspace.Project}/{change.Workspace.Id} could not be written: {ex.Message}");
            }
        }));

        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/events", (string project, string id, HttpContext context) =>
        {
            if (projects.Find(project) is not { } hosted) return Results.NotFound();
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer, hosted.Id);
            if (!decision.Allowed) return Refused(decision);
            return Results.Json(new { events = store.Events(hosted.Id, id).Select(e => new { kind = e.Kind, actor = e.ActorName, detail = e.Detail, at = e.OccurredAt }) });
        });

        // ---- builds, previews and publishing (plan 22a GP35) ----

        app.MapGet("/api/v1/app-build", (HttpContext context) =>
        {
            if (ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer) is { Allowed: false } refused && !context.ManagementToken().Projects.Keys.Any(p => ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer, p).Allowed)) return Refused(refused);
            var readiness = builds.Readiness();
            return Results.Json(new JsonObject
            {
                ["ready"] = readiness.Ready,
                ["problems"] = new JsonArray([.. readiness.Problems.Select(x => (JsonNode?)x)]),
                ["package_store"] = readiness.Store is { } packages
                    ? new JsonObject { ["folder"] = packages.Folder, ["packages"] = packages.Packages.Count, ["files"] = packages.FileCount, ["digest"] = packages.Digest, ["library"] = new JsonArray([.. packages.Library.Select(x => (JsonNode?)x)]) }
                    : null,
                ["bun"] = readiness.BunPath is null ? null : new JsonObject { ["path"] = readiness.BunPath, ["source"] = readiness.BunSource },
                ["isolation"] = new JsonObject { ["setting"] = readiness.Isolation, ["available"] = readiness.CanIsolate },
            });
        });

        object DescribeBuild(WorkspaceBuildRow b) => new { id = b.Id, workspace = b.WorkspaceId, status = b.Status, commit = b.CommitSha, requested_by = b.RequestedBy, message = b.Message, requested = b.RequestedAt, started = b.StartedAt, finished = b.FinishedAt };

        // the project of a build route, and whether this caller may do this in it; the failure is the answer
        IResult? InProject(string project, HttpContext context, ServerRole needed, out AuthoringProject hosted)
        {
            hosted = null!;
            if (projects.Find(project) is not { } found) return Results.NotFound();
            hosted = found;
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), needed, found.Id);
            return decision.Allowed ? null : Refused(decision);
        }

        // one project, for a person who may read it: what the console asks before it starts a read of a database for the project (it learns the package the project becomes on a Runner, and that the person has access).
        // `package` is null for a kind that makes no one package of tasks (an app; a Store project makes one package per model).
        app.MapGet("/api/v1/projects/{project}", (string project, HttpContext context) =>
        {
            if (InProject(project, context, ServerRole.Viewer, out var hosted) is { } refused) return refused;
            if (store.FindProject(hosted.Id) is not { } row) return Results.NotFound();
            var package = projects.Kinds.Find(row.Kind)?.Publishes.Any(r => r is { Product: "runner", What: "package" }) == true ? PackageArtifacts.NameOf(row.Id) : null;
            return Results.Json(new JsonObject
            {
                ["id"] = row.Id, ["kind"] = row.Kind, ["title"] = row.Title, ["created"] = row.CreatedAt,
                ["workspaces"] = store.Workspaces().Count(w => w.Project == row.Id), ["package"] = package,
            });
        });

        // ---- the kinds of project and the check of a draft (plan 33 SC20a) ----

        // what a screen offers when a project is made, and what each kind publishes as and to which product; any role that reads anything may ask
        app.MapGet("/api/v1/project-kinds", (HttpContext context) =>
        {
            var token = context.ManagementToken();
            if (ManagementAuthorization.Authorize(token, ServerRole.Viewer) is { Allowed: false } refused && !token.Projects.Keys.Any(p => ManagementAuthorization.Authorize(token, ServerRole.Viewer, p).Allowed)) return Refused(refused);
            return Results.Json(new JsonObject
            {
                ["kinds"] = new JsonArray([.. projects.Kinds.All.Select(k => (JsonNode?)new JsonObject
                {
                    ["name"] = k.Name,
                    ["title"] = k.Title,
                    ["description"] = k.Description,
                    ["buildable"] = k.Buildable,
                    ["produces"] = k.Produces,
                    ["templates"] = new JsonArray([.. k.Templates.Select(t => (JsonNode?)new JsonObject { ["name"] = t.Name, ["title"] = t.Title, ["description"] = t.Description })]),
                    ["publishes"] = new JsonArray([.. k.Publishes.Select(r => (JsonNode?)new JsonObject { ["product"] = r.Product, ["what"] = r.What, ["description"] = r.Description })]),
                })]),
            });
        });

        // the draft checked by its kind's own validators (the ones the standalone product runs); a finding is a diagnostic, never an error status
        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/check", async (string project, string id, HttpContext context) =>
        {
            if (InProject(project, context, ServerRole.Viewer, out var hosted) is { } refused) return refused;
            if (workspaces.Find(hosted.Id, id) is not { } workspace) return Results.NotFound();
            var kind = projects.Kinds.Find(store.FindProject(hosted.Id)!.Kind);
            if (kind is null) return Results.Json(new { error = $"The project '{hosted.Id}' is of a kind this server does not know ({store.FindProject(hosted.Id)!.Kind})." }, statusCode: StatusCodes.Status409Conflict);
            var diagnostics = await DraftCheck.RunAsync(kind, workspaces.RootOf(workspace), context.RequestAborted);
            return Results.Json(new JsonObject
            {
                ["kind"] = kind.Name,
                ["ok"] = !diagnostics.Any(d => d.IsError),
                ["diagnostics"] = new JsonArray([.. diagnostics.Select(d => (JsonNode?)new JsonObject { ["severity"] = d.Severity, ["code"] = d.Code, ["message"] = d.Message, ["file"] = d.File, ["line"] = d.Line })]),
            });
        });

        // compile, lowered text and the offline sample of a draft (plan 33 SC20l): the product's own program on a copy or read only, offline, the draft untouched; an author writes what a compile made through the file routes
        static JsonArray DiagnosticsJson(IEnumerable<ProjectDiagnostic> found) => new([.. found.Select(d => (JsonNode?)new JsonObject { ["severity"] = d.Severity, ["code"] = d.Code, ["message"] = d.Message, ["file"] = d.File, ["line"] = d.Line })]);
        IResult? CompilerOf(string project, string id, HttpContext context, out IDraftCompiler compiler, out string root)
        {
            (compiler, root) = (null!, null!);
            if (InProject(project, context, ServerRole.Viewer, out var hosted) is { } refused) return refused;
            if (workspaces.Find(hosted.Id, id) is not { } workspace) return Results.NotFound();
            var kindName = store.FindProject(hosted.Id)!.Kind;
            if (projects.Kinds.Find(kindName) is not IDraftCompiler found) return Results.Json(new { error = $"A project of the kind '{kindName}' has nothing to compile." }, statusCode: StatusCodes.Status409Conflict);
            (compiler, root) = (found, workspaces.RootOf(workspace));
            return null;
        }

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/compile", async (string project, string id, HttpContext context) =>
        {
            if (CompilerOf(project, id, context, out var compiler, out var root) is { } refused) return refused;
            try
            {
                var result = await compiler.CompileAsync(root, context.RequestAborted);
                return Results.Json(new JsonObject
                {
                    ["ok"] = result.Ok,
                    ["message"] = result.Message,
                    ["diagnostics"] = DiagnosticsJson(result.Diagnostics),
                    ["files"] = new JsonArray([.. result.Files.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Path, ["status"] = f.Status, ["content"] = f.Content, ["truncated"] = f.Truncated })]),
                });
            }
            catch (ProjectKindUnavailableException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/lowered", async (string project, string id, HttpContext context) =>
        {
            if (CompilerOf(project, id, context, out var compiler, out var root) is { } refused) return refused;
            var body = await Body(context);
            string? path;
            try { path = body?["path"]?.GetValue<string>(); } catch (InvalidOperationException) { path = null; }
            if (!BuildProjectKind.IsModelPath(path)) return Results.Json(new { error = "The body is a JSON object with the \"path\" of a model file of the project (under models/, .sql or .yml)." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var result = await compiler.LowerAsync(root, path!, context.RequestAborted);
                return Results.Json(new JsonObject
                {
                    ["ok"] = result.Ok,
                    ["diagnostics"] = DiagnosticsJson(result.Diagnostics),
                    ["files"] = new JsonArray([.. result.Files.Select(f => (JsonNode?)new JsonObject { ["path"] = f.Path, ["content"] = f.Content })]),
                });
            }
            catch (ProjectKindUnavailableException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        // the lineage of a Build draft, column by column (plan 33 SC20h): `project show graph --columns` on the draft, offline and read only
        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/lineage", async (string project, string id, HttpContext context) =>
        {
            if (CompilerOf(project, id, context, out var compiler, out var root) is { } refused) return refused;
            var body = await Body(context);
            const string Shape = "The body is a JSON object with an optional \"column\" (model.column) to follow up and down.";
            string? column;
            try { column = body?["column"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return Results.Json(new { error = Shape }, statusCode: StatusCodes.Status400BadRequest); }
            if (column is not null && !BuildProjectKind.IsColumnRef(column)) return Results.Json(new { error = Shape }, statusCode: StatusCodes.Status400BadRequest);
            try { return Results.Json(ToolJson(await compiler.LineageAsync(root, column, context.RequestAborted))); }
            catch (ProjectKindUnavailableException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        // the mapped models the draft's catalog cache would make (plan 33 SC20k): `project import --from-cache` on a copy; the author writes the ones they choose through the file routes
        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/import-preview", async (string project, string id, HttpContext context) =>
        {
            if (CompilerOf(project, id, context, out var compiler, out var root) is { } refused) return refused;
            var body = await Body(context);
            const string Shape = "The body is a JSON object with the \"connection\" of the project and optional \"tables\" (up to 50 schema.table patterns; none refreshes the descriptors the project has).";
            string? connection;
            var tables = new List<string>();
            try
            {
                connection = body?["connection"]?.GetValue<string>();
                if (body?["tables"] is { } asked) foreach (var t in asked.AsArray()) tables.Add(t!.GetValue<string>());
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return Results.Json(new { error = Shape }, statusCode: StatusCodes.Status400BadRequest); }
            if (!BuildProjectKind.IsConnectionName(connection) || tables.Count > 50 || tables.Any(t => !BuildProjectKind.IsTablePattern(t))) return Results.Json(new { error = Shape }, statusCode: StatusCodes.Status400BadRequest);
            try { return Results.Json(ToolJson(await compiler.ImportAsync(root, connection!, tables, context.RequestAborted))); }
            catch (ProjectKindUnavailableException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/sample", async (string project, string id, HttpContext context) =>
        {
            if (CompilerOf(project, id, context, out var compiler, out var root) is { } refused) return refused;
            var body = await Body(context);
            const string Shape = "The body is a JSON object with optional \"models\" (up to 20 model files of the project), \"rows\" (1 to 1000, default 50), \"seed\" and \"limit\" (1 to 200, default 20).";
            var models = new List<string>();
            int rows, seed, limit;
            try
            {
                if (body?["models"] is { } asked) foreach (var m in asked.AsArray()) models.Add(m!.GetValue<string>());
                (rows, seed, limit) = (body?["rows"]?.GetValue<int>() ?? 50, body?["seed"]?.GetValue<int>() ?? 1, body?["limit"]?.GetValue<int>() ?? 20);
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return Results.Json(new { error = Shape }, statusCode: StatusCodes.Status400BadRequest); }
            if (models.Count > 20 || models.Any(m => !BuildProjectKind.IsModelPath(m)) || rows is < 1 or > 1000 || limit is < 1 or > 200) return Results.Json(new { error = Shape }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var result = await compiler.SampleAsync(root, models, rows, seed, limit, context.RequestAborted);
                return Results.Json(new JsonObject { ["ok"] = result.Ok, ["diagnostics"] = DiagnosticsJson(result.Diagnostics), ["data"] = result.Data });
            }
            catch (ProjectKindUnavailableException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        // a Sync draft's scripts (plan 33 SC22k): the product's own program on a copy, offline, the draft untouched; listing only reads the manifests
        IResult? SyncToolsOf(string project, string id, HttpContext context, ServerRole needed, out ISyncDraftTools tools, out string root)
        {
            (tools, root) = (null!, null!);
            if (InProject(project, context, needed, out var hosted) is { } refused) return refused;
            if (workspaces.Find(hosted.Id, id) is not { } workspace) return Results.NotFound();
            var kindName = store.FindProject(hosted.Id)!.Kind;
            if (projects.Kinds.Find(kindName) is not ISyncDraftTools found) return Results.Json(new { error = $"A project of the kind '{kindName}' has no Sync scripts to run." }, statusCode: StatusCodes.Status409Conflict);
            (tools, root) = (found, workspaces.RootOf(workspace));
            return null;
        }

        static JsonObject ToolJson(DraftToolResult result) => new() { ["ok"] = result.Ok, ["diagnostics"] = DiagnosticsJson(result.Diagnostics), ["data"] = result.Data };

        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/scripts", async (string project, string id, HttpContext context) =>
        {
            if (SyncToolsOf(project, id, context, ServerRole.Viewer, out var tools, out var root) is { } refused) return refused;
            try { return Results.Json(ToolJson(await tools.ScriptsAsync(root, context.RequestAborted))); }
            catch (ProjectKindUnavailableException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
        });

        // the test of a script runs the author's C#, which this server never runs: it is the set's task `script-test` on a Runner, which the Scripts tab stages and runs through the console
        // (Design: docs/authoring-metadata.md#a-scripts-test-runs-on-a-runner); a caller of the old route is told so
        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/script-test", () => Results.Json(new { error = ScriptTestMoved }, statusCode: StatusCodes.Status410Gone));

        // the person's unsaved edits a question carries ("unsaved": path to text, null for a file deleted), so the answer reads the draft as they see it
        static Dictionary<string, string?> UnsavedOf(JsonObject? body)
        {
            var unsaved = new Dictionary<string, string?>(StringComparer.Ordinal);
            if (body?["unsaved"] is JsonObject edits) foreach (var (path, text) in edits) unsaved[path] = text?.GetValue<string>();
            else if (body?["unsaved"] is not null) throw new SyncDesignerRefusal("\"unsaved\" is an object of paths and texts.", 400);
            return unsaved;
        }

        // the plan of a Sync draft's provisioning from its catalog cache: offline, read only, the draft untouched; applying is the set's task provision on a Runner.
        // It reads the draft with the unsaved edits the question carries, as the sync Designer's questions do.
        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/provisioning-plan", async (string project, string id, HttpContext context) =>
        {
            if (SyncToolsOf(project, id, context, ServerRole.Viewer, out var tools, out var root) is { } refused) return refused;
            var body = await Body(context);
            const string Shape = "The body is a JSON object with the \"replication\" of the project to plan and optional \"unsaved\" edits.";
            string? replication;
            try { replication = body?["replication"]?.GetValue<string>(); }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return Results.Json(new { error = Shape }, statusCode: StatusCodes.Status400BadRequest); }
            if (!SyncProjectKind.IsName(replication)) return Results.Json(new { error = Shape }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                using var view = SyncDesigner.DraftView.Open(root, UnsavedOf(body));
                return Results.Json(ToolJson(await tools.ProvisioningPlanAsync(view.Root, replication!, context.RequestAborted)));
            }
            catch (ProjectKindUnavailableException ex) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status409Conflict); }
            catch (SyncDesignerRefusal ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return Results.Json(new { error = Shape }, statusCode: StatusCodes.Status400BadRequest); }
        });

        // the sync Designer's questions, answered in this process by Sync's libraries over the draft's files and catalog cache (SyncDesigner): no database, no child
        // process; a question may carry the person's unsaved edits, so the answer reads the draft as they see it. Create mappings answers files and writes none.
        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/sync/script-slots", (string project, string id, HttpContext context) =>
            SyncToolsOf(project, id, context, ServerRole.Viewer, out _, out _) is { } refused ? refused : Results.Json(SyncDesigner.ScriptSlotsAnswer()));

        async Task<IResult> SyncDesignerAsync(string project, string id, HttpContext context, ServerRole needed, Func<string, JsonObject, Task<JsonNode>> answer)
        {
            if (SyncToolsOf(project, id, context, needed, out _, out var root) is { } refused) return refused;
            var body = await Body(context);
            if (body is null) return Results.Json(new { error = "The body is a JSON object." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                using var view = SyncDesigner.DraftView.Open(root, UnsavedOf(body));
                return Results.Json(await answer(view.Root, body));
            }
            catch (SyncDesignerRefusal ex) { return Results.Json(new { error = ex.Message }, statusCode: ex.Status); }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException) { return Results.Json(new { error = "A value of the body is not of the right type: " + ex.Message }, statusCode: StatusCodes.Status400BadRequest); }
        }

        static string? Text(JsonObject body, string key) => body[key]?.GetValue<string>();

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/sync/capabilities", (string project, string id, HttpContext context) =>
            SyncDesignerAsync(project, id, context, ServerRole.Viewer, (root, body) => Task.FromResult(SyncDesigner.CapabilitiesAnswer(root, Text(body, "connection")))));

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/sync/inferred-columns", (string project, string id, HttpContext context) =>
            SyncDesignerAsync(project, id, context, ServerRole.Viewer, (root, body) => SyncDesigner.InferredAnswerAsync(root, Text(body, "replication"), Text(body, "mapping"), context.RequestAborted)));

        // making mappings is the author's, though the answer is only texts: a viewer cannot save them
        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/sync/bulk-create", (string project, string id, HttpContext context) =>
            SyncDesignerAsync(project, id, context, ServerRole.Author, (root, body) =>
            {
                var tables = (body["tables"] as JsonArray ?? throw new SyncDesignerRefusal("\"tables\" is a list of {\"schema\", \"table\"}.", 400))
                    .Select(t => t is JsonObject o && Text(o, "table") is { Length: > 0 } table
                        ? new SyncDesigner.TableToMap(Text(o, "schema") ?? "", table)
                        : throw new SyncDesignerRefusal("\"tables\" is a list of {\"schema\", \"table\"}.", 400))
                    .ToList();
                return SyncDesigner.BulkCreateAnswerAsync(root, Text(body, "replication"), tables, context.RequestAborted);
            }));

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/builds", (string project, string id, HttpContext context) =>
        {
            if (InProject(project, context, ServerRole.Author, out var hosted) is { } refused) return refused;
            if (workspaces.Find(hosted.Id, id) is not { } workspace) return Results.NotFound();
            var token = context.ManagementToken();
            try
            {
                var row = builds.Request(hosted, store.FindProject(hosted.Id)!.Kind, workspace, token.SubjectId);
                Audit(token, "build.request", $"{hosted.Id}/{id}/{row.Id}", "ok");
                return Results.Json(DescribeBuild(row), statusCode: StatusCodes.Status202Accepted);
            }
            catch (WorkspaceBuildException ex)
            {
                Audit(token, "build.request", $"{hosted.Id}/{id}", "refused: " + ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        });

        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/builds", (string project, string id, HttpContext context) =>
        {
            if (InProject(project, context, ServerRole.Viewer, out var hosted) is { } refused) return refused;
            return Results.Json(new { builds = store.Builds(hosted.Id, id).Select(DescribeBuild) });
        });

        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/builds/{build}", (string project, string id, string build, HttpContext context) =>
        {
            if (InProject(project, context, ServerRole.Viewer, out var hosted) is { } refused) return refused;
            if (!Guid.TryParse(build, out var buildId) || builds.Find(hosted.Id, id, buildId) is not { } found) return Results.NotFound();
            return Results.Json(new JsonObject
            {
                ["build"] = JsonSerializer.SerializeToNode(DescribeBuild(found.Row)),
                ["result"] = found.Result,
            });
        });

        // what a build holds to send, for the control service that deploys it: the build's record (where and from which commit it was built, what the kind's validators found) and each item its kind's routes made, with its files
        // by SHA-256; the files themselves are read one by one, by hash. Read only: this server sends nothing anywhere.
        // Design: docs/publishing.md#deploying-a-build-from-the-control-service
        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/builds/{build}/contents", (string project, string id, string build, HttpContext context) =>
        {
            if (InProject(project, context, ServerRole.Viewer, out var hosted) is { } refused) return refused;
            if (!Guid.TryParse(build, out var buildId) || builds.Find(hosted.Id, id, buildId) is not { } found) return Results.NotFound();
            var kind = store.FindProject(hosted.Id)!.Kind;
            var items = found.Row.Status == "succeeded" && projects.Kinds.Find(kind) is { } projectKind ? BuildContents.Of(projectKind.Publishes, builds.FolderOf(hosted.Id, buildId)) : [];
            return Results.Json(new JsonObject
            {
                ["record"] = BuildRecord.Describe(instanceId, hosted.Id, kind, id, buildId, found.Row.Status, found.Row.CommitSha, found.Result),
                ["items"] = new JsonArray([.. items.Select(i => (JsonNode)i.ToJson())]),
            });
        });

        app.MapGet("/api/v1/projects/{project}/workspaces/{id}/builds/{build}/files/{sha256}", (string project, string id, string build, string sha256, HttpContext context) =>
        {
            if (InProject(project, context, ServerRole.Viewer, out var hosted) is { } refused) return refused;
            if (!Guid.TryParse(build, out var buildId) || builds.Find(hosted.Id, id, buildId) is not { Row.Status: "succeeded" } || projects.Kinds.Find(store.FindProject(hosted.Id)!.Kind) is not { } projectKind) return Results.NotFound();
            var folder = builds.FolderOf(hosted.Id, buildId);
            var file = BuildContents.Of(projectKind.Publishes, folder).SelectMany(i => i.Files).FirstOrDefault(f => f.Sha256 == sha256.ToLowerInvariant());
            return file is null ? Results.NotFound() : Results.File(Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar)), "application/octet-stream");
        });

        // the built app, for a preview: read only, from this server, nothing else (see WorkspacePreview)
        foreach (var route in new[] { "/api/v1/projects/{project}/workspaces/{id}/builds/{build}/preview", "/api/v1/projects/{project}/workspaces/{id}/builds/{build}/preview/{**path}" })
            app.MapGet(route, (string project, string id, string build, string? path, HttpContext context) =>
            {
                if (InProject(project, context, ServerRole.Viewer, out var hosted) is { } refused) return refused;
                if (!Guid.TryParse(build, out var buildId) || builds.Find(hosted.Id, id, buildId) is not { } found) return Results.NotFound();
                return WorkspacePreview.Serve(found.Row, builds.AppFolderOf(hosted.Id, buildId), path);
            });

        // the calls that sent a build to a Runner or a Gateway, ran its tasks there, read a Gateway's catalog or kept a publisher key: this server calls no other
        // service and holds no key, so each answers with the sentence naming the console's call that does it as the person (Design: docs/publishing.md#deploying-a-build-from-the-control-service)
        foreach (var (method, route, answer) in RemovedRoutes)
            app.MapMethods(route, [method], () => Results.Json(new { error = answer }, statusCode: StatusCodes.Status410Gone));
    }
}
