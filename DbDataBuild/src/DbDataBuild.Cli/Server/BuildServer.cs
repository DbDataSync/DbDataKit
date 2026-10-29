using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using DbDataBuild.Cli.Mcp;
using DbDataKit.Hosting;
using DbDataKit.Hosting.Certificates;
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


    /// <summary>The variable that names the server's directory for every command (a container sets it once, so <c>docker exec</c> reaches the same files the server uses).</summary>
    public const string DirectoryVariable = "DBDATABUILD_SERVER_DIR";

    /// <summary>What <c>--repo</c> defaults to: <see cref="DirectoryVariable"/>, else a folder under the user's local application data.</summary>
    public static string DefaultDirectory(Func<string, string?>? environment = null) =>
        (environment ?? Environment.GetEnvironmentVariable)(DirectoryVariable) is { Length: > 0 } named
            ? named
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dbdatabuild", "server");
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

    public static WebApplication Build(BuildServerSettings settings, X509Certificate2 certificate, IProjectCommandRunner runner, ProjectsFile projects, IReadOnlyList<DbDataBuild.Tui.Model.CommandInfo> commands, Action<string> log, Func<string, IReadOnlyList<DbDataBuild.Tui.Model.CommandInfo>, ToolSurface>? surfaceFor = null, BuildScheduler? scheduler = null, WarmSessionRunner? workspaceRunner = null, ProxyPublisher? proxyPublisher = null)
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
        app.AnnounceToConsole("dbdatabuild", instanceId, clients, files.Codes());
        app.MapKeyRotation(new KeyRotationService(instanceId, clients, new MemoryReplayCache()));
        app.UseManagementAuthentication(new ConsoleTokenValidator(instanceId, clients, new MemoryReplayCache()), refused => log($"A call was refused ({refused.Failure}): {refused.Detail}"));
        BuildManagementApi.Map(app, projects, commands, runner, surfaceFor, scheduler, new Workspaces(settings.Directory, TimeProvider.System), workspaceRunner, new ProxiesFile(settings.Directory), proxyPublisher, files.Codes(), $"Build server {instanceId}");
        if (workspaceRunner is not null) app.Lifetime.ApplicationStopping.Register(workspaceRunner.Dispose);
        return app;
    }
}
