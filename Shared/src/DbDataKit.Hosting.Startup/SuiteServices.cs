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
