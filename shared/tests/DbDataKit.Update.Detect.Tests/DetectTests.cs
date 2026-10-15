namespace DbDataKit.Update.Detect.Tests;

public class DetectorTests
{
    private static InstallEnvironment E(string proc, string? bas = null, Dictionary<string, string?>? env = null, bool dockerEnv = false)
        => new(proc, bas ?? Path.GetDirectoryName(proc) ?? "", env ?? new Dictionary<string, string?>(), dockerEnv);

    [Theory]
    [InlineData(@"C:\Users\me\scoop\apps\dbdatabuild\1.2.0\dbdatabuild.exe")]
    [InlineData(@"C:\Users\me\scoop\shims\dbdatabuild.exe")]
    [InlineData(@"C:\ProgramData\scoop\apps\dbdatabuild\current\dbdatabuild.exe")]
    [InlineData("/home/me/scoop/apps/x/1.0/x")]
    public void Scoop(string path) => Assert.Equal(InstallMethod.Scoop, InstallMethodDetector.Detect(E(path)).Method);

    [Fact]
    public void Scoop_CustomRootViaEnv()
    {
        var env = new Dictionary<string, string?> { ["SCOOP"] = @"D:\tools\pkgs" };
        Assert.Equal(InstallMethod.Scoop, InstallMethodDetector.Detect(E(@"D:\tools\pkgs\apps\x\1.0\x.exe", env: env)).Method);
        Assert.NotEqual(InstallMethod.Scoop, InstallMethodDetector.Detect(E(@"D:\tools\pkgs-other\apps\x\x.exe", env: env)).Method);
    }

    [Theory]
    [InlineData(@"C:\Users\me\.dotnet\tools\dbdatasync.exe", @"C:\Users\me\.dotnet\tools\.store\dbdatasync\1.0.0\dbdatasync\1.0.0\tools\net10.0\any\")]
    [InlineData("/home/me/.dotnet/tools/dbdatasync", "/home/me/.dotnet/tools/.store/dbdatasync/1.0.0/dbdatasync/1.0.0/tools/net10.0/any/")]
    public void DotnetTool(string proc, string bas) => Assert.Equal(InstallMethod.DotnetTool, InstallMethodDetector.Detect(E(proc, bas)).Method);

    [Fact]
    public void DotnetTool_MachineWideToolPath()
    {
        var env = new Dictionary<string, string?> { ["DOTNET_TOOLS_PATH"] = @"C:\Tools\dotnet" };
        Assert.Equal(InstallMethod.DotnetTool, InstallMethodDetector.Detect(E(@"C:\Tools\dotnet\dbdatasync.exe", env: env)).Method);
    }

    [Fact]
    public void DotnetStoreDetectedFromBaseDirectoryAlone()
        => Assert.Equal(InstallMethod.DotnetTool, InstallMethodDetector.Detect(E("/usr/bin/dotnet", "/opt/t/.store/x/1.0.0/x/1.0.0/tools/net10.0/any/")).Method);

    [Theory]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Packages\Foo_x\foo.exe")]
    [InlineData(@"C:\Users\me\AppData\Local\Microsoft\WinGet\Links\foo.exe")]
    public void Winget(string p) => Assert.Equal(InstallMethod.Winget, InstallMethodDetector.Detect(E(p)).Method);

    [Theory]
    [InlineData("DOTNET_RUNNING_IN_CONTAINER", "true")]
    [InlineData("KUBERNETES_SERVICE_HOST", "10.0.0.1")]
    public void Container_ByEnv(string k, string v)
        => Assert.Equal(InstallMethod.Container, InstallMethodDetector.Detect(E("/app/dbdataproxy", env: new() { [k] = v })).Method);

    [Fact] public void Container_ByDockerEnvFile() => Assert.Equal(InstallMethod.Container, InstallMethodDetector.Detect(E("/app/x", dockerEnv: true)).Method);

    [Fact]
    public void Container_WinsOverScoopPath()
        => Assert.Equal(InstallMethod.Container, InstallMethodDetector.Detect(E(@"C:\scoop\apps\x\x.exe", env: new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "true" })).Method);

    [Fact]
    public void DotnetRunningInContainerFalse_IsNotContainer()
        => Assert.Equal(InstallMethod.Archive, InstallMethodDetector.Detect(E(@"C:\apps\x\x.exe", env: new() { ["DOTNET_RUNNING_IN_CONTAINER"] = "false" })).Method);

    [Theory]
    [InlineData(@"C:\Program Files\DbDataProxy\versions\1.4.0\dbdataproxy.exe")]
    [InlineData("/opt/dbdataproxy/versions/1.4.0-beta.1/dbdataproxy")]
    public void ServiceSwap(string p) => Assert.Equal(InstallMethod.ServiceSwap, InstallMethodDetector.Detect(E(p)).Method);

    [Theory]
    [InlineData(@"C:\Tools\dbdatabuild\dbdatabuild.exe")]
    [InlineData("/usr/local/bin/dbdatabuild")]
    public void Archive_Default(string p) => Assert.Equal(InstallMethod.Archive, InstallMethodDetector.Detect(E(p)).Method);

    [Fact] public void Unknown_WhenNoInfo() => Assert.Equal(InstallMethod.Unknown, InstallMethodDetector.Detect(E("", "")).Method);

    [Fact]
    public void LookalikeDirectoryIsNotScoop()
        => Assert.NotEqual(InstallMethod.Scoop, InstallMethodDetector.Detect(E(@"C:\notscoopy\apps\x.exe")).Method);
}

public class ApplierTests
{
    private static UpdateApplyRequest R(string id = "dbdatabuild", string target = "1.5.0", string? url = null, string? image = null, bool global = true)
        => new("DbDataBuild", id, ReleaseVersion.Parse("1.4.0"), ReleaseVersion.Parse(target), Channel.Stable, url, image, global);

    [Fact]
    public void Scoop_PrintsCommand()
    {
        var r = new PackageManagerHint(InstallMethod.Scoop).Plan(R());
        Assert.Equal(ApplyOutcome.Instructions, r.Outcome);
        Assert.Equal("scoop update dbdatabuild", r.Command!.ToString());
    }

    [Fact] public void Winget_PrintsCommand() => Assert.Equal("winget upgrade --id dbdatabuild --exact", new PackageManagerHint(InstallMethod.Winget).Plan(R()).Command!.ToString());

    [Fact]
    public void DotnetTool_BuildsArgumentListWithoutShell()
    {
        var r = new DotnetToolApplier().Plan(R("DbDataSync", "1.5.0-beta.2"));
        Assert.Equal(ApplyOutcome.CommandReady, r.Outcome);
        Assert.Equal("dotnet", r.Command!.FileName);
        Assert.Equal(["tool", "update", "-g", "DbDataSync", "--version", "1.5.0-beta.2"], r.Command.Arguments);
    }

    [Fact] public void DotnetTool_LocalOmitsGlobalFlag() => Assert.DoesNotContain("-g", new DotnetToolApplier().Plan(R(global: false)).Command!.Arguments);

    [Theory]
    [InlineData("x; calc")]
    [InlineData("x && del *")]
    [InlineData("$(evil)")]
    [InlineData("")]
    [InlineData("-g")]
    public void InjectionInToolIdRefused(string id)
    {
        Assert.Throws<ArgumentException>(() => new DotnetToolApplier().Plan(R(id)));
        Assert.Throws<ArgumentException>(() => new PackageManagerHint().Plan(R(id)));
    }

    [Fact]
    public void Manual_MentionsHttpsUrlOnly()
    {
        Assert.Contains("https://example.com/x.zip", new ManualApplier().Plan(R(url: "https://example.com/x.zip")).Message);
        Assert.DoesNotContain("javascript:", new ManualApplier().Plan(R(url: "javascript:alert(1)")).Message);
        Assert.Equal(ApplyOutcome.ManualRequired, new ManualApplier().Plan(R()).Outcome);
    }

    [Fact]
    public void Container_NeverOffersInPlace()
    {
        var r = new ContainerNotice().Plan(R(image: "ghcr.io/org/dbdataproxy:1.4.0"));
        Assert.Equal(ApplyOutcome.NotSupported, r.Outcome);
        Assert.Null(r.Command);
        Assert.Contains("ghcr.io/org/dbdataproxy:1.5.0", r.Message);
        Assert.Throws<ArgumentException>(() => new ContainerNotice().Plan(R(image: "x; rm -rf /")));
    }

    [Fact]
    public void Selector_FollowsDetection()
    {
        var env = new InstallEnvironment(@"C:\Users\me\scoop\shims\x.exe", @"C:\Users\me\scoop\shims\", new Dictionary<string, string?>());
        Assert.StartsWith("scoop update", UpdateApplierSelector.PlanFor(env, R()).Command!.ToString());
        Assert.IsType<ManualApplier>(UpdateApplierSelector.For(InstallMethod.Unknown));
        Assert.IsType<DotnetToolApplier>(UpdateApplierSelector.For(InstallMethod.DotnetTool));
        Assert.IsType<ServiceSwapNotice>(UpdateApplierSelector.For(InstallMethod.ServiceSwap));
    }
}
