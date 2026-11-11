using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.State.Api;
using DbDataKit.Upgrade;
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

    /// <summary>The server's instance id, made the first time and kept in its state database (<see cref="StoreState"/>) after.</summary>
    public static string InstanceId(string directory, DbDataKit.State.StateSettings? state = null) => InstanceId(new GroupSettings(StoreState.Open(directory, state)));

    private static string InstanceId(IGroupSettings settings) => GroupIdentity.InstanceId(settings, "store");

    public static X509Certificate2 Certificate(string directory) => DirectoryCertificate.Load(directory);

    public static WebApplication Build(StoreSettings settings, X509Certificate2 certificate, Action<string> log)
    {
        if (settings.Listen.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("A DbDataStore server listens on HTTPS only.", nameof(settings));
        var state = StoreState.Open(settings.Directory, settings.State);
        var instanceId = InstanceId(new GroupSettings(state));
        var files = new PairingFiles(settings.ServerYaml, Group: state);
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
        // maintenance mode (plan 29b, C05): after the token check, so a request from an administrator is known, and before the product's own calls
        var maintenance = new MaintenanceGate();
        app.UseMaintenance(maintenance, context => context.ManagementTokenOrNull() is { } token && ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed);
        app.MapState(StateApi(settings, instanceId, maintenance));
        // the server upgrades itself when the console says so (plan 24a UC09)
        app.MapUpgrade(StoreSettings.Product, Version, StoreService.RegistrationName, settings.Listen.Port, new FileAuditLog(Path.Combine(settings.Directory, "management-audit.log")), () => [.. clients.All().Select(c => new DbDataKit.Upgrade.PeerGeneration(c.Name, c.Generation))]);
        app.UseScopedTokens();
        app.UsePublishedModels(settings.Directory);
        Map(app, instanceId);
        StoreModels.Map(app, settings.Directory);
        StoreModelHistory.Map(app, settings.Directory);
        StoreEvents.Map(app, settings.Directory);
        StoreData.Map(app, settings.Directory, new StoreConnections(settings.Directory));
        return app;
    }

    /// <summary>The state endpoints (<c>GET /api/v1/state</c>, the maintenance mode) over this server's state: the settings it was started with, the audit in a file beside <c>server.yaml</c> (the state has no audit module).</summary>
    private static StateManagement StateApi(StoreSettings settings, string instanceId, IMaintenanceGate gate) => new(new StateManagementOptions(
        StoreSettings.Product, StoreSettings.Product, "the server",
        () =>
        {
            var resolved = StoreState.Resolve(settings.Directory);
            return new DbDataKit.State.Cli.StateTarget(settings.State ?? resolved.Settings, Path.Combine(settings.Directory, StoreState.FileName), () => StoreState.Secrets(settings.Directory),
                StoreState.SettingsFile(settings.Directory), settings.StateSources ?? resolved.Sources);
        },
        StoreState.Modules, StoreState.Scope, gate, new FileAuditLog(Path.Combine(settings.Directory, "management-audit.log")), instanceId, Version, Move: true));

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
