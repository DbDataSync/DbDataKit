using DbDataKit.Authoring.Server;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server;

/// <summary>
/// The management API of the Workspace hosted in the console (plan 33 SC05): the calls <c>dbdataworkspace</c> serves (<see cref="WorkspaceManagement"/> and the workspace calls of <c>DbDataKit.Authoring</c>), for the people signed in to the console
/// instead of a paired console's token. The person is the caller: the session becomes the token the engine reads (<see cref="SessionTokenOf"/>), with the person's workspace roles, so the engine's role rules, per-project scope and audit are the
/// ones the standalone server has. Mapped twice:
/// <list type="bullet">
/// <item><c>/api/embedded-workspace/api/v1/...</c>, the console's own address for it;</item>
/// <item><c>/relay/console-workspace/api/v1/...</c>, the address the Workspaces screens call for a server whose id is <c>console-workspace</c>, so they work unchanged. It is a literal route and so beats <c>/relay/{server}/{**path}</c>.</item>
/// </list>
/// Only the calls in <see cref="ServerProducts.WorkspaceRoutes"/> pass, as at the relay of a paired Workspace server: the pairing, state and maintenance calls of the standalone server do not exist here, and neither does any other.
/// </summary>
public static class ConsoleWorkspaceApi
{
    public const string Prefix = "/api/embedded-workspace";
    public const string RelayPrefix = "/relay/" + ConsoleWorkspaceSettings.ServerId;

    /// <summary>The token the engine reads for the person signed in on <paramref name="context"/>.</summary>
    public static ValidatedToken SessionTokenOf(HttpContext context, ServerRole roles, IReadOnlyDictionary<string, ServerRole>? projects = null)
    {
        var user = context.User;
        var id = user.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? user.Identity?.Name ?? "unknown";
        var now = DateTimeOffset.UtcNow;
        var client = new PairedClient("console", "dbdatakit console", [], ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver | ServerRole.Viewer | ServerRole.Publisher, ClientKind.Console);
        return new ValidatedToken(client, id, user.Identity?.Name, IsConsole: false, roles, projects ?? new Dictionary<string, ServerRole>(), Guid.NewGuid().ToString("N"), now, now.AddMinutes(1));
    }

    private static string UserIdOf(HttpContext context) => context.User.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? context.User.Identity?.Name ?? "unknown";

    public const string NoRole = "You hold no role in the Workspace. A console administrator is not a Workspace administrator: someone who holds the Workspace admin role (or `dbdatakit workspace role grant` on the machine) grants you viewer, author, publisher or admin.";

    public static void MapEmbeddedWorkspace(this WebApplication app)
    {
        if (app.Services.GetService<ConsoleWorkspace>() is not { } workspace) return;
        foreach (var prefix in new[] { Prefix, RelayPrefix })
        {
            var group = app.MapGroup(prefix).RequireAuthorization(Policies.Viewer);
            // a call outside the screens' table does not exist here, whoever signed in
            group.AddEndpointFilter(async (context, next) =>
            {
                var http = context.HttpContext;
                var path = http.Request.Path.Value![prefix.Length..].TrimStart('/');
                if (!(http.Request.Method == HttpMethods.Get && path == "api/v1/info") && !ServerProducts.Relayable(ServerProducts.Workspace, http.Request.Method, path))
                    return Results.Json(new { error = ServerProducts.RefusalFor(ServerProducts.Workspace, "The console's Workspace") }, statusCode: StatusCodes.Status403Forbidden);
                // the Workspace's roles are its own: a console administrator who was granted none is refused every call, reads included
                var (roles, projects) = workspace.RolesOf(UserIdOf(http));
                if (roles == ServerRole.None && projects.Count == 0) return Results.Json(new { error = NoRole }, statusCode: StatusCodes.Status403Forbidden);
                http.SetManagementToken(SessionTokenOf(http, roles, projects));
                return await next(context);
            });
            var own = app.MapGroup(prefix).RequireAuthorization(Policies.Viewer);
            if (prefix == Prefix) MapRoles(own, workspace);
            WorkspaceManagement.Map(group, workspace.InstanceId, ConsoleSettings.Product, workspace.Version, workspace.Store, workspace.Projects, workspace.Audit, workspace.Log, workspace.Workspaces, workspace.Builds, workspace.Publishing);
        }
    }

    /// <summary>
    /// The grants of the workspace roles: <c>GET roles/me</c> (anyone signed in: what they hold, none included, so a screen can say so), <c>GET roles</c> and <c>PUT|DELETE roles/{user}</c> (the Workspace admin role; <c>{"roles":["author"],"project":"orders"?}</c>), each change
    /// audited with the person who made it. Nothing here is a console role: <c>user list</c> says who the ids are.
    /// </summary>
    private static void MapRoles(RouteGroupBuilder group, ConsoleWorkspace workspace)
    {
        IResult? NeedAdmin(HttpContext http, out ValidatedToken token)
        {
            var (roles, projects) = workspace.RolesOf(UserIdOf(http));
            token = SessionTokenOf(http, roles, projects);
            return ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed ? null : Results.Json(new { error = "This needs the Workspace admin role." }, statusCode: StatusCodes.Status403Forbidden);
        }

        void Audit(ValidatedToken token, string command, string target, string outcome) =>
            workspace.Audit.Append(new AuditEntry(TimeProvider.System.GetUtcNow(), token.Client.Id, token.Client.Name, token.SubjectId, token.SubjectName, false, command, target, outcome, token.TokenId));

        object Describe(IEnumerable<WorkspaceRoleRow> rows) => new { grants = rows.Select(r => new { user = r.UserId, project = r.Project, roles = r.Roles, granted_by = r.GrantedBy, granted = r.GrantedAt }) };

        group.MapGet("/roles/me", (HttpContext http) => Results.Json(Describe(workspace.Store.Roles(UserIdOf(http)))));
        group.MapGet("/roles", (HttpContext http) => NeedAdmin(http, out _) ?? Results.Json(Describe(workspace.Store.Roles())));
        group.MapPut("/roles/{user}", async (string user, HttpContext http) =>
        {
            if (NeedAdmin(http, out var token) is { } refused) return refused;
            System.Text.Json.Nodes.JsonObject? body;
            try { body = await http.Request.ReadFromJsonAsync<System.Text.Json.Nodes.JsonObject>(http.RequestAborted); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException) { body = null; }
            var roles = body?["roles"] is System.Text.Json.Nodes.JsonArray list ? list.Select(r => r?.GetValue<string>() ?? "").ToList() : null;
            if (roles is null) return Results.Json(new { error = "The body is a JSON object with the \"roles\" (a list of viewer, author, publisher, admin; empty removes them) and optionally a \"project\"." }, statusCode: StatusCodes.Status400BadRequest);
            var project = body?["project"]?.GetValue<string>();
            try
            {
                workspace.Store.SetRoles(user, project, roles, token.SubjectId);
                Audit(token, "role.set", $"{user}{(project is null ? "" : "/" + project)}", "ok");
                return Results.Json(Describe(workspace.Store.Roles(user)));
            }
            catch (ArgumentException ex)
            {
                Audit(token, "role.set", user, "refused: " + ex.Message);
                return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status400BadRequest);
            }
        });
        group.MapDelete("/roles/{user}", (string user, string? project, HttpContext http) =>
        {
            if (NeedAdmin(http, out var token) is { } refused) return refused;
            workspace.Store.SetRoles(user, project, [], token.SubjectId);
            Audit(token, "role.set", $"{user}{(project is null ? "" : "/" + project)}", "ok");
            return Results.NoContent();
        });
    }
}
