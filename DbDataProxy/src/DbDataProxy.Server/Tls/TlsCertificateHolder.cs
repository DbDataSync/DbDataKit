using System.Security.Cryptography.X509Certificates;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Tls;

/// <summary>The certificate Kestrel presents; replaced in place when a self-signed certificate is rotated.</summary>
public sealed class TlsCertificateHolder
{
    private X509Certificate2? _current;

    public X509Certificate2? Current => Volatile.Read(ref _current);

    /// <summary>The previous certificate is not disposed: handshakes in flight may still be using it.</summary>
    public void Set(X509Certificate2? certificate) => Volatile.Write(ref _current, certificate);
}

/// <summary>Replaces the self-signed certificate before it expires, and picks up a renewed store certificate, without a restart.</summary>
public sealed class CertificateRotationService(
    TlsCertificateHolder holder,
    ProxyOptions options,
    string dataDirectory,
    ILogger<CertificateRotationService> log,
    TimeProvider? time = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.Tls.Mode is not (TlsMode.SelfSigned or TlsMode.Store)) return;
        using var timer = new PeriodicTimer(Interval, time ?? TimeProvider.System);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) Check();
        }
        catch (OperationCanceledException) { /* shutting down */ }
    }

    /// <summary>Rotates now if the certificate in use is inside its renewal window. Returns true when it was replaced.</summary>
    public bool Check()
    {
        try
        {
            if (holder.Current is not { } current) return false;
            // A store certificate is replaced by whoever renews it; pick the new one up (same subject, later expiry).
            // A self-signed one is rotated by us once it is inside its renewal window.
            if (options.Tls.Mode == TlsMode.SelfSigned && !CertificateProvider.NeedsRotation(current, time)) return false;
            var next = CertificateProvider.Load(options, dataDirectory, log, time);
            if (next is null || next.Thumbprint == current.Thumbprint) return false;
            holder.Set(next);
            log.LogInformation("Rotated the TLS certificate: {Old} -> {New}, valid until {NotAfter:u}. Clients must trust the new certificate.",
                current.Thumbprint, next.Thumbprint, next.NotAfter.ToUniversalTime());
            return true;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "TLS certificate rotation failed; the current certificate stays in use.");
            return false;
        }
    }
}
