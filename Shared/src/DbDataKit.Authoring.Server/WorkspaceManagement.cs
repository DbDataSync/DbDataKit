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
/// <item><c>GET /api/v1/projects</c> (viewer) and <c>POST /api/v1/projects</c> (admin, <c>{"id", "title"?, "kind"?}</c>): the projects hosted, and a new one.</item>
/// <item>the workspace calls of <see cref="DbDataKit.Authoring.WorkspaceApi"/> (list, create, abandon, the files of a draft: list, read, write, move, delete; changes, history, commit, push, merge), the same as every kind of project has, and <c>GET /api/v1/projects/{project}/workspaces/{id}/events</c> (viewer): what happened to a draft, from the state module.</item>
/// <item><c>GET /api/v1/app-build</c> (viewer): whether this server can build an app, and what it lacks. <c>POST .../workspaces/{id}/builds</c> (author) asks for a build of a draft; <c>GET .../builds</c>, <c>.../builds/{build}</c> and <c>.../builds/{build}/preview[/{path}]</c> (viewer) read them and show the built app, read only; <c>POST .../builds/{build}/publish</c> (approver, <c>{"target","note"?}</c>) sends a succeeded build to a paired Gateway (<see cref="WorkspaceBuilds"/>, <see cref="WorkspacePreview"/>, <see cref="WorkspacePublishing"/>).</item>
/// <item><c>GET /api/v1/publisher-keys</c>, <c>PUT|DELETE /api/v1/publisher-keys/{name}</c> (admin, <c>{"secret_ref"}</c>): the reference to the secret that holds the publisher's key for a target. A key itself is refused. <c>POST /api/v1/publisher-keys/{name}/pair</c> (admin, <c>{"url","code"}</c>) makes the key and pairs it with a Gateway. <c>GET /api/v1/publish-targets</c> (approver): the targets a publish may name, with no reference.</item>
/// </list>
/// What happens to a draft is recorded in the state module as it happens; a record that cannot be written is logged and never turns a finished git operation into an error.
/// </summary>
public static class WorkspaceManagement
{
    public static void Map(IEndpointRouteBuilder app, string instanceId, string product, string version, WorkspaceStore store, WorkspaceProjects projects, IAuditLog audit, Action<string> log, Workspaces workspaces, WorkspaceBuilds builds, WorkspacePublishing publishing)
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
            string? id, title, kind;
            try { (id, title, kind) = (body?["id"]?.GetValue<string>(), body?["title"]?.GetValue<string>(), body?["kind"]?.GetValue<string>() ?? "app"); }
            catch (InvalidOperationException) { id = title = kind = null; }
            if (id is null || kind is null) return Results.Json(new { error = "The body is a JSON object with the project's \"id\" (and optionally a \"title\" and a \"kind\", app by default)." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var made = projects.Create(id, kind, title, new GitAuthor(Actor(token), $"{token.SubjectId}@dbdataworkspace.local"), token.SubjectId);
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
            var diagnostics = await kind.CheckAsync(workspaces.RootOf(workspace), context.RequestAborted);
            return Results.Json(new JsonObject
            {
                ["kind"] = kind.Name,
                ["ok"] = !diagnostics.Any(d => d.IsError),
                ["diagnostics"] = new JsonArray([.. diagnostics.Select(d => (JsonNode?)new JsonObject { ["severity"] = d.Severity, ["code"] = d.Code, ["message"] = d.Message, ["file"] = d.File, ["line"] = d.Line })]),
            });
        });

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
                ["publishes"] = builds.Publishes(hosted.Id, buildId),
            });
        });

        // the built app, for a preview: read only, from this server, nothing else (see WorkspacePreview)
        foreach (var route in new[] { "/api/v1/projects/{project}/workspaces/{id}/builds/{build}/preview", "/api/v1/projects/{project}/workspaces/{id}/builds/{build}/preview/{**path}" })
            app.MapGet(route, (string project, string id, string build, string? path, HttpContext context) =>
            {
                if (InProject(project, context, ServerRole.Viewer, out var hosted) is { } refused) return refused;
                if (!Guid.TryParse(build, out var buildId) || builds.Find(hosted.Id, id, buildId) is not { } found) return Results.NotFound();
                return WorkspacePreview.Serve(found.Row, builds.AppFolderOf(hosted.Id, buildId), path);
            });

        app.MapPost("/api/v1/projects/{project}/workspaces/{id}/builds/{build}/publish", async (string project, string id, string build, HttpContext context) =>
        {
            if (InProject(project, context, ServerRole.Approver, out var hosted) is { } refused) return refused;
            if (!Guid.TryParse(build, out var buildId) || builds.Find(hosted.Id, id, buildId) is not { } found) return Results.NotFound();
            var token = context.ManagementToken();
            var body = await Body(context);
            string? target, note;
            try { (target, note) = (body?["target"]?.GetValue<string>(), body?["note"]?.GetValue<string>()); }
            catch (InvalidOperationException) { target = note = null; }
            if (target is null) return Results.Json(new { error = "The body is a JSON object with the \"target\": the name of a publisher key (and optionally a \"note\")." }, statusCode: StatusCodes.Status400BadRequest);
            if (found.Row.Status != "succeeded") return Results.Json(new { error = $"This build is {found.Row.Status}, so there is nothing to publish. Only a build that succeeded is published." }, statusCode: StatusCodes.Status409Conflict);
            var steps = new List<string>();
            var target1 = $"{hosted.Id}/{id}/{buildId}";
            try
            {
                // the product the target is must be one the project's kind publishes to (plan 33 SC20b); a target nobody paired falls through to the publisher's own sentence
                if (publishing.Find(target) is { } paired && projects.Kinds.Find(store.FindProject(hosted.Id)!.Kind) is { } projectKind) WorkspacePublishing.RouteFor(projectKind, paired);
                var provenance = $"From workspace {id} of project {hosted.Id}, build {buildId:D}" + (found.Row.CommitSha is { } sha ? $", commit {sha[..Math.Min(12, sha.Length)]}" : "") +
                    (found.Result?["uncommitted_changes"]?.GetValue<bool>() == true ? " (with changes not yet committed)" : "") + $", published by {Actor(token)}.";
                var fullNote = string.IsNullOrWhiteSpace(note) ? provenance : note.Trim() + " | " + provenance;
                JsonObject answer;
                if (projects.Kinds.Find(store.FindProject(hosted.Id)!.Kind) is { Produces: true } producing)
                {
                    // a package for a Runner or models for a Gateway (plan 33 SC20d, SC21b, SC22b): what the kind made is sent with the key of the target, and what the Runner or Gateway said is the answer
                    var bind = new Dictionary<string, string>(StringComparer.Ordinal);
                    if (body?["bind"] is JsonObject slots)
                        foreach (var slot in slots)
                            if (slot.Value is JsonValue v && v.TryGetValue<string>(out var connection)) bind[slot.Key] = connection;
                    var options = new ArtifactPublishOptions(body?["channel"]?.GetValue<string>(), body?["serve"]?.GetValue<bool>() ?? false, body?["expected_version"]?.GetValue<int>(), bind, body?["data_source"]?.GetValue<string>());
                    answer = await publishing.PublishArtifactAsync(producing, target, builds.ArtifactFolderOf(hosted.Id, buildId), options, Actor(token), steps.Add, context.RequestAborted);
                    answer["note"] = fullNote;
                }
                else
                    answer = await publishing.PublishAsync(target, builds.AppFolderOf(hosted.Id, buildId), fullNote, Actor(token), steps.Add, context.RequestAborted);
                builds.RecordPublish(hosted.Id, buildId, new JsonObject { ["at"] = DateTimeOffset.UtcNow, ["by"] = Actor(token), ["target"] = target, ["ok"] = true, ["answer"] = answer.DeepClone() });
                Audit(token, "build.publish", $"{target1} -> {target}", "ok");
                return Results.Json(new JsonObject { ["steps"] = new JsonArray([.. steps.Select(x => (JsonNode?)x)]), ["published"] = answer });
            }
            catch (Exception ex) when (ex is InvalidOperationException or FormatException)
            {
                return Results.Json(new { error = "The body's \"channel\" and \"data_source\" are text, \"serve\" is true or false, \"expected_version\" a number and \"bind\" an object of slot to connection name." }, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (WorkspacePublishException ex)
            {
                builds.RecordPublish(hosted.Id, buildId, new JsonObject { ["at"] = DateTimeOffset.UtcNow, ["by"] = Actor(token), ["target"] = target, ["ok"] = false, ["error"] = ex.Message });
                Audit(token, "build.publish", $"{target1} -> {target}", "refused: " + ex.Message);
                return Results.Json(new JsonObject { ["error"] = ex.Message, ["steps"] = new JsonArray([.. steps.Select(x => (JsonNode?)x)]) }, statusCode: ex.Status);
            }
        });

        // ---- the publisher's key reference ----

        JsonObject DescribeKey(PublisherKeyRow k)
        {
            var target = publishing.Find(k.Name);
            return new JsonObject
            {
                ["name"] = k.Name, ["secret_ref"] = k.SecretRef, ["updated"] = k.UpdatedAt,
                ["product"] = target?.Product, ["gateway"] = target is null ? null : new JsonObject { ["url"] = target.Url, ["product"] = target.Product, ["roles"] = new JsonArray([.. target.Roles.Select(x => (JsonNode?)x)]), ["client_name"] = target.ClientName, ["paired"] = target.PairedAt },
            };
        }

        app.MapGet("/api/v1/publisher-keys", (HttpContext context) =>
        {
            if (ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin) is { Allowed: false } refused) return Refused(refused);
            return Results.Json(new JsonObject { ["keys"] = new JsonArray([.. store.PublisherKeys().Select(k => (JsonNode?)DescribeKey(k))]) });
        });

        // the targets a publish may name (their names and Gateways; no reference): for whoever publishes, who need not be an administrator
        // with ?project=<id>: whether a project of that kind may be published to the target (its kind's Publishes lists the target's product); without it, null
        bool? AcceptsFor(HttpContext context, PublishTarget? target)
        {
            if (target is null || context.Request.Query["project"].ToString() is not { Length: > 0 } id || store.FindProject(id) is not { } row || projects.Kinds.Find(row.Kind) is not { } kind) return null;
            return kind.Publishes.Any(r => r.Product == target.Product);
        }

        app.MapGet("/api/v1/publish-targets", (HttpContext context) =>
        {
            var token = context.ManagementToken();
            var decision = ManagementAuthorization.Authorize(token, ServerRole.Approver);
            if (!decision.Allowed && !token.Projects.Keys.Any(p => ManagementAuthorization.Authorize(token, ServerRole.Approver, p).Allowed)) return Refused(decision);
            return Results.Json(new JsonObject
            {
                ["targets"] = new JsonArray([.. store.PublisherKeys().Select(k => (JsonNode?)new JsonObject
                {
                    ["name"] = k.Name,
                    ["gateway"] = publishing.Find(k.Name)?.Url, ["product"] = publishing.Find(k.Name)?.Product,
                    ["accepts"] = AcceptsFor(context, publishing.Find(k.Name)),
                    ["can_publish"] = publishing.Find(k.Name) is { } t && ServerRoles.TryParse(t.Roles, out var held) && (ServerRoles.Allows(held, ServerRole.Publisher) || ServerRoles.Allows(held, ServerRole.Admin)),
                })]),
            });
        });

        app.MapPut("/api/v1/publisher-keys/{name}", async (string name, HttpContext context) =>
        {
            var token = context.ManagementToken();
            if (ManagementAuthorization.Authorize(token, ServerRole.Admin) is { Allowed: false } refused) return Refused(refused);
            if (!WorkspaceProjects.IsValidId(name)) return Results.Json(new { error = $"'{name}' is not a name: lower-case letters, digits, '-' and '_', at most 32." }, statusCode: StatusCodes.Status400BadRequest);
            string? reference;
            try { reference = (await Body(context))?["secret_ref"]?.GetValue<string>(); }
            catch (InvalidOperationException) { reference = null; }
            if (reference is null) return Results.Json(new { error = "The body is a JSON object with the \"secret_ref\": the reference to the secret that holds the key." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var row = store.SetPublisherKey(name, reference, token.SubjectId);
                Audit(token, "publisher-key.set", name, "ok");     // the reference is not a secret, but the audit names only the target
                return Results.Json(DescribeKey(row));
            }
            catch (ArgumentException ex)
            {
                Audit(token, "publisher-key.set", name, "refused");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });

        // makes the key and pairs it with a Gateway with the single-use code made there (the code is not a secret that stays: it is used up); the key never leaves the secret store
        app.MapPost("/api/v1/publisher-keys/{name}/pair", async (string name, HttpContext context) =>
        {
            var token = context.ManagementToken();
            if (ManagementAuthorization.Authorize(token, ServerRole.Admin) is { Allowed: false } refused) return Refused(refused);
            var body = await Body(context);
            string? url, code, clientName, targetProduct;
            try { (url, code, clientName, targetProduct) = (body?["url"]?.GetValue<string>(), body?["code"]?.GetValue<string>(), body?["client_name"]?.GetValue<string>(), body?["product"]?.GetValue<string>()); }
            catch (InvalidOperationException) { url = code = clientName = targetProduct = null; }
            if (url is null || code is null || !Uri.TryCreate(url, UriKind.Absolute, out var address))
                return Results.Json(new { error = "The body is a JSON object with the Gateway's \"url\" (https://...) and the \"code\" its administrator made (server pair --max-role publisher)." }, statusCode: StatusCodes.Status400BadRequest);
            try
            {
                var made = await publishing.PairAsync(name, address, code, clientName, token.SubjectId, context.RequestAborted, targetProduct);
                Audit(token, "publisher-key.pair", $"{name} -> {made.Url}", "ok");
                return Results.Json(DescribeKey(store.PublisherKeys().First(k => k.Name == name)), statusCode: StatusCodes.Status201Created);
            }
            catch (WorkspacePublishException ex)
            {
                Audit(token, "publisher-key.pair", name, "refused: " + ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        });

        app.MapDelete("/api/v1/publisher-keys/{name}", (string name, HttpContext context) =>
        {
            var token = context.ManagementToken();
            if (ManagementAuthorization.Authorize(token, ServerRole.Admin) is { Allowed: false } refused) return Refused(refused);
            if (!store.RemovePublisherKey(name)) return Results.NotFound();
            Audit(token, "publisher-key.remove", name, "ok");
            return Results.NoContent();
        });
    }
}
