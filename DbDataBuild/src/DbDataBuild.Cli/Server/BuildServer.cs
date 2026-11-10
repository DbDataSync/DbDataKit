using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using DbDataBuild.Cli.Mcp;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataBuild.Cli.Server;

/// <summary>Where a Build server keeps what is its own.</summary>
/// <param name="Directory">The server's directory: <c>server.yaml</c> (its instance id and the clients paired with it), the pairing codes, its certificate, the secrets, <c>build/projects.yaml</c> (the projects it hosts) and the working copies of remote ones.</param>
/// <param name="Listen">The address it listens on; HTTPS, and a name or address of this machine.</param>
internal sealed record BuildServerSettings(string Directory, Uri Listen)
{
    public const string DefaultListen = "https://127.0.0.1:8470";

    public string ServerYaml => Path.Combine(Directory, "server.yaml");


    /// <summary>The service's name in the suite's folders and variables (<see cref="DbDataKit.State.SuiteEnvironment"/>): <c>build</c>.</summary>
    public const string ServiceName = "build";

    /// <summary>The variable that names the server's directory for every command (a container sets it once, so <c>docker exec</c> reaches the same files the server uses): <c>DBDATAKIT_DATA_BUILD</c>, or the folder <c>build</c> under <c>DBDATAKIT_DATA</c>.</summary>
    public static string DirectoryVariable => DbDataKit.State.SuiteEnvironment.FolderVariable(ServiceName);

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/>, else a folder under the user's local application data.</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        DbDataKit.State.SuiteEnvironment.ResolveDataFolder(ServiceName, environment, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatabuild", "server"));
}

/// <summary>
/// The Build server (plan 13, R01): Kestrel over HTTPS with the shared trust code of <c>DbDataKit.Web</c> (pairing, key rotation, the version header, the token check) and the management API of <see cref="BuildManagementApi"/>.
/// It has no web page of its own; a console is its client. The certificate is self-signed and kept in the server's directory, with its password in a file only the server's account can read (moving it to the shared secret store is R04's);
/// a client pins its fingerprint at pairing, so nothing else about it needs to be trusted.
/// </summary>
internal static class BuildServer
{
    /// <summary>The server's instance id, made and written to <c>server.yaml</c> the first time and read from it after.</summary>
    public static string InstanceId(string serverYaml) => ServerIdentity.InstanceId(serverYaml, "build", "dbdatabuild server");

    public static X509Certificate2 Certificate(string directory) => DirectoryCertificate.Load(directory);

    public static WebApplication Build(BuildServerSettings settings, X509Certificate2 certificate, IProjectCommandRunner runner, ProjectsFile projects, IReadOnlyList<DbDataBuild.Tui.Model.CommandInfo> commands, Action<string> log, Func<string, IReadOnlyList<DbDataBuild.Tui.Model.CommandInfo>, ToolSurface>? surfaceFor = null, BuildScheduler? scheduler = null, WarmSessionRunner? workspaceRunner = null, ProxyPublisher? proxyPublisher = null, IMaintenanceGate? maintenance = null, DbDataKit.State.Cli.StateTarget? state = null, Func<ScheduleRunnerLink, DbDataBuild.Cli.Runner.IRunnerTaskControl>? runnerControl = null, Func<ScheduleRunnerLink, DbDataBuild.Cli.Runner.IRunnerSchedule>? runnerSchedule = null, string? runnerClientDirectory = null)
    {
        if (settings.Listen.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("A Build server listens on HTTPS only.", nameof(settings));
        var instanceId = InstanceId(settings.ServerYaml);
        var files = new PairingFiles(settings.ServerYaml);
        var clients = files.Clients();
        var fingerprint = PairingProof.CertificateFingerprint(certificate);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        // service and systemd modes, draining on stop, /health and /version, request limits: the host every DbData server shares
        builder.UseDbDataHost(host =>
        {
            host.Product = "dbdatabuild";
            host.ServiceName = BuildService.RegistrationName;
            host.Version = DbDataBuild.Core.ProductInfo.Version;
            host.Limits.RequestTimeout = TimeSpan.FromHours(1);    // a command may run for half an hour (ChildProcessCommandRunner)
        });
        var address = settings.Listen.HostNameType == UriHostNameType.Dns && settings.Listen.Host != "localhost"
            ? IPAddress.Any
            : settings.Listen.Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(settings.Listen.Host);
        builder.WebHost.ConfigureKestrel(o => o.Listen(address, settings.Listen.Port, listen => listen.UseHttps(certificate)));

        if (scheduler is not null) builder.Services.AddHostedService(_ => new BuildSchedulerService(scheduler, log));
        var app = builder.Build();
        app.UseManagementApiVersion();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapPairing(new PairingService(instanceId, fingerprint, clients, files.Codes()));
        app.AnnounceToConsole("dbdatabuild", instanceId, settings.Listen.Port, clients, files.Codes());
        app.MapKeyRotation(new KeyRotationService(instanceId, clients, new MemoryReplayCache()));
        app.UseManagementAuthentication(new ConsoleTokenValidator(instanceId, clients, new MemoryReplayCache()), refused => log($"A call was refused ({refused.Failure}): {refused.Detail}"));
        // maintenance mode and the state endpoints (plan 29b, C05); the scheduler stops claiming through the gate the caller shares with it
        maintenance ??= new MaintenanceGate(work: () => scheduler?.Running ?? 0);
        app.UseMaintenance(maintenance, context => context.ManagementTokenOrNull() is { } token && ManagementAuthorization.Authorize(token, ServerRole.Admin).Allowed);
        app.MapState(new StateManagement(new StateManagementOptions(
            "dbdatabuild", "dbdatabuild", "the server",
            () => state ?? DbDataKit.State.Cli.StateTarget.From(BuildState.Resolve(settings.Directory), BuildState.SqlitePath(settings.Directory), () => BuildState.Secrets(settings.Directory), BuildState.SettingsFile(settings.Directory)),
            BuildSchedulerSchema.Modules, BuildSchedulerSchema.Scope, maintenance, new FileAuditLog(Path.Combine(settings.Directory, "management-audit.log")), instanceId, DbDataBuild.Core.ProductInfo.Version, Move: true)));
        // the server upgrades itself when the console says so (plan 24a UC09)
        DbDataKit.Upgrade.UpgradeSetup.MapUpgrade(app, "dbdatabuild", DbDataBuild.Core.ProductInfo.Version, BuildService.RegistrationName, settings.Listen.Port, new FileAuditLog(Path.Combine(settings.Directory, "management-audit.log")), () => [.. clients.All().Select(c => new DbDataKit.Upgrade.PeerGeneration(c.Name, c.Generation))]);
        BuildManagementApi.Map(app, projects, commands, runner, surfaceFor, scheduler, new DbDataKit.Authoring.Workspaces(settings.Directory, TimeProvider.System), workspaceRunner, new ProxiesFile(settings.Directory), proxyPublisher, files.Codes(), $"Build server {instanceId}", new ProjectCreator(settings.Directory, projects), settings.Directory, runnerControl, new FileAuditLog(Path.Combine(settings.Directory, "management-audit.log")), runnerSchedule, runnerClientDirectory);
        if (workspaceRunner is not null) app.Lifetime.ApplicationStopping.Register(workspaceRunner.Dispose);
        return app;
    }
}
