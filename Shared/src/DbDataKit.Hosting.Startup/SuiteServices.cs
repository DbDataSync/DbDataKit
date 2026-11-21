namespace DbDataKit.Hosting.Startup;

/// <summary>
/// One service of the suite, as the install, the service manager and the startup sequence name it.
/// </summary>
/// <param name="Program">The program in lower case (<c>dbdatagateway</c>): the systemd unit, the Linux system user, the folder of its logs and the name in <c>startup-failure-&lt;program&gt;.txt</c>.</param>
/// <param name="WindowsName">The Windows service (<c>DbDataGateway</c>); the service runs as <c>NT SERVICE\&lt;WindowsName&gt;</c>.</param>
/// <param name="Description">The description the service manager shows.</param>
/// <param name="DataFolder">The service's folder under the data root (<c>gateway</c> in <c>C:\ProgramData\DbDataKit\gateway</c>).</param>
/// <param name="StartOrder">The order a machine that runs several services starts them in: the console first, since the others join it.</param>
public sealed record SuiteService(string Program, string WindowsName, string Description, string DataFolder, int StartOrder)
{
    /// <summary>The name the service manager lists the Windows service under: the service's own name (<c>DbDataGateway</c>).</summary>
    public string DisplayName => WindowsName;

    /// <summary>The Windows account the service runs as: <c>NT SERVICE\DbDataGateway</c>.</summary>
    public string WindowsAccount => $@"NT SERVICE\{WindowsName}";

    /// <summary>The Linux system user the service runs as, in the group <see cref="SuiteServices.LinuxGroup"/>.</summary>
    public string LinuxUser => Program;

    /// <summary>The account the service runs as on this operating system.</summary>
    public string Account => OperatingSystem.IsWindows() ? WindowsAccount : LinuxUser;

    /// <summary>The name of the file a start of this service leaves in the shared logs folder: <c>startup-failure-dbdatagateway.txt</c>.</summary>
    public string FailureFileName => $"startup-failure-{Program}.txt";

    /// <summary>The service's own folder of logs under the shared logs folder: <c>dbdatagateway</c>.</summary>
    public string LogFolder => Program;

    /// <summary>
    /// The service's settings file, relative to its data folder: <c>settings.yaml</c> for the console, <c>config/server.yaml</c> for the Gateway,
    /// <c>server.yaml</c> for the Runner and the Workspace.
    /// </summary>
    public string SettingsFile => Program switch
    {
        "dbdatakit" => "settings.yaml",
        "dbdatagateway" => Path.Combine("config", "server.yaml"),
        _ => "server.yaml",
    };

    /// <summary>The service's sub-block of the suite's ports (<see cref="SuiteServices.SuitePorts"/>): the Runner's is 7840 to 7859.</summary>
    public PortRange Ports => SuiteServices.PortsOf(this);
}

/// <summary>
/// The four services of the suite: the one place a service's name, account, description and folders are spelled. Each server's <c>service install</c>
/// (the Windows service and the systemd unit), the startup sequence and <c>dist/dist.py</c> (the products' installers and the suite's Scoop manifest, through
/// <c>dist/suite_services.py</c>, which reads this file) take them from here.
/// Design: docs/install.md#one-startup-sequence
/// </summary>
public static class SuiteServices
{
    /// <summary>The Linux group every service's user is in; it owns the shared logs folder.</summary>
    public const string LinuxGroup = "dbdatakit";

    public static SuiteService Console { get; } = new("dbdatakit", "DbDataKit",
        "DbDataKit console: sign-in, users and management of the Gateway, Runner and Workspace servers.", "server", 1);

    public static SuiteService Gateway { get; } = new("dbdatagateway", "DbDataGateway",
        "DbDataKit gateway: serves data, semantic models, master data, apps, dashboards and reports to people, applications and agents.", "gateway", 2);

    public static SuiteService Runner { get; } = new("dbdatarunner", "DbDataRunner",
        "DbDataKit runner: runs published tasks (data loads, replication, deployments and quality checks) on schedule or on demand.", "runner", 3);

    public static SuiteService Workspace { get; } = new("dbdataworkspace", "DbDataWorkspace",
        "DbDataKit workspace: where people and agents author, test and publish apps, data models, replications and master data.", "workspace", 4);

    /// <summary>
    /// The ports of the suite, 7800 to 7899: outside Windows' default ports (80, 443, 135, 445, 3389, 5040, 5357, 5985, 5986, 7680, 47001, the dynamic
    /// range 49152 to 65535) and common developer ports. Each service listens on a port of its own sub-block below; 7880 to 7899 are spare.
    /// <c>scripts/check_default_ports.py</c> reads these declarations and checks that the sub-blocks lie in the range and do not overlap.
    /// Design: docs/install.md#ports
    /// </summary>
    public static PortRange SuitePorts { get; } = new(7800, 7899);

    /// <summary>The console's ports; the identity home on its own listens on one of them too.</summary>
    public static PortRange ConsolePorts { get; } = new(7800, 7819);

    /// <summary>The Gateway's ports.</summary>
    public static PortRange GatewayPorts { get; } = new(7820, 7839);

    /// <summary>The Runner's ports.</summary>
    public static PortRange RunnerPorts { get; } = new(7840, 7859);

    /// <summary>The Workspace's ports.</summary>
    public static PortRange WorkspacePorts { get; } = new(7860, 7879);

    /// <summary>The ports no service uses yet.</summary>
    public static PortRange SparePorts { get; } = new(7880, 7899);

    /// <summary>The sub-block of <paramref name="service"/>.</summary>
    public static PortRange PortsOf(SuiteService service) => service.Program switch
    {
        "dbdatakit" => ConsolePorts,
        "dbdatagateway" => GatewayPorts,
        "dbdatarunner" => RunnerPorts,
        "dbdataworkspace" => WorkspacePorts,
        _ => throw new ArgumentException($"'{service.Program}' is not a service of the suite.", nameof(service)),
    };

    /// <summary>The four services in their start order.</summary>
    public static IReadOnlyList<SuiteService> All { get; } = [Console, Gateway, Runner, Workspace];

    /// <summary>The service whose program is <paramref name="program"/> (any case), or null.</summary>
    public static SuiteService? Find(string program) =>
        All.FirstOrDefault(s => string.Equals(s.Program, program, StringComparison.OrdinalIgnoreCase)
                                || string.Equals(s.WindowsName, program, StringComparison.OrdinalIgnoreCase));

    /// <summary>The data root of an install: <c>C:\ProgramData\DbDataKit</c> on Windows, <c>/var/lib/dbdatakit</c> elsewhere.</summary>
    public static string MachineDataRoot =>
        OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DbDataKit")
            : "/var/lib/dbdatakit";

    /// <summary>The shared logs folder of an install: <c>C:\ProgramData\DbDataKit\logs</c> on Windows, <c>/var/log/dbdatakit</c> elsewhere.</summary>
    public static string MachineLogsRoot =>
        OperatingSystem.IsWindows() ? Path.Combine(MachineDataRoot, "logs") : "/var/log/dbdatakit";

    /// <summary>The data folder of a service in an install: <c>C:\ProgramData\DbDataKit\gateway</c>.</summary>
    public static string MachineDataFolder(SuiteService service) => Path.Combine(MachineDataRoot, service.DataFolder);

    /// <summary>
    /// The shared logs folder of a server whose data folder is <paramref name="dataFolder"/>. In an install the data folder is the service's folder under
    /// the data root (<c>C:\ProgramData\DbDataKit\runner</c>), and the logs folder is <c>logs</c> beside it (<c>C:\ProgramData\DbDataKit\logs</c>);
    /// under Linux's data root <c>/var/lib/dbdatakit</c> it is <c>/var/log/dbdatakit</c>. A data folder named otherwise (a run from a terminal on a
    /// folder of its own, a test's folder) keeps its logs inside it, in <c>&lt;data folder&gt;/logs</c>, so such a run never writes to the machine's folder.
    /// Design: docs/install.md#logs-and-the-startup-failure-file
    /// </summary>
    public static string LogsRootFor(SuiteService service, string dataFolder)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataFolder));
        if (string.Equals(full, Path.TrimEndingDirectorySeparator(Path.GetFullPath(MachineDataFolder(service))), comparison)) return MachineLogsRoot;
        if (string.Equals(Path.GetFileName(full), service.DataFolder, comparison) && Path.GetDirectoryName(full) is { Length: > 0 } root)
            return Path.Combine(root, "logs");
        return Path.Combine(full, "logs");
    }
}
