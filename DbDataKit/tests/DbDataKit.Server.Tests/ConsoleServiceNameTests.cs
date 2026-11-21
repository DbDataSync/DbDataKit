using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.Startup;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

namespace DbDataKit.Server.Tests;

/// <summary>The service registration takes its name, account, display name and description from the suite's table (docs/install.md, "Service names, accounts and descriptions").</summary>
public sealed class ConsoleServiceNameTests
{
    [Fact]
    public void The_Windows_service_and_the_systemd_unit_are_named_and_described_as_the_suites_table_says()
    {
        var env = new ConsoleServiceEnvironment
        {
            Scm = new InMemoryServiceControlManager(),
            Acl = new FileSystemAclService(),
            Elevation = new ElevationDetector(),
            BinaryPath = Path.Combine(Path.GetTempPath(), "dbdatakit"),
            DefaultDataDirectory = Path.Combine(Path.GetTempPath(), "dbdatakit-data"),
        };

        var context = ConsoleService.Context(env);

        Assert.Equal("DbDataKit", context.ServiceName);
        Assert.Equal(@"NT SERVICE\DbDataKit", ServiceAccount.Parse("virtual", context.ServiceName).Name);
        Assert.Equal("DbDataKit", context.DisplayName);
        Assert.Equal(SuiteServices.Console.Description, context.Description);
        Assert.Same(SuiteServices.Console, context.StartupService);
        Assert.Equal(SuiteServices.Console.Description, ConsoleService.UnitOptions("dbdatakit", "/opt/dbdatakit/dbdatakit").Description);
    }
}
