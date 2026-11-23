using DbDataKit.Hosting.Startup;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

namespace DbDataKit.Hosting.Cli;

/// <summary>
/// The Windows service control manager for <c>setup single-server</c>: each service registered as its own <c>service install</c> registers it (as
/// <c>NT SERVICE\&lt;name&gt;</c>), its top folder owned by the Administrators group with the service's grant, and its program folder closed to
/// writes by anyone but administrators and SYSTEM.
/// Design: docs/install.md#setting-up-one-machine-or-inviting-another
/// </summary>
/// <param name="scm">The service control manager.</param>
/// <param name="acl">Applies the folders' access lists.</param>
/// <param name="elevation">Whether the command runs elevated.</param>
/// <param name="allowLogsWriter">Gives a service's account its grant on an existing shared logs folder (<see cref="SharedLogsFolder.AllowWriter"/> by
/// default); tests replace it.</param>
public sealed class WindowsSuiteServiceHost(IServiceControlManager scm, IAclService acl, IElevationChecker elevation, Action<string, string>? allowLogsWriter = null) : ISuiteServiceHost
{
    private readonly Action<string, string> _allowLogsWriter = allowLogsWriter ?? SharedLogsFolder.AllowWriter;

    private readonly ServiceProvisioner _provisioner = new(scm);

    public bool IsElevated => elevation.IsElevated;

    public RegistrationCheck Check(SuiteRegistration registration)
    {
        var existing = scm.GetConfig(registration.Spec.Name);
        if (existing is null) return new(RegistrationState.NotRegistered);
        return _provisioner.Plan(registration.Spec).IsNoOp
            ? new(RegistrationState.Same, existing.BinaryPathName)
            : new(RegistrationState.Different, existing.BinaryPathName);
    }

    public void Register(SuiteRegistration registration) => _provisioner.Install(registration.Spec);

    public void PrepareFolders(SuiteRegistration registration)
    {
        // the virtual account's SID is known once the service exists, which it does by now
        var principals = registration.Context.PrincipalResolver(registration.Spec.EffectiveAccount);
        foreach (var entry in registration.Context.Layout.Entries.Where(e => e.Role == LayoutRole.Data && e.Acl is not null))
            acl.Apply(entry, principals);
        acl.Apply(new LayoutEntry(LayoutRole.InstallRoot, registration.Member.ProgramFolder, SuiteServiceRegistration.ProgramFolderAcl,
            "The program; administrators and SYSTEM change it, everyone else reads and runs it."), principals);
        // a shared logs folder an earlier start made before this service was registered still lets this service make its own folder there
        _allowLogsWriter(SuiteServices.LogsRootFor(registration.Service, registration.Member.DataFolder), principals.ResolveSid(LayoutPrincipal.ServiceAccount));
    }

    // the request inherits the folder's access list, which grants the service Modify: it reads the request and deletes it once joined
    public void GiveToService(SuiteRegistration registration, string file) { }

    public SuiteServiceState State(SuiteService service)
    {
        if (scm.GetConfig(service.WindowsName) is null) return SuiteServiceState.NotRegistered;
        return scm.QueryStatus(service.WindowsName).State switch
        {
            ServiceRunState.Running => SuiteServiceState.Running,
            ServiceRunState.Stopped => SuiteServiceState.Stopped,
            _ => SuiteServiceState.Starting,
        };
    }

    public void Start(SuiteService service) => scm.Start(service.WindowsName);

    public Task StopAsync(SuiteService service, TimeSpan timeout) => _provisioner.StopAsync(service.WindowsName, timeout);
}
