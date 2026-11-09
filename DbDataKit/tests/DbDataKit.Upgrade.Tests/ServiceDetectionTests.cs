namespace DbDataKit.Upgrade.Tests;

/// <summary>UC09: a program started by hand under a unit (a CI runner agent) inherits <c>INVOCATION_ID</c> and is still "not a service"; the unit's own main process is one.</summary>
public sealed class ServiceDetectionTests
{
    private const string Unit = "0::/system.slice/dbdatabuild.service";

    [Fact]
    public void A_process_that_only_inherited_the_invocation_id_is_not_a_service_whatever_its_cgroup_says()
    {
        // a child of a shell inside a unit: the id and the unit's control group are inherited, systemd's exec pid is the agent's, not ours
        Assert.False(ServiceDetection.IsSystemdMainProcess("abc", "100", processId: 4321, parentId: 100, Unit));
        // an older systemd names no exec pid: the parent decides, and it is the shell, not systemd
        Assert.False(ServiceDetection.IsSystemdMainProcess("abc", null, processId: 4321, parentId: 100, Unit));
        Assert.False(ServiceDetection.IsSystemdMainProcess(null, null, processId: 4321, parentId: 1, Unit));
    }

    [Fact]
    public void The_main_process_of_a_unit_is_a_service()
    {
        Assert.True(ServiceDetection.IsSystemdMainProcess("abc", "4321", processId: 4321, parentId: 1, Unit));
        Assert.True(ServiceDetection.IsSystemdMainProcess("abc", null, processId: 4321, parentId: 1, Unit));
        Assert.False(ServiceDetection.IsSystemdMainProcess("abc", null, processId: 4321, parentId: 1, "0::/user.slice/session-1.scope"));
    }

    [Fact]
    public void The_parent_is_read_after_a_command_name_that_holds_spaces_and_parentheses()
    {
        Assert.Equal(77, ServiceDetection.ParentOnLinux("12 (my (odd) name) S 77 12 12 0 -1"));
    }

    [Fact]
    public void This_test_process_with_an_inherited_invocation_id_is_not_a_service()
    {
        var before = Environment.GetEnvironmentVariable("INVOCATION_ID");
        var pid = Environment.GetEnvironmentVariable("SYSTEMD_EXEC_PID");
        try
        {
            Environment.SetEnvironmentVariable("INVOCATION_ID", "inherited");
            Environment.SetEnvironmentVariable("SYSTEMD_EXEC_PID", "1");
            Assert.False(ServiceDetection.IsRunningAsService());
        }
        finally { Environment.SetEnvironmentVariable("INVOCATION_ID", before); Environment.SetEnvironmentVariable("SYSTEMD_EXEC_PID", pid); }
    }
}
