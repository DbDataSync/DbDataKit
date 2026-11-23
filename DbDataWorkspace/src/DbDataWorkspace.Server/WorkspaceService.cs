using DbDataKit.Cli;
using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.Startup;
using DbDataKit.Hosting.Systemd;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

namespace DbDataWorkspace.Server;

/// <summary>What the service verbs act on. Tests substitute fakes; <see cref="Real"/> is the machine.</summary>
public sealed class WorkspaceServiceEnvironment
{
    public required IServiceControlManager Scm { get; init; }
    public required IAclService Acl { get; init; }
    public required IElevationChecker Elevation { get; init; }

    /// <summary>The executable the service will run: the one the command was started from.</summary>
    public required string BinaryPath { get; init; }

    /// <summary>The server's directory when <c>--data-dir</c> is not given.</summary>
    public required string DefaultDataDirectory { get; init; }

    /// <summary>Maps the service account to the SIDs the directory ACLs are written for.</summary>
    public Func<ServiceAccount, IPrincipalResolver> PrincipalResolver { get; init; } = account => new WindowsPrincipalResolver(account);

    public static WorkspaceServiceEnvironment Real() => new()
    {
        Scm = OperatingSystem.IsWindows() ? new WindowsServiceControlManager() : new InMemoryServiceControlManager(),
        Acl = new FileSystemAclService(),
        Elevation = new ElevationDetector(),
        BinaryPath = Environment.ProcessPath ?? "",
        DefaultDataDirectory = DbDataKit.State.SuiteEnvironment.ResolveServiceFolder(WorkspaceSettings.ServiceName),
    };
}

/// <summary>
/// <c>dbdataworkspace service install|uninstall|status|start|stop|restart|check|unit</c>: registration of <c>server run</c> with Windows or systemd, the shared verbs over the shared command. The service runs as the virtual account
/// <c>NT SERVICE\DbDataWorkspace</c> unless <c>--account</c> says otherwise, and keeps everything it writes in one data directory, which only administrators and the service can reach.
/// </summary>
public static class WorkspaceService
{
    /// <summary>The Windows service, its display name and description, as the suite's table spells them (<see cref="SuiteServices.Workspace"/>).</summary>
    public static readonly string RegistrationName = SuiteServices.Workspace.WindowsName;
    public static readonly string DisplayName = SuiteServices.Workspace.DisplayName;
    public static readonly string Description = SuiteServices.Workspace.Description;

    public static Noun Noun(WorkspaceServiceEnvironment env) =>
        HostingNouns.Service(new ServiceCommand(Context(env), env.Scm, env.Acl, env.Elevation, null, true));

    /// <summary>The Workspace's registration, as the suite's registrations are made (<see cref="SuiteServiceRegistration"/>).</summary>
    public static ServiceCommandContext Context(WorkspaceServiceEnvironment env) =>
        SuiteServiceRegistration.Context(SuiteServices.Workspace, env.BinaryPath, env.DefaultDataDirectory, env.PrincipalResolver, WorkspaceServer.Version);

    public static SystemdUnitOptions UnitOptions(string name, string executable) => SuiteServiceRegistration.UnitOptions(SuiteServices.Workspace, executable, name);
}
