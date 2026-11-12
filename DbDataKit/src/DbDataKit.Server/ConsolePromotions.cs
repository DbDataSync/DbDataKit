using System.Security.Claims;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server;

/// <summary>
/// Promotion between the groups a console manages (plan 30 PB06): <see cref="DefinitionPromoter"/> run by the console over the shared publish API of two servers, each reached <b>as the signed-in person</b> (the token the relay signs, the roles
/// the person holds there, the server's own check on top): the source is read as a viewer, the target is written as a publisher or an administrator, and each server records the person as the actor of what it was asked. A registered server is reached
/// through <see cref="ConsoleRelay.SendAsAsync"/> (for a runner only the calls of <see cref="ServerProducts.RunnerRoutes"/> pass), the console's embedded runner in this process, through its own routes with the person's roles.
/// </summary>
public sealed class ConsolePromotions(ConsoleInstances instances, ConsoleRelay relay, IServiceProvider services)
{
    /// <summary>The server a call names as a link as <paramref name="person"/> reaches it, or null when none is called that (a registered server by id or name, or the embedded runner).</summary>
    public IDefinitionsLink? LinkFor(string server, RelayPerson person)
    {
        if (server == ConsoleRunnerSettings.ServerId) return services.GetService<ConsoleRunner>() is null ? null : new EmbeddedLink(services, person);
        return instances.Registry.Find(server) is { } instance ? new RegisteredLink(relay, instance, person) : null;
    }

    /// <summary>Promotes <paramref name="request"/> from <paramref name="from"/> to <paramref name="to"/> as <paramref name="person"/>. A server that is not registered or a promotion a group refuses is a <see cref="DefinitionPromotionException"/> with the group's words.</summary>
    public async Task<Promotion> PromoteAsync(string from, string to, PromoteRequest request, RelayPerson person, CancellationToken ct)
    {
        var source = LinkFor(from, person) ?? throw new DefinitionPromotionException($"No server '{from}' is registered.", 404, "not_found");
        var target = LinkFor(to, person) ?? throw new DefinitionPromotionException($"No server '{to}' is registered.", 404, "not_found");
        return await new DefinitionPromoter().PromoteAsync(source, target, request, null, ct);
    }

    private sealed class RegisteredLink(ConsoleRelay relay, RegisteredInstance instance, RelayPerson person) : IDefinitionsLink
    {
        public string Name => instance.Name;

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct) =>
            relay.SendAsAsync(instance, person.Id, person.Name, person.Role, method, "api/v1/" + path, content, ct);
    }

    /// <summary>
    /// The console's embedded runner, answered in this process: the request is matched against the console's own routes (<see cref="ConsoleRunnerApi.Prefix"/>), run with the person as the signed-in user (the runner's routes decide their roles from it, as
    /// for a screen) and the answer is read whole. A file is held in memory while it passes (the runner's cap on a file is the limit); the policy middleware of the pipeline does not run, but every route of the runner checks the role itself.
    /// </summary>
    private sealed class EmbeddedLink(IServiceProvider services, RelayPerson person) : IDefinitionsLink
    {
        public string Name => "the console's runner";

        public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content, CancellationToken ct)
        {
            var (route, query) = path.Split('?', 2) is [var r, var q] ? (r, "?" + q) : (path, "");
            var full = ConsoleRunnerApi.Prefix + "/" + route;
            using var scope = services.CreateScope();
            var endpoint = Match(scope.ServiceProvider, method.Method, full, out var values);
            if (endpoint is null) return Answer(404, """{"error":"The embedded runner has no such call."}""");

            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider, RequestAborted = ct };
            var claims = new List<Claim> { new(ConsoleHost.Auth.UserIdClaim, person.Id), new(ClaimTypes.Name, person.Name ?? person.Id) };
            if (person.Role == UserRole.Admin) claims.Add(new Claim(ClaimTypes.Role, nameof(UserRole.Admin)));
            context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "promotion", ClaimTypes.Name, ClaimTypes.Role));
            context.Request.Method = method.Method;
            context.Request.Path = full;
            context.Request.QueryString = new QueryString(query);
            context.Request.RouteValues = values;
            context.SetEndpoint(endpoint);
            if (content is not null)
            {
                var body = new MemoryStream();
                await content.CopyToAsync(body, ct);
                body.Position = 0;
                context.Request.Body = body;
                context.Request.ContentLength = body.Length;
                context.Request.ContentType = content.Headers.ContentType?.ToString();
            }

            var output = new MemoryStream();
            context.Response.Body = output;
            await endpoint.RequestDelegate!(context);
            var answer = new HttpResponseMessage((System.Net.HttpStatusCode)context.Response.StatusCode) { Content = new ByteArrayContent(output.ToArray()) };
            if (context.Response.ContentType is { Length: > 0 } type) answer.Content.Headers.TryAddWithoutValidation("Content-Type", type);
            return answer;
        }

        private static RouteEndpoint? Match(IServiceProvider provider, string method, string path, out RouteValueDictionary values)
        {
            values = [];
            RouteEndpoint? best = null;
            var bestScore = -1;
            foreach (var endpoint in provider.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>())
            {
                if (endpoint.RoutePattern.RawText is not { } text || endpoint.Metadata.GetMetadata<HttpMethodMetadata>() is not { } methods || !methods.HttpMethods.Contains(method, StringComparer.OrdinalIgnoreCase)) continue;
                var found = new RouteValueDictionary();
                if (!new TemplateMatcher(TemplateParser.Parse(text), new RouteValueDictionary()).TryMatch(path, found)) continue;
                var score = endpoint.RoutePattern.PathSegments.Count(s => s.Parts.All(p => p is Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart));   // the route with the most literal segments is the more specific one
                if (score <= bestScore) continue;
                best = endpoint;
                bestScore = score;
                values = found;
            }

            return best;
        }

        private static HttpResponseMessage Answer(int status, string json) =>
            new((System.Net.HttpStatusCode)status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    }
}
