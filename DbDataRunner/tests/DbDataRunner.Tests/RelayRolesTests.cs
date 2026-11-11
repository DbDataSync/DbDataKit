using System.Net;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataRunner.Tests;

/// <summary>
/// The roles the console's relay checks before it forwards a call (<c>ServerProducts.RunnerRoutes</c> in DbDataKit.Server, owner decision of 2026-11-08) are the roles this runner asks for: a call made as a person who holds the role is not refused
/// for its role, and one made as a person who holds the role below it is refused with 403 by the runner itself (defence in depth, the relay's check is not the only one). The rows are the console's table, copied; the console's tests fail when its table
/// and the list there differ, and these when the runner's answer differs from a row.
/// </summary>
public sealed class RelayRolesTests
{
    private const string Instance = "6f1c0f1e-1111-4222-8333-444455556666";

    /// <summary>Method, path (with names filled in), the role the relay asks (the same as the runner's).</summary>
    public static TheoryData<string, string, ServerRole> Rows => new()
    {
        { "GET", "/api/v1/status", ServerRole.Viewer }, { "GET", "/api/v1/instances", ServerRole.Viewer }, { "GET", "/api/v1/connections", ServerRole.Viewer },
        { "GET", "/api/v1/runs", ServerRole.Viewer }, { "GET", "/api/v1/settings", ServerRole.Viewer }, { "GET", "/api/v1/lineage", ServerRole.Viewer }, { "GET", "/api/v1/certificate", ServerRole.Viewer }, { "GET", "/api/v1/secrets/overview", ServerRole.Admin },
        { "GET", $"/api/v1/runs/{Instance}", ServerRole.Viewer }, { "GET", $"/api/v1/runs/{Instance}/logs", ServerRole.Viewer },
        { "GET", "/api/v1/definitions/integration", ServerRole.Viewer }, { "GET", "/api/v1/definitions/integration/orders", ServerRole.Viewer },
        { "GET", $"/api/v1/instances/{Instance}/labels", ServerRole.Viewer },
        { "GET", "/api/v1/tasks/orders/dev/load/schedule", ServerRole.Viewer }, { "GET", "/api/v1/tasks/orders/dev/load/decisions", ServerRole.Viewer },
        { "HEAD", "/api/v1/artifacts/" + "a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1", ServerRole.Viewer },
        { "POST", "/api/v1/tasks/orders/dev/load/run", ServerRole.Operator }, { "POST", "/api/v1/tasks/orders/dev/load/pause", ServerRole.Operator },
        { "POST", "/api/v1/tasks/orders/dev/load/resume", ServerRole.Operator }, { "PUT", "/api/v1/tasks/orders/dev/load/owner", ServerRole.Operator },
        { "POST", "/api/v1/definitions/integration/orders/activate", ServerRole.Admin }, { "POST", "/api/v1/definitions/integration/orders/switch", ServerRole.Admin },
        { "POST", "/api/v1/definitions/integration/orders/rollback", ServerRole.Admin }, { "POST", "/api/v1/definitions/integration/orders/retire", ServerRole.Admin },
        { "PUT", "/api/v1/artifacts/" + "a0b1c2d3e4f5a6b7c8d9e0f1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b1", ServerRole.Admin },
        { "PUT", "/api/v1/definitions/integration/orders", ServerRole.Admin }, { "PUT", "/api/v1/bindings/dev/integration/orders", ServerRole.Admin },
        { "PUT", "/api/v1/settings/require_signatures", ServerRole.Admin }, { "PUT", $"/api/v1/instances/{Instance}/labels", ServerRole.Admin },
    };

    private static object? BodyOf(string method, string path) => method switch
    {
        "PUT" when path.EndsWith("/definitions/integration/orders", StringComparison.Ordinal) => FakePackage.Make("orders", "1", SamplePackage.Task("load")).Body("dev", "stage"),
        "PUT" when path.Contains("/bindings/", StringComparison.Ordinal) => new { slots = new { source = "erp" } },
        "PUT" when path.EndsWith("/require_signatures", StringComparison.Ordinal) => new { value = false },
        "PUT" when path.EndsWith("/labels", StringComparison.Ordinal) => new { labels = new[] { "gpu" } },
        "PUT" when path.EndsWith("/owner", StringComparison.Ordinal) => new { owner = "runner", reason = "relay roles test" },
        "POST" => new { channel = "dev" },
        _ => null,
    };

    private static string Path(string path, RunnerHarness h) => path.Replace(Instance, h.Engine.InstanceId.ToString());

    [Theory]
    [MemberData(nameof(Rows))]
    public async Task The_runner_asks_the_role_the_relay_asks_for_the_route(string method, string path, ServerRole role)
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await h.StartAsync();
        var call = Path(path, h);
        var body = BodyOf(method, path);

        // the role itself is not refused for its role (whatever else the call answers: a missing task, a body the runner does not take)
        var (allowed, _) = await h.CallAsync(new HttpMethod(method), call, role, body);
        Assert.NotEqual(HttpStatusCode.Forbidden, allowed);
        Assert.NotEqual(HttpStatusCode.Unauthorized, allowed);

        // the role below it is refused by the runner itself, which is what a person that the relay let through by mistake would meet
        var below = role switch { ServerRole.Admin => ServerRole.Operator, ServerRole.Operator => ServerRole.Viewer, _ => ServerRole.None };
        var (refused, _) = await h.CallAsync(new HttpMethod(method), call, below, body);
        Assert.True(refused is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, $"{method} {path} as {below}: {refused}");
    }
}
