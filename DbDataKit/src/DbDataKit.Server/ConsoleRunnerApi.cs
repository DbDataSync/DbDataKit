using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server;

/// <summary>
/// The embedded runner's management API on the console (plan 28a R20), the calls of <c>DbDataKit.Runner.Api</c> that <c>dbdatarunner</c> serves, for the people signed in to the console instead of a paired token: a viewer reads, an
/// administrator acts (run now, pause, resume and an operator's act are the administrator's here, as on a relayed runner). Mapped twice:
/// <list type="bullet">
/// <item><c>/api/embedded-runner/...</c>, the console's own address for it;</item>
/// <item><c>/relay/console-runner/api/v1/...</c>, the address the runner screens (R19) call for a server whose id is <c>console-runner</c>, so they work unchanged. It is a literal route and so beats <c>/relay/{server}/{**path}</c>; every other call under that id is refused by the relay
/// (<see cref="ServerProducts.Relayable"/>) and never reaches a runner.</item>
/// </list>
/// Besides the runner's calls: <c>GET posture</c>, <c>POST accept-production</c> (an administrator's recorded choice, which needs <c>{"confirmed": true}</c>) and <c>POST revoke-production</c>.
/// </summary>
public static class ConsoleRunnerApi
{
    public const string Prefix = "/api/embedded-runner";
    public const string RelayPrefix = "/relay/" + ConsoleRunnerSettings.ServerId + "/api/v1";

    /// <summary>Who is calling: the signed-in person, as the runner's roles (an administrator is an operator and an administrator, a viewer reads).</summary>
    public static RunnerCaller CallerOf(HttpContext context)
    {
        var user = context.User;
        var id = user.FindFirst(ConsoleHost.Auth.UserIdClaim)?.Value ?? user.Identity?.Name ?? "unknown";
        var role = user.IsInRole(nameof(UserRole.Admin)) ? UserRole.Admin : UserRole.Viewer;
        return new RunnerCaller(new RunnerActor(id, user.Identity?.Name, "console", ConsoleRunnerSettings.ServerId), ConsoleRunner.RolesOf(role));
    }

    public static void MapEmbeddedRunner(this WebApplication app)
    {
        if (app.Services.GetService<ConsoleRunner>() is not { } runner) return;
        var state = app.Services.GetRequiredService<StateDatabase>();
        RunnerHost Engine() => runner.Host!;

        foreach (var prefix in new[] { Prefix, RelayPrefix })
        {
            var group = app.MapGroup(prefix).RequireAuthorization(Policies.Viewer);
            // before the console has started the runner, and after it has stopped it, there is nothing to ask
            group.AddEndpointFilter(async (context, next) => runner.Host is null
                ? Results.Json(new { error = "The console's embedded runner is not running." }, statusCode: StatusCodes.Status503ServiceUnavailable)
                : await next(context));
            var options = new RunnerApiOptions("", CallerOf, ConsoleSettings.Product, ConsoleHost.Version) { DecorateStatus = runner.DecorateStatus };
            RunnerApi.Map(group, state, Engine, ConsoleRunnerSettings.ServerId, options);
            RunnerGroupApi.Map(group, state, Engine, "", CallerOf, ConsoleState.ServiceId);
            RunnerConnectionCopyApi.Map(group, state, Engine, "", CallerOf, ConsoleState.ServiceId);   // plan 27a IX23: the connections the console handed its runner, with their secret references and whether each resolves there
            RunnerTrackingApi.Map(group, state, "", CallerOf, ConsoleState.ServiceId);   // plan 28c E18: the embedded runner's own copy of Build's tracking
            RunnerPublishApi.MapReadAndLifecycle(group, runner.Publishing, "", CallerOf);
            // upload, publish, bind and the require-signatures setting (plan 33 UE08, UE10): an administrator signed in here does them, a viewer is refused and audited; no owner rules, since no client's token is involved
            RunnerPublishApi.MapWrite(group, runner.Publishing, "", CallerOf);
        }

        var own = app.MapGroup(Prefix).RequireAuthorization(Policies.Viewer);
        own.MapGet("/posture", () => Results.Json(Posture(runner)));
        own.MapPost("/accept-production", async (HttpContext context) =>
        {
            var caller = CallerOf(context);
            if (!caller.Authorize(ServerRole.Admin).Allowed) return Results.Json(new { error = "This needs the admin role." }, statusCode: StatusCodes.Status403Forbidden);
            var body = await Body(context);
            var confirmed = body?["confirmed"] is JsonValue c && c.TryGetValue<bool>(out var yes) && yes;
            var note = body?["note"] is JsonValue n && n.TryGetValue<string>(out var text) ? text : null;
            try
            {
                runner.AcceptProduction(caller.Actor.Id, note, confirmed);
                return Results.Json(Posture(runner));
            }
            catch (PostureRefusedException ex)
            {
                return Results.Json(new { error = ex.Message, effect = ex.Setting.Effect, setting = ex.Setting.Name }, statusCode: StatusCodes.Status409Conflict);
            }
        });
        own.MapPost("/revoke-production", (HttpContext context) =>
        {
            var caller = CallerOf(context);
            if (!caller.Authorize(ServerRole.Admin).Allowed) return Results.Json(new { error = "This needs the admin role." }, statusCode: StatusCodes.Status403Forbidden);
            runner.RevokeProduction(caller.Actor.Id);
            return Results.Json(Posture(runner));
        });
    }

    private static JsonObject Posture(ConsoleRunner runner)
    {
        var status = new JsonObject();
        runner.DecorateStatus(status);
        return (JsonObject)status["console_runner"]!.DeepClone();
    }

    private static async Task<JsonObject?> Body(HttpContext context)
    {
        try { return await context.Request.ReadFromJsonAsync<JsonObject>(context.RequestAborted); }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or BadHttpRequestException) { return null; }
    }
}
