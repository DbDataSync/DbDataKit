using System.Xml.Linq;

namespace DbData.Connections.Libraries.Tests;

public sealed class TempRoot : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dbdata-lib-" + Guid.NewGuid().ToString("N"));
    public TempRoot() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

/// <summary>The hostile inputs of H1: MSBuild injection through the package id and version, and traversal through ids and paths.</summary>
public class HostileInputTests
{
    public static TheoryData<string> BadPackageIds => new()
    {
        "", " ", "Foo\"/><Target Name=\"x\"><Exec Command=\"calc\"/></Target><X Include=\"", "Foo\" Version=\"1\" /><Import Project=\"evil.props",
        "Foo Bar", "Foo\nBar", "Foo;Bar", "Foo$(Evil)", "$(MSBuildProjectDirectory)", "%(Identity)", "..\\..\\evil", "../evil", "a/b", "C:\\evil",
        "Foo..Bar", ".Foo", "Foo.", "-Foo", "Foo<Bar>", "Foo&amp;", "Foo'Bar", "Fo\u043e", "Foo\0", new string('a', 101), "Foo,Bar", "Foo*", "Foo|Bar",
    };

    [Theory]
    [MemberData(nameof(BadPackageIds))]
    public void Hostile_package_ids_are_refused(string id) => Assert.Throws<LibraryValidationException>(() => LibraryValidation.ValidatePackageId(id));

    [Theory]
    [InlineData("MySqlConnector")]
    [InlineData("Microsoft.Data.SqlClient")]
    [InlineData("Npgsql")]
    [InlineData("DuckDB.NET.Data.Full")]
    [InlineData("System.Data.Odbc")]
    [InlineData("Oracle.ManagedDataAccess.Core")]
    [InlineData("IBM.Data.DB2.Core-lnx")]
    [InlineData("Some_Pkg-2.Thing")]
    public void Real_package_ids_pass(string id) => LibraryValidation.ValidatePackageId(id);

    public static TheoryData<string> BadVersions => new()
    {
        "", " ", "1", "1.0", "01.0.0", "1.0.0.", "1.0.0\"/><Target Name=\"x\"/>", "1.0.0\" /><Import Project=\"x", "$(Foo)", "$(Version)", "*", "1.*", "1.0.*", "[1.0,2.0)",
        "(1.0,)", "[1.0.0]", ">=1.0.0", "1.0.0;2.0.0", "1.0.0 ", " 1.0.0", "1.0.0\n", "v1.0.0", "latest", "1.0.0-", "1.0.0-beta..1", "1.0.0+", "1.0.0-\u0431", "1.0.0%(x)",
        "..\\1.0.0", "1.0.0/../x", new string('1', 70) + ".0.0",
    };

    [Theory]
    [MemberData(nameof(BadVersions))]
    public void Hostile_versions_are_refused(string v) => Assert.Throws<LibraryValidationException>(() => LibraryValidation.ValidateVersion(v));

    [Theory]
    [InlineData("1.0.0")]
    [InlineData("7.1.1")]
    [InlineData("10.0.3")]
    [InlineData("1.5.6")]
    [InlineData("1.0.0-beta.1")]
    [InlineData("1.0.0-rc.2+build.5")]
    [InlineData("1.2.3.4")]
    [InlineData("0.0.0")]
    public void Real_versions_pass(string v) => LibraryValidation.ValidateVersion(v);

    public static TheoryData<string> BadNames => new()
    {
        "", "..", ".", "../x", "..\\x", "a/b", "a\\b", "/abs", "C:\\x", "C:x", "UPPER", "con", "nul", "com1", "aux.txt", "a..b", ".hidden", "trailing.", "-lead", "a b", "a\0b",
        "a\nb", new string('a', 65), "caf\u00e9", "a:stream", "x*", "x?",
    };

    [Theory]
    [MemberData(nameof(BadNames))]
    public void Hostile_library_ids_and_file_names_are_refused(string name) => Assert.Throws<LibraryValidationException>(() => LibraryValidation.ValidateName(name));

    [Theory]
    [InlineData("mysql-connector")]
    [InlineData("system-data-odbc")]
    [InlineData("oracle")]
    [InlineData("a")]
    [InlineData("driver.v2_x")]
    public void Real_names_pass(string name) => LibraryValidation.ValidateName(name);

    [Theory]
    [InlineData("MySqlConnector.MySqlConnectorFactory, MySqlConnector")]
    [InlineData("Microsoft.Data.SqlClient.SqlClientFactory, Microsoft.Data.SqlClient")]
    [InlineData("Ns.Outer+Inner,Asm")]
    [InlineData("Ns.Type")]
    public void Factory_types_pass(string t) => LibraryValidation.ValidateFactoryType(t);

    [Theory]
    [InlineData("")]
    [InlineData("A.B, C; Evil")]
    [InlineData("A.B\nC")]
    [InlineData("A.B, C, Version=1.0.0.0")]
    [InlineData("../A")]
    [InlineData("A<B>")]
    public void Odd_factory_types_are_refused(string t) => Assert.Throws<LibraryValidationException>(() => LibraryValidation.ValidateFactoryType(t));

    [Fact]
    public void Error_messages_do_not_echo_control_characters()
    {
        var ex = Assert.Throws<LibraryValidationException>(() => LibraryValidation.ValidatePackageId("evil\r\nINJECT"));
        Assert.DoesNotContain('\n', ex.Message);
        Assert.DoesNotContain('\r', ex.Message);
    }

    // ---- paths ------------------------------------------------------------------------------------

    [Theory]
    [InlineData("..")]
    [InlineData("../..")]
    [InlineData("..\\..\\Windows")]
    [InlineData("a/../../b")]
    [InlineData("/etc")]
    [InlineData("C:\\Windows")]
    [InlineData("")]
    [InlineData("a/b")]
    public void Library_directories_cannot_leave_libraries(string id)
    {
        using var root = new TempRoot();
        Assert.Throws<LibraryValidationException>(() => LibraryPaths.LibraryDir(root.Path, id));
        Assert.Throws<LibraryValidationException>(() => LibraryPaths.DeleteLibraryDir(root.Path, id));
    }

    [Fact]
    public void Delete_only_removes_the_named_library()
    {
        using var root = new TempRoot();
        string keep = Path.Combine(root.Path, "libraries", "keep");
        string drop = Path.Combine(root.Path, "libraries", "drop");
        Directory.CreateDirectory(keep);
        Directory.CreateDirectory(Path.Combine(drop, "lib"));
        File.WriteAllText(Path.Combine(drop, "lib", "x.dll"), "x");
        File.WriteAllText(Path.Combine(root.Path, "precious.txt"), "x");
        LibraryPaths.DeleteLibraryDir(root.Path, "drop");
        Assert.False(Directory.Exists(drop));
        Assert.True(Directory.Exists(keep));
        Assert.True(File.Exists(Path.Combine(root.Path, "precious.txt")));
    }

    [Fact]
    public void AssertInside_rejects_the_root_itself_siblings_with_the_same_prefix_and_parents()
    {
        using var root = new TempRoot();
        string libs = Path.Combine(root.Path, "libraries");
        Directory.CreateDirectory(libs);
        LibraryPaths.AssertInside(libs, Path.Combine(libs, "a"));
        Assert.Throws<LibraryValidationException>(() => LibraryPaths.AssertInside(libs, libs));
        Assert.Throws<LibraryValidationException>(() => LibraryPaths.AssertInside(libs, libs + "-evil"));
        Assert.Throws<LibraryValidationException>(() => LibraryPaths.AssertInside(libs, Path.Combine(libs, "..", "x")));
        Assert.Throws<LibraryValidationException>(() => LibraryPaths.AssertInside(libs, root.Path));
    }

    [Fact]
    public void A_link_in_the_libraries_directory_is_removed_not_followed()
    {
        using var root = new TempRoot();
        string outside = Path.Combine(root.Path, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        Directory.CreateDirectory(Path.Combine(root.Path, "libraries"));
        string link = Path.Combine(root.Path, "libraries", "linked");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; } // no symlink right on this machine
        LibraryPaths.DeleteLibraryDir(root.Path, "linked");
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    // ---- sources ----------------------------------------------------------------------------------

    [Theory]
    [InlineData("--configfile")]
    [InlineData("-s")]
    [InlineData("http://api.nuget.org/v3/index.json")]
    [InlineData("https://user:pass@api.nuget.org/v3/index.json")]
    [InlineData("https://evil.example/v3/index.json")]
    [InlineData("\\\\server\\share\\feed")]
    [InlineData("C:\\feed")]
    [InlineData("file:///etc/feed")]
    [InlineData("/var/feed")]
    [InlineData("https://api.nuget.org/v3/index.json\nhttps://evil")]
    [InlineData(" https://api.nuget.org/v3/index.json")]
    [InlineData("https://api.nuget.org.evil.example/v3/index.json")]
    [InlineData("")]
    public void Sources_outside_the_configured_feeds_are_refused(string source) =>
        Assert.Throws<LibraryValidationException>(() => NuGetSourcePolicy.NuGetOrgOnly.Resolve(source));

    [Fact]
    public void Configured_feeds_are_allowed_case_insensitively_and_the_default_is_the_first()
    {
        var policy = new NuGetSourcePolicy(["https://api.nuget.org/v3/index.json", "https://feed.corp.example/nuget/v3/index.json"]);
        Assert.Equal("https://api.nuget.org/v3/index.json", policy.Resolve(null));
        Assert.Equal("https://feed.corp.example/nuget/v3/index.json", policy.Resolve("HTTPS://FEED.CORP.EXAMPLE/nuget/v3/index.json/"));
        Assert.Throws<ArgumentException>(() => new NuGetSourcePolicy(["http://not-https"]));
    }

    // ---- project file -----------------------------------------------------------------------------

    [Fact]
    public void Project_is_built_with_xelement_and_escapes_attribute_values()
    {
        // validation is bypassed on purpose: the builder alone must not let a value add elements
        const string id = "Foo\"/><Target Name=\"x\"><Exec Command=\"calc\"/></Target><X Include=\"";
        const string version = "1.0.0\" /><Import Project=\"evil.props";
        var xml = ProjectFile.Build([new PackageRef(id, version), new PackageRef("Npgsql", "10.0.3")], "win-x64");
        var doc = XDocument.Parse(xml);
        var refs = doc.Descendants("PackageReference").ToList();
        Assert.Equal(2, refs.Count);
        Assert.Equal(id, (string?)refs[0].Attribute("Include"));
        Assert.Equal(version, (string?)refs[0].Attribute("Version"));
        Assert.Empty(doc.Descendants("Target"));
        Assert.Empty(doc.Descendants("Exec"));
        Assert.Empty(doc.Descendants("Import"));
        Assert.Equal("Microsoft.NET.Sdk", (string?)doc.Root!.Attribute("Sdk"));
    }

    [Fact]
    public void Project_pins_the_runtime_for_native_assets_and_has_no_feed()
    {
        var doc = XDocument.Parse(ProjectFile.Build([new PackageRef("Npgsql", "10.0.3")], "linux-x64"));
        Assert.Equal("linux-x64", doc.Descendants("RuntimeIdentifier").Single().Value);
        Assert.Equal("false", doc.Descendants("SelfContained").Single().Value);
        Assert.DoesNotContain("source", doc.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}

public class PlanTests
{
    private static readonly PackageRef Npgsql = new("Npgsql", "10.0.3");

    [Fact]
    public void A_good_request_plans_without_touching_the_disk()
    {
        using var root = new TempRoot();
        var plan = LibraryInstallPlanner.Plan(root.Path, "npgsql", [Npgsql], "Npgsql.NpgsqlFactory, Npgsql", runtimeIdentifier: "win-x64");
        Assert.Equal(Path.Combine(root.Path, "libraries", "npgsql"), plan.LibraryDir);
        Assert.Equal("https://api.nuget.org/v3/index.json", plan.Source);
        Assert.False(Directory.Exists(Path.Combine(root.Path, "libraries")));
        var args = plan.PublishArguments("p.csproj", "out");
        Assert.Equal(["publish", "p.csproj", "-c", "Release", "-o", "out", "--nologo", "--source", plan.Source], args);
    }

    [Fact]
    public void Every_bad_field_fails_the_plan()
    {
        using var root = new TempRoot();
        string Ft = "A.B, C";
        Assert.Throws<LibraryValidationException>(() => LibraryInstallPlanner.Plan(root.Path, "..", [Npgsql], Ft));
        Assert.Throws<LibraryValidationException>(() => LibraryInstallPlanner.Plan(root.Path, "ok", [new PackageRef("a\"b", "1.0.0")], Ft));
        Assert.Throws<LibraryValidationException>(() => LibraryInstallPlanner.Plan(root.Path, "ok", [new PackageRef("Npgsql", "$(Evil)")], Ft));
        Assert.Throws<LibraryValidationException>(() => LibraryInstallPlanner.Plan(root.Path, "ok", [], Ft));
        Assert.Throws<LibraryValidationException>(() => LibraryInstallPlanner.Plan(root.Path, "ok", [Npgsql], "A;B"));
        Assert.Throws<LibraryValidationException>(() => LibraryInstallPlanner.Plan(root.Path, "ok", [Npgsql], Ft, source: "https://evil.example/index.json"));
        Assert.Throws<LibraryValidationException>(() => LibraryInstallPlanner.Plan(root.Path, "ok", [Npgsql], Ft, runtimeIdentifier: "win-x64;evil"));
    }

    [Fact]
    public void Manifest_round_trips_and_a_planted_manifest_is_validated()
    {
        var m = new LibraryManifest("npgsql", "Npgsql.NpgsqlFactory, Npgsql", [Npgsql]);
        var back = LibraryManifest.Parse(m.ToJson());
        Assert.Equal("npgsql", back.Id);
        Assert.Equal("10.0.3", back.Packages[0].Version);
        string planted = new LibraryManifest("../evil", "A.B, C", [Npgsql]).ToJson();
        Assert.Throws<LibraryValidationException>(() => LibraryManifest.Parse(planted));
        string plantedVersion = new LibraryManifest("ok", "A.B, C", [new PackageRef("Npgsql", "1.0.0\"/>")]).ToJson();
        Assert.Throws<LibraryValidationException>(() => LibraryManifest.Parse(plantedVersion));
    }

    private sealed class FakeExecutor(Action<string>? fill = null) : ILibraryRestoreExecutor
    {
        public LibraryInstallPlan? Seen { get; private set; }
        public Task RestoreAsync(LibraryInstallPlan plan, string projectDir, string outputDir, CancellationToken ct)
        {
            Seen = plan;
            Directory.CreateDirectory(Path.Combine(outputDir, "runtimes", "win-x64", "native"));
            File.WriteAllText(Path.Combine(outputDir, "Npgsql.dll"), "managed");
            File.WriteAllText(Path.Combine(outputDir, "runtimes", "win-x64", "native", "n.dll"), "native");
            fill?.Invoke(outputDir);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Install_replaces_lib_writes_the_manifest_and_cleans_up()
    {
        using var root = new TempRoot();
        var plan = LibraryInstallPlanner.Plan(root.Path, "npgsql", [Npgsql], "Npgsql.NpgsqlFactory, Npgsql", runtimeIdentifier: "win-x64");
        Directory.CreateDirectory(plan.LibDir);
        File.WriteAllText(Path.Combine(plan.LibDir, "stale.dll"), "old");

        var manifest = await LibraryInstaller.InstallAsync(plan, new FakeExecutor());

        Assert.False(File.Exists(Path.Combine(plan.LibDir, "stale.dll")));
        Assert.True(File.Exists(Path.Combine(plan.LibDir, "Npgsql.dll")));
        Assert.True(File.Exists(Path.Combine(plan.LibDir, "runtimes", "win-x64", "native", "n.dll")));
        Assert.Equal("npgsql", LibraryManifest.Parse(File.ReadAllText(plan.ManifestPath)).Id);
        Assert.Equal("npgsql", manifest.Id);
    }

    [Fact]
    public async Task A_failing_restore_leaves_the_existing_library_alone()
    {
        using var root = new TempRoot();
        var plan = LibraryInstallPlanner.Plan(root.Path, "npgsql", [Npgsql], "A.B, C", runtimeIdentifier: "win-x64");
        Directory.CreateDirectory(plan.LibDir);
        File.WriteAllText(Path.Combine(plan.LibDir, "good.dll"), "old");
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await LibraryInstaller.InstallAsync(plan, new Throwing()));
        Assert.True(File.Exists(Path.Combine(plan.LibDir, "good.dll")));
    }

    private sealed class Throwing : ILibraryRestoreExecutor
    {
        public Task RestoreAsync(LibraryInstallPlan plan, string projectDir, string outputDir, CancellationToken ct) => throw new InvalidOperationException("restore failed");
    }

    [Fact]
    public void Executor_arguments_are_a_list_not_a_shell_string()
    {
        using var root = new TempRoot();
        var plan = LibraryInstallPlanner.Plan(root.Path, "npgsql", [Npgsql], "A.B, C");
        var args = plan.PublishArguments(@"C:\tmp\a b\library.csproj", @"C:\tmp\a b\out");
        Assert.Contains(@"C:\tmp\a b\library.csproj", args);
        Assert.Equal(1, args.Count(a => a == "--source"));
        Assert.Equal(plan.Source, args[args.ToList().IndexOf("--source") + 1]);
    }
}
