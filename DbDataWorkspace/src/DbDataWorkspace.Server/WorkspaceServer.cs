using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Authoring.Server;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.State;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataWorkspace.Server;

/// <summary>
/// The DbDataWorkspace server (plan 22a GP31, decision D8): Kestrel over HTTPS with the shared trust code of <c>DbDataKit.Web</c> (pairing, key rotation, the version header, the token check) and a management API a console is the client of.
/// It has no web page of its own. The certificate is self-signed and kept in the server's directory, and a client pins its fingerprint at pairing, so nothing else about it needs to be trusted. It hosts projects and the git workspaces
/// people author them in (<see cref="WorkspaceManagement"/>); builds, previews and publishing arrive in the slices after this one.
/// </summary>
public static class WorkspaceServer
{
    public static string Version { get; } =
        (typeof(WorkspaceServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    /// <summary>The server's instance id, made the first time and kept in its state database (<see cref="WorkspaceState"/>) after.</summary>
    public static string InstanceId(string directory, DbDataKit.State.StateSettings? state = null) => InstanceId(new GroupSettings(WorkspaceState.Open(directory, state)));

    private static string InstanceId(IGroupSettings settings) => GroupIdentity.InstanceId(settings, "workspace");

    public static X509Certificate2 Certificate(string directory) => DirectoryCertificate.Load(directory);

    public static WebApplication Build(WorkspaceSettings settings, X509Certificate2 certificate, Action<string> log)
    {
        if (settings.Listen.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("A DbDataWorkspace server listens on HTTPS only.", nameof(settings));
        var state = WorkspaceState.Open(settings.Directory, settings.State);
        var instanceId = InstanceId(new GroupSettings(state));
        var files = new PairingFiles(settings.ServerYaml, Group: state);
        var clients = files.Clients();
        var fingerprint = PairingProof.CertificateFingerprint(certificate);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // service and systemd modes, draining on stop, /health and /version, request limits: the host every DbData server shares
        builder.UseDbDataHost(host =>
        {
            host.Product = WorkspaceSettings.Product;
            host.ServiceName = WorkspaceService.RegistrationName;
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
        app.AnnounceToConsole("dbdataworkspace", instanceId, settings.Listen.Port, clients, files.Codes());
        app.MapKeyRotation(new KeyRotationService(instanceId, clients, new MemoryReplayCache()));
        app.UseManagementAuthentication(new ConsoleTokenValidator(instanceId, clients, new MemoryReplayCache()), refused => log($"A call was refused ({refused.Failure}): {refused.Detail}"));
        // maintenance mode (plan 29b, C05): after the token check, so a request from an administrator is known, and before the product's own calls
        var maintenance = new MaintenanceGate();
        app.UseMaintenance(maintenance, context => context.ManagementTokenOrNull() is { } token && ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed);
        app.MapState(StateApi(settings, instanceId, maintenance));
        var store = new WorkspaceStore(state);
        var workspaces = new DbDataKit.Authoring.Workspaces(settings.Directory, TimeProvider.System);
        var builds = new WorkspaceBuilds(settings.Directory, store, workspaces, () => settings.Apps ?? WorkspaceAppSettings.Load(settings.Directory), log);
        app.Lifetime.ApplicationStopping.Register(builds.Dispose);
        WorkspaceManagement.Map(app, instanceId, WorkspaceSettings.Product, Version, store, new WorkspaceProjects(settings.Directory, store, StandardProjectKinds.Create(new BuildTool(settings.BuildExecutable))), new FileAuditLog(Path.Combine(settings.Directory, "management-audit.log")), log, workspaces, builds, new WorkspacePublishing(settings.Directory, store, () => WorkspaceSecrets.Open(settings.Directory), () => WorkspaceState.Secrets(settings.Directory)));
        return app;
    }

    /// <summary>The state endpoints (<c>GET /api/v1/state</c>, the maintenance mode) over this server's state: the settings it was started with, the audit in a file beside <c>server.yaml</c> (the state has no audit module).</summary>
    private static StateManagement StateApi(WorkspaceSettings settings, string instanceId, IMaintenanceGate gate) => new(new StateManagementOptions(
        WorkspaceSettings.Product, WorkspaceSettings.Product, "the server",
        () =>
        {
            var resolved = WorkspaceState.Resolve(settings.Directory);
            return new DbDataKit.State.Cli.StateTarget(settings.State ?? resolved.Settings, Path.Combine(settings.Directory, WorkspaceState.FileName), () => WorkspaceState.Secrets(settings.Directory),
                WorkspaceState.SettingsFile(settings.Directory), settings.StateSources ?? resolved.Sources);
        },
        WorkspaceState.Modules, WorkspaceState.Scope, gate, new FileAuditLog(Path.Combine(settings.Directory, "management-audit.log")), instanceId, Version, Move: true));
}
