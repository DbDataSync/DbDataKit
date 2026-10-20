using System.Net;
using DbDataKit.Hosting.Certificates;
using DbDataSync.Api.Configuration;
using Microsoft.AspNetCore.HttpOverrides;

namespace DbDataSync.Api;

/// <summary>
/// Refuses a plain-HTTP request from a non-local client unless it came through a TLS-terminating proxy this deployment
/// trusts (<see cref="PlainHttpGuard"/>). The proxy says so with <c>X-Forwarded-Proto: https</c>, believed only from
/// <see cref="ApiOptions.TrustedProxies"/>; <see cref="ApiOptions.AllowInsecureHttp"/> turns the whole check off.
/// <para>
/// Loopback clients are always served (the CLI's health probe, a proxy on the same host), as is <c>/api/health</c> (a
/// load balancer's probe, which carries no data).
/// </para>
/// </summary>
public static class PlainHttpProtection
{
    public const string HealthPath = "/api/health";

    /// <summary>Adds forwarded-header handling (when proxies are trusted) and the guard to the pipeline. Call first.</summary>
    public static void Apply(WebApplication app, ApiOptions options)
    {
        if (options.TrustedProxies.Count > 0)
            app.UseForwardedHeaders(ForwardedHeaders(options.TrustedProxies));

        if (options.AllowInsecureHttp)
            return;

        app.Use(async (context, next) =>
        {
            if (PlainHttpGuard.RequestAllowed(context.Request.IsHttps, context.Connection.RemoteIpAddress)
                || context.Request.Path.Equals(HealthPath, StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                "HTTPS is required. This request arrived over plain HTTP from a non-local client. Use the https address; " +
                "behind a TLS-terminating proxy, set DbDataSync:App:TrustedProxies to the proxy's address so its " +
                "X-Forwarded-Proto header is believed (or DbDataSync:App:AllowInsecureHttp to true to serve plain HTTP anyway).");
        });
    }

    /// <summary>A warning to log at startup when a configured address is plain HTTP off loopback and nothing says how it is protected; otherwise null.</summary>
    public static string? StartupWarning(ApiOptions options, IEnumerable<string> urls)
    {
        if (options.AllowInsecureHttp || options.TrustedProxies.Count > 0)
            return null;

        var violations = PlainHttpGuard.FindViolations(urls);
        return violations.Count == 0
            ? null
            : $"Plain HTTP is bound on a non-loopback address ({string.Join(", ", violations)}). Requests from non-local clients that do not " +
              "arrive over HTTPS will be refused: bind HTTPS, or put a TLS-terminating proxy in front and set DbDataSync:App:TrustedProxies " +
              "to its address, or set DbDataSync:App:AllowInsecureHttp to true.";
    }

    internal static ForwardedHeadersOptions ForwardedHeaders(IEnumerable<string> trustedProxies)
    {
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor
                | Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto,
        };

        foreach (var entry in trustedProxies)
        {
            if (IPAddress.TryParse(entry, out var address))
                forwarded.KnownProxies.Add(address);
            else if (System.Net.IPNetwork.TryParse(entry, out var network))
                forwarded.KnownIPNetworks.Add(network);
            else
                throw new InvalidOperationException(
                    $"DbDataSync:App:TrustedProxies entry '{entry}' is neither an IP address nor a CIDR range such as 10.0.0.0/8.");
        }

        return forwarded;
    }
}
