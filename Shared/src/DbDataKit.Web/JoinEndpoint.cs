using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Web.Trust;

/// <summary>
/// The console's join route: <c>POST</c> <see cref="Path"/> takes a <see cref="JoinRequest"/> and answers with a <see cref="JoinAnswer"/>, or with the
/// status of <see cref="JoinOutcome"/> and <c>{"error": ...}</c>. A joining server has no credentials but its proof, so the route takes none: the host maps
/// it where its sign-in does not apply.
/// </summary>
public static class JoinEndpoint
{
    public const string Path = "/api/v1/join";
    private const int MaxBody = 8192;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="joined">Hears each server the console joined, before the answer is sent: the host records and pins it there.</param>
    /// <param name="app">The console's routes.</param>
    /// <param name="service">The console's half of the join.</param>
    public static IEndpointRouteBuilder MapJoin(this IEndpointRouteBuilder app, JoinService service, Func<JoinedServer, CancellationToken, Task>? joined = null)
    {
        app.MapJoin((_, _) => Task.FromResult(service), joined);
        return app;
    }

    /// <summary>The join route with the console's half made for each request, so a console whose signing key was rotated answers with the key it signs with now.</summary>
    /// <returns>The route, for the host to take it out of its sign-in (<c>AllowAnonymous</c>).</returns>
    public static RouteHandlerBuilder MapJoin(this IEndpointRouteBuilder app, Func<HttpContext, CancellationToken, Task<JoinService>> service, Func<JoinedServer, CancellationToken, Task>? joined = null) =>
        app.MapPost(Path, async (HttpContext http) =>
        {
            // a host whose middleware already read the body (to log it) has fixed the limit; the route then keeps the host's
            if (http.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = MaxBody;
            JoinRequest? request;
            try { request = await http.Request.ReadFromJsonAsync<JoinRequest>(Json, http.RequestAborted); }
            catch (Exception ex) when (ex is JsonException or BadHttpRequestException or InvalidOperationException) { return Results.Json(new { error = "the request is not valid JSON" }, Json, statusCode: 400); }
            if (request is null) return Results.Json(new { error = "the request is empty" }, Json, statusCode: 400);
            var outcome = await (await service(http, http.RequestAborted)).JoinAsync(request, http.RequestAborted);
            if (!outcome.Ok)
            {
                http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("DbDataKit.Web.Join").LogWarning("A join was refused: {Reason}", outcome.Reason);
                return Results.Json(new { error = outcome.Told }, Json, statusCode: outcome.Status);
            }
            if (joined is not null) await joined(outcome.Server!, http.RequestAborted);
            return Results.Json(outcome.Answer, Json);
        });
}
