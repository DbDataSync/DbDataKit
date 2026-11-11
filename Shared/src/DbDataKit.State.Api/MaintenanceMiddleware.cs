using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;

namespace DbDataKit.State.Api;

/// <summary>The middleware of maintenance mode (plan 29b, C05).</summary>
public static class MaintenanceMiddleware
{
    /// <summary>The seconds a refused client is told to wait (<c>Retry-After</c>).</summary>
    public const int RetryAfterSeconds = 30;

    /// <summary>
    /// While <paramref name="gate"/> is in maintenance, answers 503 with <c>Retry-After</c> and a JSON body naming the reason to every request except: the state endpoints of the management API (<see cref="StateManagement.Prefix"/>), the
    /// shared <c>/health</c> and <c>/version</c>, pairing and key rotation (<c>/management/</c>), and requests <paramref name="isAdmin"/> says come from an administrator (the person who put the server in maintenance must be able to go on).
    /// Every other request is counted while it is answered, so the gate can say when the server is idle.
    /// <para>
    /// <paramref name="exemptPrefixes"/> names more paths that always answer (the console keeps its sign-in open, so an administrator whose session ended can come back and end maintenance).
    /// </para>
    /// <para>
    /// Place it after the authentication that <paramref name="isAdmin"/> reads (after <c>UseManagementAuthentication</c> for a token product, after <c>UseAuthentication</c> for a session product); a request that has no caller yet is not an administrator's.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UseMaintenance(this IApplicationBuilder app, IMaintenanceGate gate, Func<HttpContext, bool>? isAdmin = null, IReadOnlyList<string>? exemptPrefixes = null) =>
        app.Use(async (context, next) =>
        {
            if (Exempt(context.Request.Path, exemptPrefixes) || (isAdmin?.Invoke(context) ?? false))
            {
                await next();
                return;
            }

            // counted before the flag is read: a request that got in just before maintenance began is in the count, one that arrives after is refused
            using var tracked = gate.Track();
            if (!gate.Enabled)
            {
                await next();
                return;
            }

            var status = gate.Status();
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            context.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            context.Response.ContentType = "application/json; charset=utf-8";
            var reason = string.IsNullOrWhiteSpace(status.Reason) ? "maintenance" : status.Reason;
            await context.Response.WriteAsync(new JsonObject
            {
                ["error"] = $"This server is in maintenance and takes no new work: {reason}",
                ["maintenance"] = true,
                ["reason"] = status.Reason,
                ["since"] = status.Since?.ToString("O", CultureInfo.InvariantCulture),
                ["retry_after_seconds"] = RetryAfterSeconds,
            }.ToJsonString(), context.RequestAborted);
        });

    private static bool Exempt(PathString path, IReadOnlyList<string>? more)
    {
        var text = path.Value ?? "";
        return more?.Any(prefix => text.StartsWith(prefix, StringComparison.Ordinal)) == true
            || text.StartsWith(StateManagement.Prefix, StringComparison.Ordinal)
            || text.StartsWith("/health", StringComparison.Ordinal) || text == "/version"
            || text.StartsWith("/management/", StringComparison.Ordinal);
    }
}
