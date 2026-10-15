using System.Security;

namespace DbData.Hosting.WindowsService.Tests;

public class ProvisionerTests
{
    private static ServiceInstallSpec Spec(Action<ServiceInstallSpecBuilder>? tweak = null)
    {
        var b = new ServiceInstallSpecBuilder();
        tweak?.Invoke(b);
        return b.Build();
    }

    internal sealed class ServiceInstallSpecBuilder
    {
        public ServiceAccount? Account;
        public bool AllowLocalSystem;
        public string[] Args = [];
        public string? Version = "1.0.0";
        public string? Description = "desc";
        public string? EventLog = "TestProd";

        public ServiceInstallSpec Build() => new()
        {
            Name = "TestProd",
            BinaryPath = @"C:\Program Files\TestProd\current\TestProd.exe",
            Arguments = Args,
            Account = Account,
            AllowLocalSystem = AllowLocalSystem,
            Version = Version,
            Description = Description,
            EventLogSource = EventLog,
        };
    }

    private static SecureString Secret(string s)
    {
        var ss = new SecureString();
        foreach (var c in s) ss.AppendChar(c);
        return ss;
    }

    [Fact]
    public void Default_identity_is_the_virtual_account()
    {
        var spec = Spec();
        Assert.Equal(ServiceAccountKind.VirtualAccount, spec.EffectiveAccount.Kind);
        Assert.Equal(@"NT SERVICE\TestProd", spec.EffectiveAccount.Name);
    }

    [Theory]
    [InlineData(null, ServiceAccountKind.VirtualAccount)]
    [InlineData("virtual", ServiceAccountKind.VirtualAccount)]
    [InlineData(@"CORP\svc-db$", ServiceAccountKind.Gmsa)]
    [InlineData("LocalSystem", ServiceAccountKind.LocalSystem)]
    [InlineData(@"NT AUTHORITY\SYSTEM", ServiceAccountKind.LocalSystem)]
    [InlineData(@".\SYSTEM", ServiceAccountKind.LocalSystem)]
    [InlineData(@"NT AUTHORITY\NetworkService", ServiceAccountKind.NetworkService)]
    [InlineData(@"CORP\alice", ServiceAccountKind.User)]
    public void Account_parsing(string? text, ServiceAccountKind kind) =>
        Assert.Equal(kind, ServiceAccount.Parse(text, "TestProd").Kind);

    [Fact]
    public void Install_creates_then_is_idempotent()
    {
        var scm = new InMemoryServiceControlManager();
        var registrar = new InMemoryEventLogSourceRegistrar();
        var p = new ServiceProvisioner(scm, registrar);
        var spec = Spec();

        Assert.Equal(InstallOutcome.Created, p.Install(spec).Outcome);
        Assert.True(registrar.Exists("TestProd"));
        Assert.Equal(InstallOutcome.Unchanged, p.Install(spec).Outcome);
        Assert.Single(scm.Operations, o => o.StartsWith("create"));
        Assert.DoesNotContain(scm.Operations, o => o.StartsWith("reconfigure"));
    }

    [Fact]
    public void Install_reconfigures_on_drift()
    {
        var scm = new InMemoryServiceControlManager();
        var p = new ServiceProvisioner(scm);
        p.Install(Spec());
        var result = p.Install(Spec(b => b.Args = ["--data-dir", @"D:\x"]));
        Assert.Equal(InstallOutcome.Reconfigured, result.Outcome);
        Assert.Contains("command line", result.Plan.Render());
    }

    [Fact]
    public void Dry_run_changes_nothing_and_renders_plan()
    {
        var scm = new InMemoryServiceControlManager();
        var registrar = new InMemoryEventLogSourceRegistrar();
        var result = new ServiceProvisioner(scm, registrar).Install(Spec(), options: new InstallOptions { DryRun = true });
        Assert.Equal(InstallOutcome.DryRun, result.Outcome);
        Assert.Empty(scm.Operations);
        Assert.Empty(registrar.Sources);
        var text = result.Plan.Render();
        Assert.Contains("create", text);
        Assert.Contains(@"NT SERVICE\TestProd", text);
    }

    [Fact]
    public void LocalSystem_needs_explicit_allow()
    {
        var p = new ServiceProvisioner(new InMemoryServiceControlManager());
        var ex = Assert.Throws<ServiceInstallException>(() => p.Install(Spec(b => b.Account = ServiceAccount.LocalSystem)));
        Assert.Contains("LocalSystem", ex.Message);
        Assert.Equal(InstallOutcome.Created, p.Install(Spec(b => { b.Account = ServiceAccount.LocalSystem; b.AllowLocalSystem = true; })).Outcome);
    }

    [Fact]
    public void Explicit_account_requires_password_and_others_refuse_one()
    {
        var scm = new InMemoryServiceControlManager();
        var p = new ServiceProvisioner(scm);
        var user = Spec(b => b.Account = ServiceAccount.User(@"CORP\alice"));
        Assert.Throws<ServiceInstallException>(() => p.Install(user));
        using var pw = Secret("hunter2");
        Assert.Equal(InstallOutcome.Created, p.Install(user, pw).Outcome);
        Assert.True(scm.LastPasswordSupplied);

        var virt = Spec();
        Assert.Throws<ServiceInstallException>(() => new ServiceProvisioner(new InMemoryServiceControlManager()).Install(virt, pw));
    }

    [Fact]
    public void Password_never_appears_in_plan_or_exception_text()
    {
        using var pw = Secret("hunter2");
        var p = new ServiceProvisioner(new InMemoryServiceControlManager());
        var result = p.Install(Spec(b => b.Account = ServiceAccount.User(@"CORP\alice")), pw, new InstallOptions { DryRun = true });
        Assert.DoesNotContain("hunter2", result.Plan.Render());
    }

    [Fact]
    public void Arguments_with_passwords_are_refused()
    {
        var p = new ServiceProvisioner(new InMemoryServiceControlManager());
        Assert.Throws<ServiceInstallException>(() => p.Install(Spec(b => b.Args = ["--password=abc"])));
    }

    [Fact]
    public void Downgrade_is_refused_unless_allowed()
    {
        var p = new ServiceProvisioner(new InMemoryServiceControlManager());
        var spec = Spec(b => b.Version = "1.0.0");
        Assert.Throws<ServiceInstallException>(() => p.Install(spec, options: new InstallOptions { InstalledVersion = "1.2.0" }));
        Assert.Equal(InstallOutcome.Created, p.Install(spec, options: new InstallOptions { InstalledVersion = "1.2.0-beta", AllowDowngrade = true }).Outcome);
    }

    [Fact]
    public async Task Uninstall_stops_then_deletes()
    {
        var scm = new InMemoryServiceControlManager();
        var p = new ServiceProvisioner(scm, null, _ => Task.CompletedTask);
        p.Install(Spec());
        await p.StartAsync("TestProd", TimeSpan.FromSeconds(1));
        Assert.True(await p.UninstallAsync("TestProd", TimeSpan.FromSeconds(1)));
        Assert.Equal(["create:TestProd", "start:TestProd", "stop:TestProd", "delete:TestProd"], scm.Operations);
        Assert.False(await p.UninstallAsync("TestProd", TimeSpan.FromSeconds(1)));
    }

    [Theory]
    [InlineData(@"C:\a b\c.exe", @"""C:\a b\c.exe""")]
    [InlineData(@"say ""hi""", @"""say \""hi\""""")]
    [InlineData(@"C:\dir with space\", @"""C:\dir with space\\""")]
    public void Command_line_quoting(string input, string expected) =>
        Assert.Equal(expected, CommandLineQuoting.Quote(input, forceQuotes: true));

    [Fact]
    public void Command_line_quotes_exe_always()
    {
        Assert.Equal(@"""C:\x\a.exe"" --flag ""a b""", CommandLineQuoting.Build(@"C:\x\a.exe", ["--flag", "a b"]));
    }

    [Fact]
    public void Error_codes_are_typed()
    {
        Assert.Equal(ServiceControlError.NotFound, ServiceControlException.Map(1060));
        Assert.Equal(ServiceControlError.AccessDenied, ServiceControlException.Map(5));
        Assert.Equal(ServiceControlError.AlreadyExists, ServiceControlException.Map(1073));
    }
}
