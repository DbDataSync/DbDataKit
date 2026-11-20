namespace DbDataKit.Hosting.Startup.Tests;

public sealed class SuiteServicesTests
{
    [Fact]
    public void The_table_holds_the_four_services_in_start_order_with_their_names_accounts_and_files()
    {
        Assert.Equal(["dbdatakit", "dbdatagateway", "dbdatarunner", "dbdataworkspace"], SuiteServices.All.Select(s => s.Program));
        Assert.Equal(["DbDataKit", "DbDataGateway", "DbDataRunner", "DbDataWorkspace"], SuiteServices.All.Select(s => s.WindowsName));
        Assert.Equal(["server", "gateway", "runner", "workspace"], SuiteServices.All.Select(s => s.DataFolder));
        Assert.Equal([1, 2, 3, 4], SuiteServices.All.Select(s => s.StartOrder));
        Assert.Equal(@"NT SERVICE\DbDataGateway", SuiteServices.Gateway.WindowsAccount);
        Assert.Equal("dbdatagateway", SuiteServices.Gateway.LinuxUser);
        Assert.Equal("startup-failure-dbdatagateway.txt", SuiteServices.Gateway.FailureFileName);
        Assert.All(SuiteServices.All, s => Assert.StartsWith("DbDataKit ", s.Description));
        Assert.Same(SuiteServices.Workspace, SuiteServices.Find("DbDataWorkspace"));
        Assert.Same(SuiteServices.Runner, SuiteServices.Find("dbdatarunner"));
        Assert.Null(SuiteServices.Find("dbdatasync"));
    }

    [Fact]
    public void The_machine_folders_are_the_installs()
    {
        if (OperatingSystem.IsWindows())
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DbDataKit");
            Assert.Equal(Path.Combine(root, "logs"), SuiteServices.MachineLogsRoot);
            Assert.Equal(Path.Combine(root, "gateway"), SuiteServices.MachineDataFolder(SuiteServices.Gateway));
        }
        else
        {
            Assert.Equal("/var/log/dbdatakit", SuiteServices.MachineLogsRoot);
            Assert.Equal("/var/lib/dbdatakit/server", SuiteServices.MachineDataFolder(SuiteServices.Console));
        }
    }
}

public sealed class ExcludedPortRangesTests
{
    [Fact]
    public void Netsh_output_is_read_as_ranges()
    {
        const string output = """

            Protocol tcp Port Exclusion Ranges

            Start Port    End Port
            ----------    --------
                  5357        5357
                  7840        7939
                 50000       50059     *

            * - Administered port exclusions.

            """;

        var ranges = ExcludedPortRanges.Parse(output.Replace("\n", "\r\n", StringComparison.Ordinal));

        Assert.Equal([new PortRange(5357, 5357), new PortRange(7840, 7939), new PortRange(50000, 50059)], ranges);
        Assert.True(ranges[1].Contains(7841));
        Assert.Equal("5357, 7840-7939, 50000-50059", string.Join(", ", ranges));
    }

    [Fact]
    public void A_port_inside_an_excluded_range_is_named_with_it()
    {
        var error = StartupStepException.Ports(7841, holder: null, [new PortRange(7840, 7939)]);

        Assert.Equal(StartupStep.Ports, error.Step);
        Assert.Equal("Port 7841 cannot be bound: it is inside the excluded port range 7840-7939.", error.Message);
    }
}
