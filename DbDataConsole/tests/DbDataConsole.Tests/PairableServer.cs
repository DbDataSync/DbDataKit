using System.Net;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataConsole.Tests;

/// <summary>
/// A server a console can pair with, as small as one can be and still be the real thing at the edges: Kestrel over HTTPS with a certificate of its own, the shared pairing and key-rotation endpoints, the
/// management API version header, and the shared host's <c>/health</c> and <c>/version</c>. What the console does with it is what it would do with a Build, Sync or Proxy server.
/// </summary>
internal sealed class PairableServer : IAsyncDisposable
{
    public const string InstanceId = "fake-instance-1";
    private readonly WebApplication app;

    public Uri Url { get; }
    public string Directory { get; }
    public string Fingerprint { get; }
    public PairedClientsFile Clients { get; }
    public PairingCodes Codes { get; }

    private PairableServer(WebApplication app, Uri url, string directory, string fingerprint)
    {
        (this.app, Url, Directory, Fingerprint) = (app, url, directory, fingerprint);
        Clients = new PairedClientsFile(Path.Combine(directory, "server.yaml"));
        Codes = new PairingCodes(Path.Combine(directory, "pairing-codes.json"));
    }

    public static async Task<PairableServer> StartAsync(int port = 0, string? directory = null)
    {
        directory ??= System.IO.Directory.CreateTempSubdirectory("ddk-pairable").FullName;
        var certificate = DirectoryCertificate.Load(directory);
        var fingerprint = PairingProof.CertificateFingerprint(certificate);
        var clients = new PairedClientsFile(Path.Combine(directory, "server.yaml"));
        var codes = new PairingCodes(Path.Combine(directory, "pairing-codes.json"));

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.UseDbDataHost(host =>
        {
            host.Product = "fake-server";
            host.Version = "9.9.9";
        });
        builder.WebHost.ConfigureKestrel(o => o.Listen(IPAddress.Loopback, port, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.UseManagementApiVersion();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapPairing(new PairingService(InstanceId, fingerprint, clients, codes));
        app.MapKeyRotation(new KeyRotationService(InstanceId, clients, new MemoryReplayCache()));
        await app.StartAsync();
        return new PairableServer(app, new Uri(app.Urls.First().Replace("[::1]", "127.0.0.1")), directory, fingerprint);
    }

    /// <summary>A single-use code, as <c>server pair</c> prints it.</summary>
    public string NewCode() => Codes.Create(ServerRoles.Close(ServerRole.Viewer)).Code;

    public async Task StopAsync()
    {
        await app.StopAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        try { System.IO.Directory.Delete(Directory, recursive: true); } catch (IOException) { }
    }
}
