using System.IO.Compression;
using System.Text;
using DbDataKit.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace DbDataKit.Runner.Packaging.Tests;

/// <summary>A scratch folder holding an integration project, an offline feed with one real package (<c>Greeter 1.0.0</c>, compiled here) and the build cache. Nothing is downloaded.</summary>
public sealed class Scratch : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "dbdata-packaging-" + Guid.NewGuid().ToString("N")[..10]);

    public string Project => Path.Combine(Root, "project");

    public string Feed => Path.Combine(Root, "feed");

    public string Cache => Path.Combine(Root, "cache");

    public Scratch()
    {
        Directory.CreateDirectory(Project);
        Directory.CreateDirectory(Feed);
    }

    public const string Manifest = """
        schema: package/1
        name: greeting
        launch: { kind: assembly, assembly: bin/greeting.dll }
        built_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }
        tasks:
          - name: greet
            entry: { type: Greeting.Greet }
        """;

    public const string Job = """
        #r "nuget: Greeter, 1.0.0"

        namespace Greeting;

        public static class GreetCounters { public static readonly Counter Letters = new("letters"); }

        [RunnerTask("greet")]
        public sealed class Greet : RunnerJob<NoInput, NoProgress, NoSlots>
        {
            protected override Task RunAsync(CancellationToken ct)
            {
                Count(GreetCounters.Letters, global::Greeter.Greeting.Say("world").Length);
                return Task.CompletedTask;
            }
        }
        """;

    public const string PassingTest = """
        var sim = new RunSimulator<Greeting.Greet>();
        await sim.RunAsync();
        True(sim.Outcome == DbDataKit.Monitoring.RunOutcome.Succeeded, "the run did not succeed");
        Equal(12L, sim.Counted(Greeting.GreetCounters.Letters));
        sim.Dispose();
        """;

    public void Write(string relative, string text)
    {
        var path = Path.Combine(Project, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.Replace("\r\n", "\n"));
    }

    public void Standard(string? test = PassingTest)
    {
        Write("package.yaml", Manifest);
        Write("src/greet.csx", Job);
        if (test is not null) Write("tests/greet.csx", test);
        AddGreeter();
    }

    public PackageBuildOptions Options(bool writeLock = true) => new() { CacheDirectory = Cache, Sources = [new PackageSource(Feed)], Offline = false, WriteLock = writeLock };

    /// <summary>Compiles <c>Greeter</c> (a class with one method) and lays it out as a package of a folder feed.</summary>
    public void AddGreeter(string version = "1.0.0", string body = "\"Hello, \" + name")
    {
        var tree = CSharpSyntaxTree.ParseText($"namespace Greeter {{ public static class Greeting {{ public static string Say(string name) => {body}; }} }}");
        var refs = (CsxPackageBuilder.ReferencePack() ?? CsxPackageBuilder.FrameworkAssemblies()).Values.Select(p => (MetadataReference)MetadataReference.CreateFromFile(p));
        var compilation = CSharpCompilation.Create("Greeter", [tree], refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, deterministic: true));
        using var dll = new MemoryStream();
        var emit = compilation.Emit(dll);
        Assert.True(emit.Success, string.Join("; ", emit.Diagnostics));

        var dir = Path.Combine(Feed, "greeter", version);
        Directory.CreateDirectory(dir);
        var nupkg = Path.Combine(dir, $"greeter.{version}.nupkg");
        File.Delete(nupkg);
        var nuspec = $"<?xml version=\"1.0\"?><package xmlns=\"http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd\"><metadata><id>Greeter</id><version>{version}</version><authors>t</authors><description>t</description></metadata></package>";
        using (var zip = ZipFile.Open(nupkg, ZipArchiveMode.Create))
        {
            Put(zip, "Greeter.nuspec", Encoding.UTF8.GetBytes(nuspec));
            Put(zip, "lib/net10.0/Greeter.dll", dll.ToArray());
        }
        File.WriteAllText(Path.Combine(dir, "greeter.nuspec"), nuspec);
        File.WriteAllText(nupkg + ".sha512", PackageStore.HashOf(nupkg));
    }

    private static void Put(ZipArchive zip, string name, byte[] bytes)
    {
        using var s = zip.CreateEntry(name).Open();
        s.Write(bytes);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { /* a leftover scratch folder is the operating system's to clean */ }
    }
}
