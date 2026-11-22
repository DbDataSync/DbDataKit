using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.Startup;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>The service registration takes its name, account, display name and description from the suite's table (docs/install.md, "Service names, accounts and descriptions").</summary>
public sealed class RunnerServiceNameTests
{
    [Fact]
    public void The_Windows_service_and_the_systemd_unit_are_named_and_described_as_the_suites_table_says()
    {
        var env = new RunnerServiceEnvironment
        {
            Scm = new InMemoryServiceControlManager(),
            Acl = new FileSystemAclService(),
            Elevation = new ElevationDetector(),
            BinaryPath = Path.Combine(Path.GetTempPath(), "dbdatarunner"),
            DefaultDataDirectory = Path.Combine(Path.GetTempPath(), "dbdatarunner-data"),
        };

        var context = RunnerService.Context(env);

        Assert.Equal("DbDataRunner", context.ServiceName);
        Assert.Equal(@"NT SERVICE\DbDataRunner", ServiceAccount.Parse("virtual", context.ServiceName).Name);
        Assert.Equal("DbDataRunner", context.DisplayName);
        Assert.Equal(SuiteServices.Runner.Description, context.Description);
        Assert.Same(SuiteServices.Runner, context.StartupService);
        Assert.Equal(SuiteServices.Runner.Description, RunnerService.UnitOptions("dbdatarunner", "/opt/dbdatarunner/dbdatarunner").Description);
        // the unit runs as the system user install.sh --system makes, and systemd makes its data and logs folders for it, closed to other users
        var unit = DbDataKit.Hosting.Systemd.SystemdUnitGenerator.Generate(RunnerService.UnitOptions("dbdatarunner", "/opt/dbdatarunner/dbdatarunner"));
        foreach (var line in new[] { "User=dbdatarunner", "Group=dbdatakit", "StateDirectory=dbdatakit/runner", "StateDirectoryMode=0700", "LogsDirectory=dbdatakit/dbdatarunner", "LogsDirectoryMode=0700" })
            Assert.Contains("\n" + line + "\n", unit);
        Assert.DoesNotContain("DynamicUser", unit);
    }
}
