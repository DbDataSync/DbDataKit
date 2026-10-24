using DbDataKit.Cli;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Hosting.Systemd;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

namespace DbDataKit.Hosting.Cli.Tests;

public sealed class HostingNounTests
{
    private sealed class Elevated(bool value) : IElevationChecker
    {
        public bool IsElevated => value;
    }

    private sealed class NoAcl : IAclService
    {
        public bool DirectoryExists(string path) => false;
        public IEnumerable<string> EnumerateFiles(string directory) => [];
        public ObservedAcl? Read(string path) => null;
        public void Apply(string directory, AclSpecification spec) { }
    }

    private static (Noun Noun, InMemoryServiceControlManager Scm) ServiceNoun(bool elevated = true)
    {
        var scm = new InMemoryServiceControlManager();
        var context = new ServiceCommandContext
        {
            ServiceName = "Prod",
            BinaryPath = @"C:\Program Files\Prod\current\Prod.exe",
            Layout = DirectoryLayout.ForProduct("Prod", @"C:\PF", @"C:\PD"),
            PrincipalResolver = _ => new WellKnownPrincipalResolver("S-1-5-80-1"),
            SystemdUnit = () => new SystemdUnitOptions { Name = "prod", ExecStart = "/opt/prod/prod" },
            EventLogSource = "Prod",
            Version = "2.0.0",
        };
        var command = new ServiceCommand(context, scm, new NoAcl(), new Elevated(elevated), new InMemoryEventLogSourceRegistrar(), true, _ => Task.CompletedTask);
        return (HostingNouns.Service(command), scm);
    }

    private static async Task<(int Exit, string Out, string Error)> Run(Noun noun, params string[] args)
    {
        var router = new NounRouter("prod", [noun]);
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await router.RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public void The_service_noun_has_the_shared_verbs()
    {
        var (noun, _) = ServiceNoun();

        Assert.Equal(["install", "uninstall", "status", "start", "stop", "restart", "doctor"], noun.Verbs.Select(v => v.Name));
    }

    [Fact]
    public async Task Service_install_through_the_noun_registers_the_service_and_service_status_reports_it()
    {
        var (noun, scm) = ServiceNoun();

        var install = await Run(noun, "service", "install");
        var status = await Run(noun, "service", "status");

        Assert.Equal(0, install.Exit);
        Assert.NotNull(scm.GetConfig("Prod"));
        Assert.Contains("installed", install.Out);
        Assert.Equal(0, status.Exit);
    }

    [Fact]
    public async Task A_service_verb_that_needs_elevation_says_so_through_the_noun()
    {
        var (noun, scm) = ServiceNoun(elevated: false);

        var install = await Run(noun, "service", "install");

        Assert.Equal(ExitCodes.NotElevated, install.Exit);
        Assert.Null(scm.GetConfig("Prod"));
    }

    private static (Noun Noun, string Directory) CertNoun()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dbdata-noun-" + Guid.NewGuid().ToString("N"));
        var options = new CertificateOptions { Mode = CertificateMode.SelfSigned, Path = Path.Combine(dir, "s.pfx") };
        var secrets = new InMemorySecretSource();
        return (HostingNouns.Cert(new CertCommand(options, secrets, new CertificateProvider(secrets, fqdnLookup: _ => null))), dir);
    }

    [Fact]
    public void The_cert_noun_has_the_shared_verbs_and_marks_what_changes_files()
    {
        var (noun, _) = CertNoun();

        Assert.Equal(["show", "ensure", "check", "renew"], noun.Verbs.Select(v => v.Name));
        Assert.Equal(EffectClass.OfflineOnly, noun.Verbs.Single(v => v.Name == "show").Effect);
        Assert.Equal(EffectClass.RepoFilesOnly, noun.Verbs.Single(v => v.Name == "renew").Effect);
    }

    [Fact]
    public async Task Cert_check_through_the_noun_fails_when_there_is_no_certificate_and_ensure_makes_one()
    {
        var (noun, dir) = CertNoun();
        try
        {
            Assert.Equal(ExitCodes.Failure, (await Run(noun, "cert", "check")).Exit);
            Assert.Equal(ExitCodes.Ok, (await Run(noun, "cert", "ensure")).Exit);
            Assert.Equal(ExitCodes.Ok, (await Run(noun, "cert", "check")).Exit);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
