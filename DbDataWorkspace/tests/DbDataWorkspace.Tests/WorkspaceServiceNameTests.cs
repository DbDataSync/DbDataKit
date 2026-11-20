using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.Startup;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

using DbDataWorkspace.Server;

namespace DbDataWorkspace.Tests;

/// <summary>The service registration takes its name, account, display name and description from the suite's table (docs/install.md, "Service names, accounts and descriptions").</summary>
public sealed class WorkspaceServiceNameTests
{
    [Fact]
    public void The_Windows_service_and_the_systemd_unit_are_named_and_described_as_the_suites_table_says()
    {
        var env = new WorkspaceServiceEnvironment
        {
            Scm = new InMemoryServiceControlManager(),
            Acl = new FileSystemAclService(),
            Elevation = new ElevationDetector(),
            BinaryPath = Path.Combine(Path.GetTempPath(), "dbdataworkspace"),
            DefaultDataDirectory = Path.Combine(Path.GetTempPath(), "dbdataworkspace-data"),
        };

        var context = WorkspaceService.Context(env);

        Assert.Equal("DbDataWorkspace", context.ServiceName);
        Assert.Equal(@"NT SERVICE\DbDataWorkspace", ServiceAccount.Parse("virtual", context.ServiceName).Name);
        Assert.Equal("DbDataWorkspace", context.DisplayName);
        Assert.Equal(SuiteServices.Workspace.Description, context.Description);
        Assert.Same(SuiteServices.Workspace, context.StartupService);
        Assert.Equal(SuiteServices.Workspace.Description, WorkspaceService.UnitOptions("dbdataworkspace", "/opt/dbdataworkspace/dbdataworkspace").Description);
    }
}
