using DbDataKit.Runner;

namespace DbDataKit.Runner.Abstractions.Tests;

/// <summary>Plan 28c E04: <c>launch.runtime</c> and <c>runtime: true</c>, the newer keys of <c>package/1</c>, and the range grammar.</summary>
public sealed class RuntimeManifestTests
{
    private const string File = "p/package.yaml";

    private const string Runtime = """
        schema: package/1
        name: dbdatabuild-runtime
        runtime: true
        version: 2.1.0
        platform: linux-x64
        launch: { kind: executable, command: bin/dbdatabuild, args: [--runner] }
        built_against: { protocol: dbdatakit.runner.v1 }
        """;

    private const string Definition = """
        schema: package/1
        name: orders-project
        launch: { runtime: { name: dbdatabuild-runtime, range: "2.x" } }
        built_against: { protocol: dbdatakit.runner.v1 }
        tasks:
          - name: refresh
        """;

    private static string Errors(string yaml) => string.Join("\n", PackageManifestReader.Load(yaml, File).Diagnostics);

    [Fact]
    public void A_runtime_package_has_no_tasks_a_version_and_an_executable_launch_and_round_trips()
    {
        var load = PackageManifestReader.Load(Runtime, File);

        Assert.True(load.Ok, Errors(Runtime));
        var manifest = load.Manifest!;
        Assert.True(manifest.IsRuntime);
        Assert.Equal("2.1.0", manifest.Version);
        Assert.Empty(manifest.Tasks);
        Assert.Equal(["runtime", "platform"], manifest.NewerKeys);
        Assert.Equal("linux-x64", manifest.Platform);
        Assert.Equal("dbdatabuild-runtime-linux-x64", manifest.PackageName);           // the registry name: one package per platform
        Assert.Equal("dbdatabuild-runtime", manifest.Name);
        Assert.Equal("linux-x64", PackageManifestReader.Load(PackageManifestWriter.Write(manifest), File).Manifest!.Platform);
        Assert.Contains("\nplatform: linux-x64\n", PackageManifestWriter.Write(manifest), StringComparison.Ordinal);
        Assert.Equal(manifest.Version, PackageManifestReader.Load(PackageManifestWriter.Write(manifest), File).Manifest!.Version);
        Assert.Equal(PackageManifestWriter.Write(manifest), PackageManifestWriter.Write(PackageManifestReader.Load(PackageManifestWriter.Write(manifest), File).Manifest!));
    }

    [Fact]
    public void A_definition_names_its_runtime_and_range_and_round_trips()
    {
        var load = PackageManifestReader.Load(Definition, File);

        Assert.True(load.Ok, Errors(Definition));
        var manifest = load.Manifest!;
        Assert.Equal(new RuntimeReference("dbdatabuild-runtime", "2.x"), manifest.Launch.Runtime);
        Assert.Equal(LaunchKind.Executable, manifest.Launch.Kind);
        Assert.Equal(["launch.runtime"], manifest.NewerKeys);
        var text = PackageManifestWriter.Write(manifest);
        Assert.Contains("runtime: { name: dbdatabuild-runtime, range: \"2.x\" }", text, StringComparison.Ordinal);
        Assert.Equal(manifest.Launch, PackageManifestReader.Load(text, File).Manifest!.Launch);
    }

    [Fact]
    public void Two_platforms_of_one_runtime_are_two_packages_with_the_same_name_and_version()
    {
        var linux = PackageManifestReader.Load(Runtime, File).Manifest!;
        var windows = PackageManifestReader.Load(Runtime.Replace("linux-x64", "win-x64", StringComparison.Ordinal), File).Manifest!;

        Assert.Equal(linux.Name, windows.Name);
        Assert.Equal(linux.Version, windows.Version);
        Assert.NotEqual(linux.PackageName, windows.PackageName);
        Assert.Equal("dbdatabuild-runtime-win-x64", windows.PackageName);
        Assert.Equal(RuntimePlatforms.PackageName("dbdatabuild-runtime", "win-x64"), windows.PackageName);
    }

    [Fact]
    public void A_runtime_whose_name_and_platform_are_too_long_for_a_package_name_is_refused()
    {
        var yaml = Runtime.Replace("name: dbdatabuild-runtime", "name: " + new string('a', 60), StringComparison.Ordinal);

        Assert.Contains("too long for a package name", Errors(yaml));
    }

    [Fact]
    public void The_current_platform_is_the_operating_system_and_the_architecture()
    {
        Assert.Matches("^(win|linux|osx)-[a-z0-9]+$", RuntimePlatforms.Current);
        Assert.True(RuntimePlatforms.IsSupported("win-x64") && RuntimePlatforms.IsSupported("linux-x64") && !RuntimePlatforms.IsSupported("osx-arm64"));
    }

    [Fact]
    public void A_package_without_the_newer_keys_has_none()
    {
        var manifest = new PackageManifest("p", null, new PackageLaunch(LaunchKind.Assembly, "a.dll"), new BuiltAgainst("10.0", null, "dbdatakit.runner.v1"), [new PackageTask("t", new TaskEntry("T"))]);

        Assert.Empty(manifest.NewerKeys);
    }

    [Theory]
    [InlineData("runtime: true\nversion: 2.1.0\nplatform: linux-x64\nlaunch: { kind: executable, command: bin/x }\ntasks:\n  - name: t", "no tasks of its own")]
    [InlineData("runtime: true\nversion: 2.1.0\nlaunch: { kind: executable, command: bin/x }", "says the platform its files are built for")]
    [InlineData("runtime: true\nversion: 2.1.0\nplatform: osx-arm64\nlaunch: { kind: executable, command: bin/x }", "is not a platform runtime packages are published for: win-x64, linux-x64")]
    [InlineData("version: 2.1.0\nplatform: linux-x64\nlaunch: { kind: executable, command: bin/x }\ntasks:\n  - name: t", "Only a runtime package")]
    [InlineData("platform: linux-x64\nlaunch: { kind: executable, command: bin/x }\ntasks:\n  - name: t", "Only a runtime package (`runtime: true`) has a `platform`")]
    [InlineData("runtime: true\nlaunch: { kind: executable, command: bin/x }", "says its version")]
    [InlineData("runtime: true\nversion: two\nlaunch: { kind: executable, command: bin/x }", "is not a version")]
    [InlineData("runtime: true\nversion: 2.1.0\nlaunch: { kind: assembly, assembly: bin/x.dll }", "starts as an executable")]
    [InlineData("version: 2.1.0\nlaunch: { kind: executable, command: bin/x }\ntasks:\n  - name: t", "Only a runtime package")]
    [InlineData("launch: { runtime: { name: engine, range: \"two\" } }\ntasks:\n  - name: t", "not a version comparator")]
    [InlineData("launch: { runtime: { name: Engine!, range: \"2.x\" } }\ntasks:\n  - name: t", "not a name for a runtime package")]
    [InlineData("launch: { kind: executable, command: bin/x, runtime: { name: engine, range: \"2.x\" } }\ntasks:\n  - name: t", "no command of its own")]
    [InlineData("launch: { runtime: { name: engine } }\ntasks:\n  - name: t", "`launch.runtime.range`")]
    [InlineData("launch: { runtime: { name: engine, range: \"2.x\" } }", "at least one task")]
    public void A_runtime_or_a_definition_that_breaks_the_rules_is_refused_naming_the_key(string body, string expected)
    {
        var yaml = $"schema: package/1\nname: p\nbuilt_against: {{ protocol: dbdatakit.runner.v1 }}\n{body}\n";

        var result = PackageManifestReader.Load(yaml, File);

        Assert.False(result.Ok);
        Assert.Contains(expected, string.Join("\n", result.Diagnostics));
    }

    [Theory]
    [InlineData("2.x", "2.0.0", true)]
    [InlineData("2.x", "2.9.9", true)]
    [InlineData("2.x", "3.0.0", false)]
    [InlineData("2.x", "1.9.0", false)]
    [InlineData("2", "2.4.1", true)]
    [InlineData("2.1.x", "2.1.7", true)]
    [InlineData("2.1.x", "2.2.0", false)]
    [InlineData("2.1.4", "2.1.4", true)]
    [InlineData("2.1.4", "2.1.5", false)]
    [InlineData(">=2.1 <3", "2.1.0", true)]
    [InlineData(">=2.1 <3", "2.0.9", false)]
    [InlineData(">=2.1 <3", "3.0.0", false)]
    [InlineData(">2.1.0", "2.1.0", false)]
    [InlineData("<=2.1.0", "2.1.0", true)]
    [InlineData("*", "9.9.9", true)]
    [InlineData("2.x", "10.0.0", false)]
    [InlineData("2.x", "not-a-version", false)]
    public void A_range_holds_the_versions_it_names(string range, string version, bool expected)
    {
        Assert.True(VersionRange.TryParse(range, out var parsed, out var error), error);

        Assert.Equal(expected, parsed.Contains(version));
    }

    [Theory]
    [InlineData("")]
    [InlineData("2.x.x")]
    [InlineData(">=2.x")]
    [InlineData("two")]
    [InlineData("1.2.3.4")]
    [InlineData(">=")]
    public void A_range_that_is_not_one_is_refused_with_the_comparator_named(string range)
    {
        Assert.False(VersionRange.TryParse(range, out _, out var error));
        Assert.NotEmpty(error);
    }
}
