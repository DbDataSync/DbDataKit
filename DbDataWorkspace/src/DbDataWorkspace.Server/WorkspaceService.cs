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

    public static ServiceCommandContext Context(WorkspaceServiceEnvironment env)
    {
        var installDirectory = Path.GetDirectoryName(env.BinaryPath) ?? env.BinaryPath;
        var admin = new AclRule(LayoutPrincipal.Administrators, AccessRights.Full);
        var system = new AclRule(LayoutPrincipal.System, AccessRights.Full);
        AclRule Svc(AccessRights r) => new(LayoutPrincipal.ServiceAccount, r);

        var layout = DirectoryLayout.Create(RegistrationName,
        [
            new LayoutEntry(LayoutRole.InstallRoot, installDirectory, new AclSpec(false, [admin, system, Svc(AccessRights.ReadExecute)]),
                "Binaries; the service can read and run them but not change them."),
            // the install makes only this folder, owned by the Administrators group; the service creates everything inside it at its first start
            // Design: docs/install.md#a-service-owns-its-data
            new LayoutEntry(LayoutRole.Data, env.DefaultDataDirectory, new AclSpec(true, [admin, system, Svc(AccessRights.Modify)], LayoutPrincipal.Administrators),
                "The server's identity, paired clients, certificate, projects and workspaces; Users have no access."),
        ]);

        return new ServiceCommandContext
        {
            ServiceName = RegistrationName,
            DisplayName = DisplayName,
            Description = Description,
            BinaryPath = env.BinaryPath,
            Version = WorkspaceServer.Version,
            // the service runs the server, with its directory named so it does not depend on the account's profile
            Arguments = ["server", "run", "--repo", env.DefaultDataDirectory],
            Layout = layout,
            PrincipalResolver = env.PrincipalResolver,
            SystemdUnit = () => UnitOptions("dbdataworkspace", env.BinaryPath.StartsWith('/') ? env.BinaryPath : "/opt/dbdataworkspace/dbdataworkspace"),
            StartMode = ServiceStartMode.Automatic,
            Failure = new FailurePolicy([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)], TimeSpan.FromDays(1)),
            DataDirectoryArgument = path => ["--repo", path],
            StartupService = SuiteServices.Workspace,
        };
    }

    public static SystemdUnitOptions UnitOptions(string name, string executable) => new()
    {
        Name = name,
        Description = SuiteServices.Workspace.Description,
        ExecStart = $"{executable} server run --repo /var/lib/dbdatakit/{WorkspaceSettings.ServiceName}",
        WorkingDirectory = executable[..executable.LastIndexOf('/')],
        // the system user install.sh --system makes, in the group dbdatakit; systemd makes /var/lib/dbdatakit/workspace and /var/log/dbdatakit/dbdataworkspace for it
        // at each start, owned by that user and closed to every other one, so the service creates everything inside them as itself
        // Design: docs/install.md#a-service-owns-its-data
        DynamicUser = false,
        User = SuiteServices.Workspace.LinuxUser,
        Group = SuiteServices.LinuxGroup,
        StateDirectories = [$"dbdatakit/{WorkspaceSettings.ServiceName}"],
        StateDirectoryMode = "0700",
        LogsDirectories = [$"dbdatakit/{SuiteServices.Workspace.LogFolder}"],
        LogsDirectoryMode = "0700",
    };
}
