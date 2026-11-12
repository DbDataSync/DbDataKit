using System.Net;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// The roles the console's relay checks before it forwards a Gateway's publish call (<c>ServerProducts.GatewayRoutes</c> in DbDataKit.Server, plan 21 CH02) are the roles this Gateway asks for: a call made as a person who holds the role is not refused for its role, and
/// one made as a person who holds the role below it is refused with 403 by the Gateway itself. The rows are the console's table, copied (the console's tests pin its table to itself, these pin each row to a real Gateway).
/// </summary>
public sealed partial class ManagementTests
{
    private const string Sha = "a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1";

    public static TheoryData<string, string, ServerRole> RelayRows => new()
    {
        { "GET", "/api/v1/definitions/app-nav", ServerRole.Viewer },
        { "GET", "/api/v1/definitions/app-nav/menu", ServerRole.Viewer },
        { "GET", "/api/v1/definitions/app-nav/menu/versions/1", ServerRole.Viewer },
        { "PUT", "/api/v1/definitions/app-nav/menu", ServerRole.Admin },
        { "POST", "/api/v1/definitions/app-nav/menu/activate", ServerRole.Admin },
        { "POST", "/api/v1/definitions/app-nav/menu/switch", ServerRole.Admin },
        { "POST", "/api/v1/definitions/app-nav/menu/rollback", ServerRole.Admin },
        { "POST", "/api/v1/definitions/app-nav/menu/retire", ServerRole.Admin },
        { "POST", "/api/v1/definitions/app-nav/menu/prune", ServerRole.Admin },
        { "HEAD", "/api/v1/artifacts/" + Sha, ServerRole.Viewer },
        { "GET", "/api/v1/artifacts/" + Sha, ServerRole.Viewer },
        { "PUT", "/api/v1/artifacts/" + Sha, ServerRole.Admin },
        { "PUT", "/api/v1/bindings/prod/app-nav/menu", ServerRole.Admin },
        { "DELETE", "/api/v1/bindings/prod/app-nav/menu/slot", ServerRole.Admin },
    };

    [Theory]
    [MemberData(nameof(RelayRows))]
    public async Task The_gateway_asks_the_role_the_relay_asks_for_the_publish_route(string method, string path, ServerRole role)
    {
        await StartAsync();
        var server = await PairAsync();
        Assert.Equal(HttpStatusCode.OK, (await CallAsync(server, HttpMethod.Put, "/api/v1/settings/group", ServerRole.Admin, "{\"mode\":\"test\"}")).StatusCode);   // unsigned versions are accepted, so a refusal here is the role's
        var body = method switch
        {
            "PUT" when path.Contains("/definitions/", StringComparison.Ordinal) => JsonSerializer(new { manifest = "{\"title\":\"x\",\"entries\":[]}" }),
            "PUT" when path.Contains("/bindings/", StringComparison.Ordinal) => JsonSerializer(new { slots = new { source = "x" } }),
            "POST" => "{}",
            _ => null,
        };

        // the role itself is not refused for its role (whatever else the call answers: a missing definition, a body the Gateway does not take)
        var allowed = await CallAsync(server, new HttpMethod(method), path, role, body);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed.StatusCode);
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed.StatusCode);

        // the role below it is refused by the Gateway itself, which is what a person the relay let through by mistake would meet
        var below = role == ServerRole.Admin ? ServerRole.Operator : ServerRole.None;
        var refused = await CallAsync(server, new HttpMethod(method), path, below, body);
        Assert.True(refused.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"{method} {path} as {below}: {refused.StatusCode}");
    }
}
