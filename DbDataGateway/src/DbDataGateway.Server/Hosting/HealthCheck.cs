using System.Net;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Startup;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// <c>dbdatagateway health check [--url url]</c>: exit code 0 when a running gateway answers <c>/health/live</c>, for Docker
/// HEALTHCHECK and watchdog scripts. The gateway listens for HTTP/2 only and (by default) with a self-signed
/// certificate, so the probe speaks HTTP/2 and accepts the certificate for a loopback address only.
/// </summary>
public static class HealthCheck
{
    public static HealthProbeOptions Options(GatewayOptions options)
    {
        string scheme = options.Tls.Mode == TlsMode.None ? "http" : "https";
        // Listening on all addresses (0.0.0.0, ::) is reached through loopback.
        var flight = GatewayListeners.Parsed(options.Listen).FirstOrDefault();
        var address = flight is not null && !flight.IsEveryAddress ? flight.Address : IPAddress.Loopback;
        string host = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
        return new HealthProbeOptions
        {
            DefaultUrl = $"{scheme}://{host}:{flight?.Port ?? SuiteServices.Gateway.Ports.Start}/health/live",
            HttpVersion = HttpVersion.Version20,
            TrustLoopbackCertificate = true,
        };
    }
}
