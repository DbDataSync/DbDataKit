using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Server.Tls;

namespace DbDataProxy.Server.Management;

/// <summary>
/// The server certificate and the recent failures through the management API (plan 13, P07). The certificate is described (never its key), and a self-signed one can be renewed now: the new one is made the way
/// <c>dbdataproxy cert renew</c> makes it and put in use at once. <b>Renewing replaces the certificate every paired console has pinned</b>, so each of them must be paired again; the call therefore asks for <c>{"confirm": true}</c>.
/// </summary>
public sealed class ProxyCertificate(TlsCertificateHolder holder, Func<ProxyOptions> options, string dataDirectory, RecentFailures failures, IAuditLog audit, TimeProvider? clock = null)
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public ManagementResult Handle(string method, string[] rest, string? body, ManagementCaller caller)
    {
        switch (rest)
        {
            case ["certificate"] when method == "GET": return ManagementResult.Ok(Describe());
            case ["certificate", "renew"] when method == "POST": return Renew(body, caller);
            case ["failures"] when method == "GET": return ManagementResult.Ok(Failures());
            default: return ManagementResult.Problem(404, "There is no such call.");
        }
    }

    private JsonObject Describe()
    {
        var o = options();
        var certificate = holder.Current;
        var now = _clock.GetUtcNow();
        var result = new JsonObject { ["mode"] = o.Tls.Mode.ToString().ToLowerInvariant(), ["renew_before_days"] = CertificateProvider.RenewBeforeDays, ["present"] = certificate is not null };
        if (certificate is null) return result;
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime(), TimeSpan.Zero);
        result["subject"] = certificate.Subject;
        result["issuer"] = certificate.Issuer;
        result["self_signed"] = certificate.Subject == certificate.Issuer;
        result["thumbprint"] = certificate.Thumbprint;
        result["not_before"] = new DateTimeOffset(certificate.NotBefore.ToUniversalTime(), TimeSpan.Zero);
        result["not_after"] = notAfter;
        result["days_left"] = (int)Math.Floor((notAfter - now).TotalDays);
        result["needs_renewal"] = CertificateProvider.NeedsRotation(certificate, _clock);
        result["names"] = new JsonArray([.. DnsNames(certificate).Select(n => (JsonNode)n)]);
        return result;
    }

    private static IEnumerable<string> DnsNames(X509Certificate2 certificate)
    {
        foreach (var extension in certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>())
            foreach (var name in extension.EnumerateDnsNames()) yield return name;
    }

    private sealed record RenewRequest(bool Confirm);

    private ManagementResult Renew(string? body, ManagementCaller caller)
    {
        var o = options();
        if (o.Tls.Mode != TlsMode.SelfSigned) return ManagementResult.Problem(409, $"Only a self-signed certificate is renewed here; this proxy's certificate is {o.Tls.Mode.ToString().ToLowerInvariant()}, which whoever issued it renews.");
        bool confirmed;
        try { confirmed = !string.IsNullOrWhiteSpace(body) && System.Text.Json.JsonSerializer.Deserialize<RenewRequest>(body, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))?.Confirm == true; }
        catch (System.Text.Json.JsonException) { confirmed = false; }
        if (!confirmed)
            return ManagementResult.Problem(400, "Renewing replaces the certificate every paired console has pinned, so each of them must be paired again. Send {\"confirm\": true} to go on.");

        var before = holder.Current;
        var console = new CapturedConsole();
        var exit = CertCli.Run(["renew"], o, dataDirectory, console, _clock);
        if (exit != 0)
        {
            Record(caller, "certificate renew", "failed");
            return ManagementResult.Problem(500, "The certificate could not be renewed; see the server's log.");
        }
        var next = CertificateProvider.Load(o, dataDirectory, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, _clock);
        if (next is not null) holder.Set(next);
        Record(caller, "certificate renew", next is not null && before is not null && next.Thumbprint != before.Thumbprint ? "renewed" : "unchanged");
        var described = Describe();
        described["renewed"] = next is not null && (before is null || next.Thumbprint != before.Thumbprint);
        described["warning"] = "Every paired console pinned the old certificate and has to be paired again.";
        return ManagementResult.Ok(described);
    }

    private JsonObject Failures() => new()
    {
        ["failures"] = new JsonArray([.. failures.Newest().Select(f => (JsonNode)new JsonObject
        {
            ["at"] = f.At, ["data_source"] = f.DataSource, ["kind"] = f.Kind, ["code"] = f.Code, ["engine"] = f.Engine, ["message"] = f.Message,
        })]),
    };

    private void Record(ManagementCaller caller, string command, string outcome) => audit.Append(caller.Audit(command, "tls", outcome, _clock.GetUtcNow()));

    private sealed class CapturedConsole : DbDataKit.Hosting.Cli.IConsole
    {
        public TextWriter Out { get; } = new StringWriter();
        public TextWriter Error { get; } = new StringWriter();
        public string? ReadLine() => null;
    }
}
