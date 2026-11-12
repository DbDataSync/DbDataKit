using DbDataKit.Runner;

namespace DbDataKit.Runner.Packaging.Tests;

/// <summary>IX20 / csx without the SDK: a csx project becomes a package with Roslyn and the NuGet client only; nothing here calls dotnet or MSBuild.</summary>
public sealed class CsxPackageBuilderTests
{
    [Fact]
    public async Task A_csx_project_with_a_package_builds_into_a_manifest_and_a_files_folder()
    {
        using var s = new Scratch();
        s.Standard();
        var output = Path.Combine(s.Root, "out");

        var result = await CsxPackageBuilder.BuildAsync(s.Project, output, s.Options());

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics));
        Assert.Equal("greeting", result.Package);
        Assert.Equal(["greet"], result.Tasks);
        Assert.True(File.Exists(Path.Combine(output, "package.yaml")));
        Assert.True(File.Exists(Path.Combine(output, "files", "bin", "greeting.dll")));
        Assert.True(File.Exists(Path.Combine(output, "files", "bin", "Greeter.dll")), "the package's run-time assembly travels beside the job");
        Assert.Equal(["bin/Greeter.dll", "bin/greeting.dll"], result.Files!.Order(StringComparer.Ordinal).ToArray());
        // the manifest the runner reads is the one that was written
        Assert.True(PackageManifestReader.LoadFile(Path.Combine(output, "package.yaml")).Ok);
        // the packages are pinned beside the project
        var pins = File.ReadAllText(Path.Combine(s.Project, "package.lock"));
        Assert.Contains("scripting-lock/1", pins);
        Assert.Contains("greeter/1.0.0", pins);
    }

    [Fact]
    public async Task The_same_project_builds_the_same_assembly_twice()
    {
        using var s = new Scratch();
        s.Standard();
        var first = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "a"), s.Options());
        var second = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "b"), s.Options());
        Assert.True(first.Succeeded && second.Succeeded);
        Assert.Equal(File.ReadAllBytes(first.AssemblyPath!), File.ReadAllBytes(second.AssemblyPath!));
    }

    [Fact]
    public async Task The_assembly_binds_to_the_reference_assemblies_when_the_machine_has_them_and_to_the_implementation_when_it_has_not()
    {
        using var s = new Scratch();
        s.Standard();
        var result = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "out"), s.Options());
        Assert.True(result.Succeeded);
        using var pe = new System.Reflection.PortableExecutable.PEReader(File.OpenRead(result.AssemblyPath!));
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
        var refs = metadata.AssemblyReferences.Select(r => metadata.GetString(metadata.GetAssemblyReference(r).Name)).ToList();
        if (CsxPackageBuilder.ReferencePack() is not null)
        {
            Assert.Contains("System.Runtime", refs);
            Assert.DoesNotContain("System.Private.CoreLib", refs);
            Assert.DoesNotContain(result.Diagnostics, d => d.Code == "DBD-INT-REFS");
        }
        else
        {
            Assert.Contains("System.Private.CoreLib", refs);
            Assert.Contains(result.Diagnostics, d => d.Code == "DBD-INT-REFS");
        }
    }

    [Fact]
    public async Task A_compile_error_names_the_file_and_the_line_of_the_source_not_of_a_generated_one()
    {
        using var s = new Scratch();
        s.Standard();
        s.Write("src/greet.csx", Scratch.Job.Replace("return Task.CompletedTask;", "return Task.Completed;"));           // line 15 of the file, after the #r line

        var result = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "out"), s.Options());

        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Diagnostics, d => d.IsError);
        Assert.Equal("src/greet.csx", error.File);
        Assert.Equal(Scratch.Job.Split('\n').ToList().FindIndex(l => l.Contains("Task.CompletedTask")) + 1, error.Line);
    }

    [Fact]
    public async Task A_task_in_the_manifest_with_no_job_class_and_a_job_class_the_manifest_does_not_list_are_both_refused()
    {
        using var s = new Scratch();
        s.Standard();
        s.Write("package.yaml", Scratch.Manifest.Replace("name: greet", "name: other"));

        var result = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "out"), s.Options());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "DBD-INT-TASK" && d.Message.Contains("'other'"));
        Assert.Contains(result.Diagnostics, d => d.Code == "DBD-INT-TASK" && d.Message.Contains("\"greet\""));
    }

    [Fact]
    public async Task A_launch_that_is_not_an_assembly_in_bin_is_refused_with_the_form_to_write()
    {
        using var s = new Scratch();
        s.Standard();
        s.Write("package.yaml", Scratch.Manifest.Replace("kind: assembly, assembly: bin/greeting.dll", "kind: executable, command: sh").Replace("entry: { type: Greeting.Greet }", "entry: {}"));
        var result = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "out"), s.Options());
        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "DBD-INT-LAUNCH");
    }

    [Fact]
    public async Task A_damaged_manifest_is_reported_by_the_runners_own_reader_with_its_file()
    {
        using var s = new Scratch();
        s.Standard();
        s.Write("package.yaml", "schema: package/1\nname: greeting\nlaunsh: {}\n");
        var result = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "out"), s.Options());
        Assert.False(result.Succeeded);
        Assert.All(result.Diagnostics.Where(d => d.IsError), d => Assert.Equal("package.yaml", d.File));
    }

    [Fact]
    public async Task A_package_that_changed_under_its_pin_is_refused_naming_the_lock_file()
    {
        using var s = new Scratch();
        s.Standard();
        Assert.True((await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "a"), s.Options())).Succeeded);
        // a feed that serves other bytes for the same id and version
        s.AddGreeter(body: "\"Goodbye, \" + name");
        Directory.Delete(Path.Combine(s.Cache, "packages"), recursive: true);

        var result = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "b"), s.Options());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Diagnostics, d => d.Code == "DBD-INT-RESTORE" && d.Message.Contains("package.lock"));
    }

    [Fact]
    public async Task A_range_instead_of_an_exact_version_and_two_versions_of_one_package_are_refused()
    {
        using var s = new Scratch();
        s.Standard();
        s.Write("src/greet.csx", Scratch.Job.Replace("Greeter, 1.0.0", "Greeter, 1.*"));
        var range = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "a"), s.Options());
        Assert.False(range.Succeeded);
        Assert.Contains(range.Diagnostics, d => d.Code == "DBD-INT-PACKAGE" && d.File == "src/greet.csx" && d.Line == 1);

        s.Write("src/greet.csx", Scratch.Job);
        s.Write("src/other.csx", "#r \"nuget: Greeter, 2.0.0\"\nnamespace Other { public static class X { } }\n");
        var two = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "b"), s.Options());
        Assert.False(two.Succeeded);
        Assert.Contains(two.Diagnostics, d => d.Code == "DBD-INT-PACKAGE" && d.Message.Contains("two versions"));
    }

    [Fact]
    public async Task Files_of_the_project_travel_in_the_package_and_cannot_replace_a_built_file()
    {
        using var s = new Scratch();
        s.Standard();
        s.Write("files/data/countries.csv", "code\nDE\n");
        var ok = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "a"), s.Options());
        Assert.True(ok.Succeeded);
        Assert.Contains("data/countries.csv", ok.Files!);
        Assert.Equal("code\nDE\n", File.ReadAllText(Path.Combine(s.Root, "a", "files", "data", "countries.csv")));

        s.Write("files/bin/greeting.dll", "not the build");
        var clash = await CsxPackageBuilder.BuildAsync(s.Project, Path.Combine(s.Root, "b"), s.Options());
        Assert.False(clash.Succeeded);
        Assert.Contains(clash.Diagnostics, d => d.Code == "DBD-INT-FILES");
    }

    [Fact]
    public void A_project_is_csx_or_compiled_by_what_it_holds()
    {
        using var s = new Scratch();
        Assert.Null(IntegrationProject.Detect(s.Project));
        s.Write("src/a.csx", "namespace A { }");
        Assert.Equal(IntegrationLanguage.Csx, IntegrationProject.Detect(s.Project));
        s.Write("src/Orders.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        Assert.Equal(IntegrationLanguage.CompiledProject, IntegrationProject.Detect(s.Project));
    }

    [Fact]
    public void A_machine_with_the_runtime_and_no_sdk_is_told_so_in_words_that_say_what_to_do()
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("PATH", Path.Combine(Path.GetTempPath(), "no-such-folder"));
            Environment.SetEnvironmentVariable("DOTNET_ROOT", Path.Combine(Path.GetTempPath(), "no-such-folder"));
            Assert.Null(DotnetSdk.Find());
            Assert.Contains("needs the .NET SDK", DotnetSdk.Missing);
            Assert.Contains("src/ (csx)", DotnetSdk.Missing);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
            Environment.SetEnvironmentVariable("DOTNET_ROOT", root);
        }
    }
}
