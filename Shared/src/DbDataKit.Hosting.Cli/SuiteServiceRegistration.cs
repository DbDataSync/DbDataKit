using DbDataKit.Hosting.Startup;
using DbDataKit.Hosting.Systemd;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

namespace DbDataKit.Hosting.Cli;

/// <summary>
/// How each service of the suite is registered with the service manager: the Windows service (its command line, start mode, failure actions and the
/// folders it is given) and the systemd unit. Each server's <c>service install</c> and <c>service unit</c> take them from here, and so does
/// <c>dbdatakit setup single-server</c>, which registers every service of the machine by calling this library rather than each program's command line.
/// Design: docs/install.md#setting-up-one-machine-or-inviting-another
/// </summary>
public static class SuiteServiceRegistration
{
    /// <summary>
    /// The access list of a folder that holds a service's programs: the Administrators group and SYSTEM have full control, everyone else reads and
    /// runs, and the parent's grants are cut, so a user cannot plant a file beside a program a service runs.
    /// </summary>
    public static AclSpec ProgramFolderAcl { get; } = new(true,
    [
        new AclRule(LayoutPrincipal.Administrators, AccessRights.Full),
        new AclRule(LayoutPrincipal.System, AccessRights.Full),
        new AclRule(LayoutPrincipal.Users, AccessRights.ReadExecute),
        new AclRule(LayoutPrincipal.ServiceAccount, AccessRights.ReadExecute),
    ]);

    /// <summary>
    /// The shared service command's inputs for <paramref name="service"/>, run from <paramref name="binaryPath"/> on the data folder
    /// <paramref name="dataFolder"/>. The console, the Runner and the Workspace are registered with the folder on their command line
    /// (<c>server run --repo &lt;folder&gt;</c>); the Gateway is registered as <c>server run</c> and finds its folder from the service's environment, unless
    /// <paramref name="namedDataFolder"/> asks for the folder on its command line as well (<c>--Gateway:DataDirectory=&lt;folder&gt;</c>).
    /// <paramref name="name"/> is the Windows service's name, the table's (<see cref="SuiteService.WindowsName"/>) when null.
    /// </summary>
    public static ServiceCommandContext Context(SuiteService service, string binaryPath, string dataFolder, Func<ServiceAccount, IPrincipalResolver> principals,
        string? version = null, string? name = null, bool namedDataFolder = false)
    {
        var serviceName = name ?? service.WindowsName;
        var installDirectory = Path.GetDirectoryName(binaryPath) ?? binaryPath;
        var admin = new AclRule(LayoutPrincipal.Administrators, AccessRights.Full);
        var system = new AclRule(LayoutPrincipal.System, AccessRights.Full);
        AclRule Svc(AccessRights r) => new(LayoutPrincipal.ServiceAccount, r);
        var (binaries, data) = Purposes(service);

        var layout = DirectoryLayout.Create(serviceName,
        [
            new LayoutEntry(LayoutRole.InstallRoot, installDirectory, new AclSpec(false, [admin, system, Svc(AccessRights.ReadExecute)]), binaries),
            // the install makes only this folder, owned by the Administrators group; the service creates everything inside it at its first start
            // Design: docs/install.md#a-service-owns-its-data
            new LayoutEntry(LayoutRole.Data, dataFolder, new AclSpec(true, [admin, system, Svc(AccessRights.Modify)], LayoutPrincipal.Administrators), data),
        ]);

        var isGateway = service == SuiteServices.Gateway;
        return new ServiceCommandContext
        {
            ServiceName = serviceName,
            DisplayName = service.DisplayName,
            Description = service.Description,
            BinaryPath = binaryPath,
            Version = version,
            // the service runs the server, with its folder named so it does not depend on the account's profile; the Gateway reads its folder from
            // the service's environment
            Arguments = isGateway
                ? namedDataFolder ? ["server", "run", GatewayDataFolderArgument(dataFolder)] : ["server", "run"]
                : ["server", "run", "--repo", dataFolder],
            Layout = layout,
            PrincipalResolver = principals,
            SystemdUnit = () => UnitOptions(service, binaryPath.StartsWith('/') ? binaryPath : $"/opt/{service.Program}/{service.Program}"),
            // start at boot (not delayed); restart after a crash after 5s, 5s, then 60s; reset after a day
            StartMode = ServiceStartMode.Automatic,
            Failure = new FailurePolicy([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)], TimeSpan.FromDays(1)),
            DataDirectoryArgument = isGateway ? path => [GatewayDataFolderArgument(path)] : path => ["--repo", path],
            StartupService = service,
        };
    }

    /// <summary>
    /// The systemd unit of <paramref name="service"/> running <paramref name="executable"/>: as the service's system user in the group
    /// <see cref="SuiteServices.LinuxGroup"/>, with its data folder <c>/var/lib/dbdatakit/&lt;folder&gt;</c> and its logs folder
    /// <c>/var/log/dbdatakit/&lt;program&gt;</c>, which systemd makes for that user at each start. <paramref name="extraArguments"/> are added to the
    /// command line (<c>--skip-folder-permissions-check</c>).
    /// <paramref name="name"/> is the unit's name, the program when null; a Gateway registered under another name keeps a folder of that name.
    /// </summary>
    public static SystemdUnitOptions UnitOptions(SuiteService service, string executable, string? name = null, IReadOnlyList<string>? extraArguments = null)
    {
        var unit = name ?? service.Program;
        var isGateway = service == SuiteServices.Gateway;
        // a second Gateway on one machine, under another name, keeps a folder of that name beside the first
        var folder = isGateway && !string.Equals(unit, service.Program, StringComparison.Ordinal) ? unit : service.DataFolder;
        var extra = extraArguments is { Count: > 0 } ? " " + string.Join(' ', extraArguments) : "";
        return new SystemdUnitOptions
        {
            Name = unit,
            Description = service.Description,
            ExecStart = isGateway ? $"{executable} server run{extra}" : $"{executable} server run --repo /var/lib/dbdatakit/{folder}{extra}",
            WorkingDirectory = executable[..executable.LastIndexOf('/')],
            // the system user install.sh --system (or setup single-server) makes, in the group dbdatakit; systemd makes /var/lib/dbdatakit/<folder> and
            // /var/log/dbdatakit/<program> for it at each start, owned by that user and closed to every other one, so the service creates everything
            // inside them as itself
            // Design: docs/install.md#a-service-owns-its-data
            DynamicUser = false,
            User = service.LinuxUser,
            Group = SuiteServices.LinuxGroup,
            StateDirectories = [$"dbdatakit/{folder}"],
            StateDirectoryMode = "0700",
            LogsDirectories = [$"dbdatakit/{service.LogFolder}"],
            LogsDirectoryMode = "0700",
            // the Gateway is registered with no folder on its command line, so the unit names its folder in the variable the Gateway reads it from
            Environment = isGateway
                ? new Dictionary<string, string> { [$"DBDATAKIT_DATA_{SuiteServices.Gateway.DataFolder.ToUpperInvariant()}"] = $"/var/lib/dbdatakit/{folder}" }
                : new Dictionary<string, string>(),
        };
    }

    private static string GatewayDataFolderArgument(string folder) => $"--Gateway:DataDirectory={folder}";

    // what the service install's plan says each folder is for
    private static (string Binaries, string Data) Purposes(SuiteService service) => service.Program switch
    {
        "dbdatakit" => ("Binaries and the web app; the service can read and run them but not change them.",
            "The users, sessions, invitations and the certificate; Users have no access."),
        "dbdatagateway" => ("Binaries; the service can read and run them but not change them.",
            "Settings (config/), the generated TLS certificate and its protected password; Users have no access."),
        "dbdatarunner" => ("Binaries and the task host; the service can read and run them but not change them.",
            "The server's state, certificate, package cache and scratch space; Users have no access."),
        "dbdataworkspace" => ("Binaries; the service can read and run them but not change them.",
            "The server's identity, paired clients, certificate, projects and workspaces; Users have no access."),
        _ => throw new ArgumentException($"'{service.Program}' is not a service of the suite.", nameof(service)),
    };
}
