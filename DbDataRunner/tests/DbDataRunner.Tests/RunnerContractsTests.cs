using DbDataKit.Runner;
using DbDataRunner.Server;

namespace DbDataRunner.Tests;

/// <summary>Plan 28c E04: the keys <c>launch.runtime</c> and <c>runtime</c> of <c>package/1</c> are in this runner's contract, and a runner without them refuses a manifest that carries them, naming the key.</summary>
public sealed class RunnerContractsTests
{
    private static readonly BuiltAgainst Built = new(null, null, RunnerProtocol.V1);

    private static PackageManifest Definition() =>
        new("orders", null, new PackageLaunch(LaunchKind.Executable, Runtime: new RuntimeReference("engine", "2.x")), Built, [new PackageTask("refresh", new TaskEntry())]);

    private static PackageManifest Runtime() =>
        new("engine", null, new PackageLaunch(LaunchKind.Executable, Command: "bin/engine"), Built, [], IsRuntime: true, Version: "2.0.0", Platform: "linux-x64");

    [Fact]
    public void This_runner_serves_a_definition_and_a_runtime()
    {
        Assert.Null(RunnerContracts.Check(Definition()));
        Assert.Null(RunnerContracts.Check(Runtime()));
    }

    [Fact]
    public void A_runner_on_the_old_format_refuses_each_key_naming_it_and_serves_a_manifest_without_them()
    {
        Assert.Contains("`launch.runtime`", RunnerContracts.CheckKeys(Definition(), []));
        Assert.Contains("`runtime`", RunnerContracts.CheckKeys(Runtime(), []));
        Assert.Contains("`platform`", RunnerContracts.CheckKeys(Runtime(), [PackageManifest.RuntimeKey]));
        var plain = new PackageManifest("p", null, new PackageLaunch(LaunchKind.Assembly, "a.dll"), new BuiltAgainst("10.0", null, RunnerProtocol.V1), [new PackageTask("t", new TaskEntry("T"))]);
        Assert.Null(RunnerContracts.CheckKeys(plain, []));
    }
}
