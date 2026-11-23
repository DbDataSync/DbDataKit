using DbDataKit.Hosting.Startup;
using DbDataKit.Hosting.Systemd;
using DbDataKit.Hosting.WindowsService;

namespace DbDataKit.Hosting.Cli;

/// <summary>
/// A service of the suite found on this machine: its program and the data folder it runs on.
/// </summary>
/// <param name="Service">The service, as the suite's table names it.</param>
/// <param name="Binary">The program the service runs (<c>C:\Program Files\DbDataKit\runner\dbdatarunner.exe</c>, <c>/opt/dbdatarunner/dbdatarunner</c>).</param>
/// <param name="DataFolder">The service's data folder (<c>C:\ProgramData\DbDataKit\runner</c>).</param>
/// <param name="NamedDataFolder">The folder is not the one the service finds by itself, so the Gateway's command line names it too.</param>
public sealed record SetupMember(SuiteService Service, string Binary, string DataFolder, bool NamedDataFolder = false)
{
    /// <summary>The folder that holds the program.</summary>
    public string ProgramFolder => Path.GetDirectoryName(Path.GetFullPath(Binary)) ?? Binary;
}

/// <summary>What the service manager should hold for one service: the Windows service, the systemd unit, and whether the folder check is skipped.</summary>
public sealed record SuiteRegistration(SetupMember Member, ServiceCommandContext Context, ServiceInstallSpec Spec, SystemdUnitOptions Unit, bool SkipFolderPermissionsCheck)
{
    /// <summary>The registration of <paramref name="member"/>, as each server's own <c>service install</c> makes it.</summary>
    public static SuiteRegistration For(SetupMember member, bool skipFolderPermissionsCheck, Func<ServiceAccount, IPrincipalResolver>? principals = null)
    {
        var context = SuiteServiceRegistration.Context(member.Service, member.Binary, member.DataFolder, principals ?? (account => new WindowsPrincipalResolver(account)),
            namedDataFolder: member.NamedDataFolder);
        var spec = ServiceCommand.InstallSpec(context, skipFolderPermissionsCheck: skipFolderPermissionsCheck);
        // a unit is only written on Linux; elsewhere it names the program where install.sh puts it
        var executable = member.Binary.StartsWith('/') ? member.Binary : $"/opt/{member.Service.Program}/{member.Service.Program}";
        var unit = SuiteServiceRegistration.UnitOptions(member.Service, executable, extraArguments: skipFolderPermissionsCheck ? [FolderPermissions.Flag] : null);
        return new SuiteRegistration(member, context, spec, unit, skipFolderPermissionsCheck);
    }

    public SuiteService Service => Member.Service;
}

/// <summary>Whether a service is registered as it should be.</summary>
public enum RegistrationState
{
    NotRegistered,

    /// <summary>Registered with exactly the command, account and settings wanted.</summary>
    Same,

    /// <summary>Registered otherwise: another program, another command line or other settings.</summary>
    Different,
}

/// <summary>What the service manager holds for a service; <see cref="Registered"/> is its command line when it is registered.</summary>
public sealed record RegistrationCheck(RegistrationState State, string? Registered = null);

/// <summary>Whether a registered service runs.</summary>
public enum SuiteServiceState
{
    NotRegistered,
    Stopped,
    Starting,
    Running,

    /// <summary>The service manager says the service failed (systemd's <c>failed</c>).</summary>
    Failed,
}

/// <summary>
/// The machine's service manager as <c>setup single-server</c> uses it: the Windows service control manager (<see cref="WindowsSuiteServiceHost"/>) or
/// systemd (<see cref="SystemdSuiteServiceHost"/>).
/// </summary>
public interface ISuiteServiceHost
{
    /// <summary>The command runs as an administrator (Windows) or root.</summary>
    bool IsElevated { get; }

    RegistrationCheck Check(SuiteRegistration registration);

    /// <summary>Registers the service, or registers it again when it is registered otherwise.</summary>
    void Register(SuiteRegistration registration);

    /// <summary>
    /// Gives the service's top folder its owner and access list, and the program folder its access list: only administrators may change it, everyone
    /// else reads and runs.
    /// </summary>
    void PrepareFolders(SuiteRegistration registration);

    /// <summary>Makes a file the command wrote into the service's folder readable by the service (the join request).</summary>
    void GiveToService(SuiteRegistration registration, string file);

    SuiteServiceState State(SuiteService service);

    /// <summary>Asks the service manager to start the service, without waiting for it to run.</summary>
    void Start(SuiteService service);

    /// <summary>Stops the service and waits for it to stop.</summary>
    Task StopAsync(SuiteService service, TimeSpan timeout);
}
