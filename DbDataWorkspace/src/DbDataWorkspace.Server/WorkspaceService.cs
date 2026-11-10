using DbDataKit.Cli;
using DbDataKit.Hosting.Cli;
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
/// <c>dbdataworkspace service install|uninstall|status|start|stop|restart|doctor|unit</c>: registration of <c>server run</c> with Windows or systemd, the shared verbs over the shared command. The service runs as the virtual account
/// <c>NT SERVICE\DbDataWorkspace</c> unless <c>--account</c> says otherwise, and keeps everything it writes in one data directory, which only administrators and the service can reach.
/// </summary>
public static class WorkspaceService
{
    public const string RegistrationName = "DbDataWorkspace";
    public const string DisplayName = "DbDataWorkspace server";
    public const string Description = "Hosts the projects people and agents author in git workspaces, with a record of what was done, over a management API a console pairs with.";

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
            new LayoutEntry(LayoutRole.Data, env.DefaultDataDirectory, new AclSpec(true, [admin, system, Svc(AccessRights.Modify)]),
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
        };
    }

    public static SystemdUnitOptions UnitOptions(string name, string executable) => new()
    {
        Name = name,
        Description = "DbDataWorkspace server",
        ExecStart = $"{executable} server run --repo /var/lib/dbdatakit/{WorkspaceSettings.ServiceName}",
        WorkingDirectory = executable[..executable.LastIndexOf('/')],
        // DynamicUser: no account to manage; the state directory /var/lib/dbdatakit/workspace is created and owned by systemd.
        StateDirectories = [$"dbdatakit/{WorkspaceSettings.ServiceName}"],
    };
}
