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

    /// <summary>What a signed-in person may do in the hosted Workspace: until the workspace role namespace (SC06) a console administrator holds what the relay gives them on a paired Workspace server, a viewer reads.</summary>
    public static ServerRole RolesOf(UserRole role) => ServerRoles.Close(ConsoleRelay.RolesOf(role));

    /// <summary>The token the engine reads for the person signed in on <paramref name="context"/>.</summary>
    public static ValidatedToken SessionTokenOf(HttpContext context, ServerRole roles)
    {
        var user = context.User;
        var id = user.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? user.Identity?.Name ?? "unknown";
        var now = DateTimeOffset.UtcNow;
        var client = new PairedClient("console", "dbdatakit console", [], ServerRole.Admin | ServerRole.Operator | ServerRole.Author | ServerRole.Approver | ServerRole.Viewer | ServerRole.Publisher, ClientKind.Console);
        return new ValidatedToken(client, id, user.Identity?.Name, IsConsole: false, roles, new Dictionary<string, ServerRole>(), Guid.NewGuid().ToString("N"), now, now.AddMinutes(1));
    }

    private static UserRole UserRoleOf(HttpContext context) => context.User.IsInRole(nameof(UserRole.Admin)) ? UserRole.Admin : UserRole.Viewer;

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
                http.SetManagementToken(SessionTokenOf(http, RolesOf(UserRoleOf(http))));
                return await next(context);
            });
            WorkspaceManagement.Map(group, workspace.InstanceId, ConsoleSettings.Product, workspace.Version, workspace.Store, workspace.Projects, workspace.Audit, workspace.Log, workspace.Workspaces, workspace.Builds, workspace.Publishing);
        }
    }
}
