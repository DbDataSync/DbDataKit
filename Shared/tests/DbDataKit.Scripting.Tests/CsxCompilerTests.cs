using System.Runtime.CompilerServices;
using DbDataKit.Scripting;

namespace DbDataKit.Scripting.Tests;

public interface IHostThing
{
    string Name { get; }
}

public sealed class Globals
{
    public int Factor { get; init; }
}

public sealed class CsxCompilerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "csx-" + Guid.NewGuid().ToString("N")[..8]);

    public CsxCompilerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private CsxCompiler Compiler(Action<CsxCompilerOptionsBuilder>? configure = null)
    {
        var builder = new CsxCompilerOptionsBuilder { Cache = Path.Combine(_root, "cache"), LoadRoot = Path.Combine(_root, "repo") };
        configure?.Invoke(builder);
        Directory.CreateDirectory(builder.LoadRoot);
        return new CsxCompiler(new CsxCompilerOptions { CacheDirectory = builder.Cache, LoadRoot = builder.LoadRoot, HostReferences = builder.HostReferences, GlobalsType = builder.GlobalsType });
    }

    private sealed class CsxCompilerOptionsBuilder
    {
        public string Cache = "", LoadRoot = "";
        public IReadOnlyList<string> HostReferences = [];
        public Type? GlobalsType;
    }

    private static async Task<object?> RunAsync(CsxCompilation compiled, object? globals = null, Func<string, bool>? shared = null)
    {
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));
        using var loader = new ClosureLoader(shared);
        return await CsxCompiler.RunAsync(compiled.Image!, loader.Load("t", new Closure()), globals);
    }

    [Fact]
    public async Task A_script_compiles_runs_and_hands_back_its_last_expression()
    {
        var compiled = Compiler().Compile("var xs = new[] { 1, 2, 3 };\nxs.Select(x => x * 2).Sum()", "sum.csx", new Closure());

        Assert.Equal(12, await RunAsync(compiled));
    }

    [Fact]
    public void Roslyns_submission_entry_point_is_where_we_expect_it()
    {
        // the one test that pins the undocumented naming convention the runner depends on (plan 14, risk 4)
        var compiled = Compiler().Compile("1", "pin.csx", new Closure());
        var assembly = System.Reflection.Assembly.Load(compiled.Image!);

        var factory = assembly.GetType("Submission#0")?.GetMethod("<Factory>");

        Assert.NotNull(factory);
        Assert.True(factory!.IsStatic);
        Assert.Equal(typeof(Task<object>), factory.ReturnType);
    }

    [Fact]
    public void An_error_is_reported_at_the_scripts_own_line_even_after_a_directive_line_was_blanked()
    {
        var (_, source) = ScriptDirectives.Extract("#r \"nuget: Some.Package, 1.0.0\"\n\nvar ok = 1;\nvar bad = missing + 1;\n");

        var compiled = Compiler().Compile(source, "line.csx", new Closure());

        Assert.False(compiled.Success);
        var error = Assert.Single(compiled.Diagnostics, d => d.IsError);
        Assert.Equal(("line.csx", 4), (error.File, error.Line));
        Assert.Contains("missing", error.Message);
    }

    [Theory]
    [InlineData("using System.IO;\n1", "System.IO")]
    [InlineData("System.IO.File.ReadAllText(\"x\")", "System.IO")]
    [InlineData("System.Diagnostics.Process.Start(\"x\")", "System.Diagnostics")]
    [InlineData("typeof(string).Assembly.GetType(\"x\"); System.Reflection.Assembly.GetCallingAssembly()", "System.Reflection")]
    public void A_script_that_names_a_banned_namespace_is_refused_with_the_reason_before_anything_is_emitted(string source, string banned)
    {
        var compiled = Compiler().Compile(source, "guard.csx", new Closure());

        Assert.False(compiled.Success);
        Assert.Contains(compiled.Diagnostics, d => d.Message.Contains($"may not use '{banned}") && d.Line > 0);
    }

    [Fact]
    public async Task Load_brings_in_a_file_next_to_the_script_and_a_change_to_it_recompiles()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "lib"));
        File.WriteAllText(Path.Combine(repo, "lib", "helper.csx"), "int Twice(int x) => x * 2;");
        var compiler = Compiler();
        var path = Path.Combine(repo, "lib", "main.csx");
        const string Script = "#load \"helper.csx\"\nTwice(21)";

        var first = compiler.Compile(Script, path, new Closure());
        Assert.Equal(42, await RunAsync(first));
        Assert.True(compiler.Compile(Script, path, new Closure()).FromCache);

        File.WriteAllText(Path.Combine(repo, "lib", "helper.csx"), "int Twice(int x) => x * 3;");
        var changed = compiler.Compile(Script, path, new Closure());
        Assert.False(changed.FromCache);
        Assert.Equal(63, await RunAsync(changed));
    }

    [Theory]
    [InlineData("#load \"../../outside.csx\"\n1")]
    [InlineData("#load \"/etc/passwd.csx\"\n1")]
    [InlineData("#load \"helper.cs\"\n1")]
    [InlineData("#r \"/usr/lib/dotnet/shared/Microsoft.NETCore.App/x.dll\"\n1")]
    public void Load_and_file_references_that_leave_the_repository_are_refused(string source)
    {
        File.WriteAllText(Path.Combine(_root, "outside.csx"), "1");
        Directory.CreateDirectory(Path.Combine(_root, "repo", "lib"));
        File.WriteAllText(Path.Combine(_root, "repo", "lib", "helper.cs"), "1");

        var compiled = Compiler().Compile(source, Path.Combine(_root, "repo", "lib", "main.csx"), new Closure());

        Assert.False(compiled.Success);
    }

    [Fact]
    public void A_link_inside_the_repository_that_points_outside_it_is_refused()
    {
        var repo = Path.Combine(_root, "repo");
        Directory.CreateDirectory(repo);
        File.WriteAllText(Path.Combine(_root, "secret.csx"), "1");
        try { File.CreateSymbolicLink(Path.Combine(repo, "link.csx"), Path.Combine(_root, "secret.csx")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException) { Assert.Skip("symbolic links need a privilege here"); }

        Assert.False(Compiler().Compile("#load \"link.csx\"\n1", Path.Combine(repo, "main.csx"), new Closure()).Success);
    }

    [Fact]
    public void A_second_compile_of_the_same_script_comes_from_the_cache_and_a_changed_one_does_not()
    {
        var compiler = Compiler();

        Assert.False(compiler.Compile("1 + 1", "c.csx", new Closure()).FromCache);
        Assert.True(compiler.Compile("1 + 1", "c.csx", new Closure()).FromCache);
        Assert.False(compiler.Compile("1 + 2", "c.csx", new Closure()).FromCache);
    }

    [Fact]
    public async Task The_host_hands_a_script_its_globals_and_its_contract_assemblies_and_the_types_are_the_hosts()
    {
        var compiler = Compiler(b => { b.HostReferences = [typeof(IHostThing).Assembly.Location]; b.GlobalsType = typeof(Globals); });
        const string Script = "class Thing(int factor) : DbDataKit.Scripting.Tests.IHostThing { public string Name => \"thing\" + factor; }\nnew Thing(Factor)";
        var compiled = compiler.Compile(Script, "host.csx", new Closure());
        var hostAssembly = typeof(IHostThing).Assembly.GetName().Name;

        var thing = await RunAsync(compiled, new Globals { Factor = 7 }, shared: name => name == hostAssembly);

        Assert.Equal("thing7", Assert.IsAssignableFrom<IHostThing>(thing).Name);       // castable: the script's interface is the host's, not a copy
    }

    [Fact]
    public async Task A_script_with_a_package_runs_inside_its_context_and_the_context_is_collected_afterwards()
    {
        using var temp = new Feed();
        var closure = await RealPackages.ResolveAsync(temp, "Npgsql");
        var compiled = Compiler().Compile("typeof(Npgsql.NpgsqlConnection).Assembly.GetName().Name", "pg.csx", closure);
        Assert.True(compiled.Success, string.Join("\n", compiled.Diagnostics));

        var (answer, tracked) = RunAndRelease(compiled, closure);

        Assert.Equal("Npgsql", answer);
        Assert.True(await ClosureLoader.UnloadedAsync(tracked), "the script's context was still referenced after it was unloaded");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object? Answer, WeakReference Tracked) RunAndRelease(CsxCompilation compiled, Closure closure)
    {
        using var loader = new ClosureLoader();
        var context = loader.Load("pg", closure);
        var tracked = ClosureLoader.Track(context);
        var answer = CsxCompiler.RunAsync(compiled.Image!, context).GetAwaiter().GetResult();
        loader.Unload("pg");
        return (answer, tracked);
    }
}
