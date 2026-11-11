using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataKit.Web.Trust;

/// <summary>
/// <c>GET /api/v1/certificate</c> of a server that presents one certificate for its life (Build, Store, Runner, Workspace; UI10): what the certificate is and how long it has, in the shape of the Gateway's route of the same name,
/// so the console's Monitoring reads every server alike. The certificate is described, never its key. Any role of a paired console may ask (the token is checked by <see cref="ManagementAuthentication"/> first); nothing here renews it:
/// renewing replaces the certificate every console has pinned, which is the server's own command (<c>cert renew</c>).
/// </summary>
public static class CertificateStatusEndpoint
{
    public const string Path = "/api/v1/certificate";

    /// <summary>Days before the end at which a certificate is said to need renewing; the same default the servers renew by.</summary>
    public const int DefaultRenewBeforeDays = 30;

    /// <param name="certificate">The certificate the server presents; null says there is none.</param>
    /// <param name="mode">How the certificate was made, as the server says it (<c>self-signed</c> for the one kept in the server's directory).</param>
    /// <param name="now">The time the days left are counted from.</param>
    /// <param name="renewBeforeDays">The days before the end at which it needs renewing.</param>
    public static JsonObject Describe(X509Certificate2? certificate, string mode, DateTimeOffset now, int renewBeforeDays = DefaultRenewBeforeDays)
    {
        var result = new JsonObject { ["mode"] = mode, ["renew_before_days"] = renewBeforeDays, ["present"] = certificate is not null };
        if (certificate is null) return result;
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        var daysLeft = (int)Math.Floor((notAfter - now).TotalDays);
        result["subject"] = certificate.Subject;
        result["issuer"] = certificate.Issuer;
        result["self_signed"] = certificate.Subject == certificate.Issuer;
        result["thumbprint"] = certificate.Thumbprint;
        result["not_before"] = new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        result["not_after"] = notAfter;
        result["days_left"] = daysLeft;
        result["needs_renewal"] = daysLeft <= renewBeforeDays;
        result["names"] = new JsonArray([.. certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().SelectMany(e => e.EnumerateDnsNames()).Select(n => (JsonNode)n)]);
        return result;
    }

    /// <summary>Maps the route; <paramref name="current"/> is asked on every call, so a certificate that is replaced is the one described.</summary>
    public static IEndpointRouteBuilder MapCertificateStatus(this IEndpointRouteBuilder endpoints, Func<X509Certificate2?> current, string mode = "self-signed", TimeProvider? clock = null)
    {
        endpoints.MapGet(Path, (HttpContext context) =>
        {
            var token = context.ManagementTokenOrNull();
            if (token is null) return Results.Json(new { error = "A paired console's token is needed." }, statusCode: StatusCodes.Status401Unauthorized);
            if (!ManagementAuthorization.Authorize(token, ServerRole.Viewer).Allowed) return Results.Json(new { error = "This needs the viewer role." }, statusCode: StatusCodes.Status403Forbidden);
            return Results.Json(Describe(current(), mode, (clock ?? TimeProvider.System).GetUtcNow()));
        });
        return endpoints;
    }
}
