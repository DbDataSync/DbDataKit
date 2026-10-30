using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataStore.Server;

/// <summary>
/// The DbDataStore server (plan 19, Z01): Kestrel over HTTPS with the shared trust code of <c>DbDataKit.Web</c> (pairing, key rotation, the version header, the token check) and a management API a console is the client of. It has no web page
/// of its own. The certificate is self-signed and kept in the server's directory, and a client pins its fingerprint at pairing, so nothing else about it needs to be trusted. Models, storage and writes arrive in the slices after this one.
/// </summary>
public static class StoreServer
{
    public static string Version { get; } =
        (typeof(StoreServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>The server's instance id, made and written to <c>server.yaml</c> the first time and read from it after.</summary>
    public static string InstanceId(string serverYaml) => ServerIdentity.InstanceId(serverYaml, "store", "dbdatastore server");

    public static X509Certificate2 Certificate(string directory) => DirectoryCertificate.Load(directory);

    public static WebApplication Build(StoreSettings settings, X509Certificate2 certificate, Action<string> log)
    {
        if (settings.Listen.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("A DbDataStore server listens on HTTPS only.", nameof(settings));
        var instanceId = InstanceId(settings.ServerYaml);
        var files = new PairingFiles(settings.ServerYaml);
        var clients = files.Clients();
        var fingerprint = PairingProof.CertificateFingerprint(certificate);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // service and systemd modes, draining on stop, /health and /version, request limits: the host every DbData server shares
        builder.UseDbDataHost(host =>
        {
            host.Product = StoreSettings.Product;
            host.ServiceName = StoreService.RegistrationName;
            host.Version = Version;
        });
        var address = settings.Listen.HostNameType == UriHostNameType.Dns && settings.Listen.Host != "localhost"
            ? IPAddress.Any
            : settings.Listen.Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(settings.Listen.Host);
        builder.WebHost.ConfigureKestrel(o => o.Listen(address, settings.Listen.Port, listen => listen.UseHttps(certificate)));

        var app = builder.Build();
        app.UseManagementApiVersion();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapPairing(new PairingService(instanceId, fingerprint, clients, files.Codes()));
        app.AnnounceToConsole("dbdatastore", instanceId, settings.Listen.Port, clients, files.Codes());
        app.MapKeyRotation(new KeyRotationService(instanceId, clients, new MemoryReplayCache()));
        app.UseManagementAuthentication(new ConsoleTokenValidator(instanceId, clients, new MemoryReplayCache()), refused => log($"A call was refused ({refused.Failure}): {refused.Detail}"));
        Map(app, instanceId);
        return app;
    }

    /// <summary>The management API so far: who the server is. Every call needs a token a paired console issued, and any role may ask.</summary>
    private static void Map(IEndpointRouteBuilder app, string instanceId)
    {
        app.MapGet("/api/v1/info", () => Results.Json(new JsonObject
        {
            ["product"] = StoreSettings.Product,
            ["version"] = Version,
            ["instance_id"] = instanceId,
        }));
    }
}
