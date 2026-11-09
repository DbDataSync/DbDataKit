using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Upgrade;

/// <summary>
/// A server's upgrade of itself on its management API (plan 24a UC09). <c>GET /api/v1/upgrade</c> (viewer) is the status the console polls: the running version, whether the console may upgrade this install, the state
/// (<c>none</c>, <c>prepared</c>, <c>draining</c>, <c>applying</c>, <c>committed</c>, <c>rolled_back</c>, <c>rejected</c>, <c>failed</c>) and the journal's steps. <c>POST /api/v1/upgrade</c> (admin) takes <c>{"action": "prepare", "version": "optional"}</c>:
/// download, check and report, nothing changed; or <c>{"action": "apply"}</c>: drain, swap, health, rollback (answered 202 once the helper runs). Both are audited, refusals too. Mount it after
/// <c>UseManagementAuthentication</c>; the status is a GET, so a draining server (which refuses changes) still answers it.
/// </summary>
public static class UpgradeEndpoints
{
    public const string Path = "/api/v1/upgrade";
    public const string PrepareCommand = "upgrade.prepare";
    public const string ApplyCommand = "upgrade.apply";
    private const int MaxBody = 4 * 1024;

    public static RouteGroupBuilder MapUpgrade(this IEndpointRouteBuilder app, UpgradeService service, IAuditLog audit, TimeProvider? clock = null)
    {
        clock ??= TimeProvider.System;
        var group = app.MapGroup(Path);
        group.MapGet("", (HttpContext context) =>
        {
            var token = context.ManagementToken();
            return ManagementAuthorization.Authorize(token, ServerRole.Viewer).Allowed
                ? Results.Json(service.Status())
                : Results.Json(new { error = "This call needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden);
        });
        group.MapPost("", async (HttpContext context) =>
        {
            var token = context.ManagementToken();
            var at = clock.GetUtcNow();
            if (!ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed)
            {
                audit.Append(ManagementIdentity.Entry(token, "upgrade", null, "refused: the admin role is needed", at));
                return Results.Json(new { error = "This call needs the admin role." }, statusCode: StatusCodes.Status403Forbidden);
            }

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            var buffer = new char[MaxBody + 1];
            var read = await reader.ReadBlockAsync(buffer, context.RequestAborted);
            if (read > MaxBody) return Results.Json(new { error = "The body is larger than 4 KB." }, statusCode: StatusCodes.Status413PayloadTooLarge);
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(new string(buffer, 0, read)); }
            catch (System.Text.Json.JsonException) { parsed = null; }
            if (parsed is not JsonObject body || body["action"] is not JsonValue a || !a.TryGetValue<string>(out var action) || action is not ("prepare" or "apply"))
                return Results.Json(new { error = "The body is {\"action\": \"prepare\" or \"apply\", \"version\": \"optional\"}." }, statusCode: StatusCodes.Status400BadRequest);
            foreach (var (key, _) in body)
                if (key is not ("action" or "version")) return Results.Json(new { error = $"'{key}' is not a setting of the call (action, version)." }, statusCode: StatusCodes.Status400BadRequest);
            var version = body["version"] is JsonValue v && v.TryGetValue<string>(out var asked) ? asked : null;
            var command = action == "prepare" ? PrepareCommand : ApplyCommand;
            var target = version ?? service.Options.Version;
            try
            {
                if (action == "prepare")
                {
                    var report = await service.PrepareAsync(version, context.RequestAborted);
                    audit.Append(ManagementIdentity.Entry(token, command, $"{service.Options.Version} -> {report["to"]?.ToString() ?? "-"}", report["state"]?.ToString() ?? "ok", at));
                    return Results.Json(report);
                }

                var status = await service.ApplyAsync(context.RequestAborted);
                audit.Append(ManagementIdentity.Entry(token, command, $"{service.Options.Version} -> {status["plan"]?["to"]?.ToString() ?? "-"}", "started", at));
                return Results.Json(status, statusCode: StatusCodes.Status202Accepted);
            }
            catch (UpgradeException ex)
            {
                audit.Append(ManagementIdentity.Entry(token, command, target, "refused: " + ex.Message, at));
                return Results.Json(new { error = ex.Message }, statusCode: ex.Status);
            }
        });
        return group;
    }
}

/// <summary>Wires a server's upgrade of itself: one call in a product's host, after <c>UseManagementAuthentication</c> (plan 24a UC09).</summary>
public static class UpgradeSetup
{
    /// <summary>
    /// Builds the <see cref="UpgradeService"/> of a server and maps its endpoints. The feed is <see cref="UpgradeFeedSettings.FromEnvironment"/>. The server is upgradable
    /// from the console when it runs as a Windows service or a systemd unit (<paramref name="serviceName"/> is then used; by hand it is not) and was not installed by a package manager or an image.
    /// </summary>
    public static UpgradeService MapUpgrade(this WebApplication app, string product, string version, string serviceName, int port, IAuditLog audit, Func<IReadOnlyList<PeerGeneration>>? peers = null)
    {
        var running = OperatingSystem.IsWindows() ? !Environment.UserInteractive : Environment.GetEnvironmentVariable("INVOCATION_ID") is not null;
        var service = new UpgradeService(new UpgradeOptions
        {
            Product = product, Version = version, ProgramDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            ServiceName = running ? serviceName : null, HealthUrl = new Uri($"https://127.0.0.1:{port}/version"), Peers = peers ?? (() => []),
        }, ReleaseFeed.Create(UpgradeFeedSettings.FromEnvironment()), app.Services.GetRequiredService<DbDataKit.Hosting.IDrainCoordinator>(), new ProcessUpgradeLauncher());
        app.MapUpgrade(service, audit);
        return service;
    }
}
