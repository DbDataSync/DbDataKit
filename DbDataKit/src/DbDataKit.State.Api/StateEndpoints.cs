using System.Text;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.State.Api;

/// <summary>Maps the state endpoints on a product's management API (plan 29b, C05).</summary>
public static class StateEndpoints
{
    private const int MaxBody = 8 * 1024;

    /// <summary>
    /// Maps <c>GET /api/v1/state</c>, <c>GET /api/v1/state/maintenance</c> and <c>POST /api/v1/state/maintenance</c>. <paramref name="caller"/> says who is calling; the default is the console's token that
    /// <c>UseManagementAuthentication</c> validated, so the endpoints must be under its prefix. The returned group is for conventions (a product that signs people in with a session adds its authorization policy there).
    /// </summary>
    public static RouteGroupBuilder MapState(this IEndpointRouteBuilder app, StateManagement management, Func<HttpContext, StateCaller>? caller = null)
    {
        caller ??= context => StateCaller.FromToken(context.ManagementToken());
        var group = app.MapGroup(StateManagement.Prefix);
        group.MapGet("", (Delegate)((HttpContext context) => RunAsync(management, context, caller, null)));
        group.MapGet("/maintenance", (Delegate)((HttpContext context) => RunAsync(management, context, caller, null)));
        group.MapPost("/maintenance", async (HttpContext context) =>
        {
            // a small body: the call is {"enabled": ..., "reason": "..."}
            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var buffer = new char[MaxBody + 1];
            var read = await reader.ReadBlockAsync(buffer, context.RequestAborted);
            if (read > MaxBody) return Results.Json(new { error = "The body is larger than 8 KB." }, statusCode: StatusCodes.Status413PayloadTooLarge);
            return await RunAsync(management, context, caller, new string(buffer, 0, read));
        });
        return group;
    }

    private static async Task<IResult> RunAsync(StateManagement management, HttpContext context, Func<HttpContext, StateCaller> caller, string? body)
    {
        var result = await management.HandleAsync(context.Request.Method, context.Request.Path.Value!, body, caller(context), context.RequestAborted);
        return Results.Json(result.Body, statusCode: result.Status);
    }
}
