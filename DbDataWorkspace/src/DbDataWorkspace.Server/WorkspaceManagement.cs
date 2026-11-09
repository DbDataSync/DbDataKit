using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;
using DbDataKit.Config.Git;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataWorkspace.Server;

/// <summary>
/// The management API of the Workspace server (plan 22a GP31). Every call needs a token a paired console issued, and the role the call says:
/// <list type="bullet">
/// <item><c>GET /api/v1/info</c>: who the server is (any role).</item>
/// <item><c>GET /api/v1/projects</c> (viewer) and <c>POST /api/v1/projects</c> (admin, <c>{"id", "title"?, "kind"?}</c>): the projects hosted, and a new one.</item>
/// <item>the workspace calls of <see cref="DbDataKit.Authoring.WorkspaceApi"/> (list, create, abandon, changes, history, commit, push, merge), the same as every kind of project has, and <c>GET /api/v1/projects/{project}/workspaces/{id}/events</c> (viewer): what happened to a draft, from the state module.</item>
/// <item><c>GET /api/v1/publisher-keys</c>, <c>PUT|DELETE /api/v1/publisher-keys/{name}</c> (admin, <c>{"secret_ref"}</c>): the reference to the secret that holds the publisher's key for a target. A key itself is refused.</item>
/// </list>
/// What happens to a draft is recorded in the state module as it happens; a record that cannot be written is logged and never turns a finished git operation into an error.
/// </summary>
public static class WorkspaceManagement
{
    public static void Map(IEndpointRouteBuilder app, string instanceId, WorkspaceSettings settings, WorkspaceStore store, WorkspaceProjects projects, IAuditLog audit, Action<string> log)
    {
        app.MapGet("/api/v1/info", () => Results.Json(new JsonObject
        {
            ["product"] = WorkspaceSettings.Product,
            ["version"] = WorkspaceServer.Version,
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

        var workspaces = new Workspaces(settings.Directory, TimeProvider.System);
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

        // ---- the publisher's key reference ----

        app.MapGet("/api/v1/publisher-keys", (HttpContext context) =>
        {
            if (ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Admin) is { Allowed: false } refused) return Refused(refused);
            return Results.Json(new { keys = store.PublisherKeys().Select(k => new { name = k.Name, secret_ref = k.SecretRef, updated = k.UpdatedAt }) });
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
                return Results.Json(new { name = row.Name, secret_ref = row.SecretRef, updated = row.UpdatedAt });
            }
            catch (ArgumentException ex)
            {
                Audit(token, "publisher-key.set", name, "refused");
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
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
