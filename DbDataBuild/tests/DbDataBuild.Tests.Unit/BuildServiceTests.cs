using DbDataBuild.Cli.Server;
using DbDataKit.Cli;
using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

namespace DbDataBuild.Tests.Unit;

/// <summary>The Build server as a service (plan 13, R05): the shared verbs over fakes of the machine, and the unit systemd would run.</summary>
public sealed class BuildServiceTests
{
    private const string Binary = @"C:\Program Files\DbDataBuild\current\dbdatabuild.exe";
    private const string Data = @"C:\ProgramData\DbDataBuild\server";

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

    private static (Noun Noun, InMemoryServiceControlManager Scm) Service(bool elevated = true, string binary = Binary)
    {
        var scm = new InMemoryServiceControlManager();
        var env = new BuildServiceEnvironment
        {
            Scm = scm,
            Acl = new NoAcl(),
            Elevation = new Elevated(elevated),
            BinaryPath = binary,
            DefaultDataDirectory = Data,
            PrincipalResolver = _ => new WellKnownPrincipalResolver("S-1-5-80-1"),
        };
        return (BuildService.Noun(env), scm);
    }

    private static async Task<(int Exit, string Out, string Err)> Run(Noun noun, params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var exit = await new NounRouter("dbdatabuild", [noun]).RunAsync(args, new CliContext(o, e, TextReader.Null));
        return (exit, o.ToString(), e.ToString());
    }

    [Fact]
    public async Task Install_registers_the_service_to_run_the_server_in_its_data_directory()
    {
        var (noun, scm) = Service();

        var install = await Run(noun, "service", "install");

        Assert.Equal(0, install.Exit);
        var config = scm.GetConfig(BuildService.RegistrationName)!;
        Assert.Contains("server run --repo", config.BinaryPathName);
        Assert.Contains(Data, config.BinaryPathName);
        Assert.Equal(0, (await Run(noun, "service", "status")).Exit);
    }

    [Fact]
    public async Task A_data_directory_given_at_install_wins_over_the_default()
    {
        var (noun, scm) = Service();

        Assert.Equal(0, (await Run(noun, "service", "install", "--data-dir", @"D:\Build")).Exit);

        var path = scm.GetConfig(BuildService.RegistrationName)!.BinaryPathName;
        Assert.True(path.LastIndexOf(@"D:\Build", StringComparison.Ordinal) > path.IndexOf(Data, StringComparison.Ordinal), path);
    }

    [Fact]
    public async Task Uninstall_removes_it_and_a_verb_that_needs_elevation_says_so_without_changing_anything()
    {
        var (noun, scm) = Service();
        await Run(noun, "service", "install");
        Assert.Equal(0, (await Run(noun, "service", "uninstall")).Exit);
        Assert.Null(scm.GetConfig(BuildService.RegistrationName));

        var (plain, none) = Service(elevated: false);
        Assert.Equal(ExitCodes.NotElevated, (await Run(plain, "service", "install")).Exit);
        Assert.Null(none.GetConfig(BuildService.RegistrationName));
    }

    [Fact]
    public async Task The_systemd_unit_runs_the_server_with_a_state_directory_and_no_account_to_manage()
    {
        var (noun, _) = Service(binary: "/opt/dbdatabuild/dbdatabuild");

        var unit = await Run(noun, "service", "unit");

        Assert.Equal(0, unit.Exit);
        Assert.Contains("ExecStart=/opt/dbdatabuild/dbdatabuild server run --repo /var/lib/dbdatabuild", unit.Out);
        Assert.Contains("WorkingDirectory=/opt/dbdatabuild", unit.Out);
        Assert.Contains("DynamicUser=yes", unit.Out);
        Assert.Contains("StateDirectory=dbdatabuild", unit.Out);
        Assert.Contains("Description=DbDataBuild server", unit.Out);
    }

    [Fact]
    public void The_service_verbs_are_the_shared_ones()
    {
        Assert.Equal(["install", "uninstall", "status", "start", "stop", "restart", "doctor", "unit"], Service().Noun.Verbs.Select(v => v.Name));
    }
}
