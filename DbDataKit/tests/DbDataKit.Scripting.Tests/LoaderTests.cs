using System.Reflection;
using System.Runtime.CompilerServices;
using DbDataKit.Scripting;

namespace DbDataKit.Scripting.Tests;

/// <summary>The loader on real packages: Npgsql and DuckDB are resolved from the machine's NuGet folder, loaded into their own contexts, used, and unloaded. Skipped where they were never restored.</summary>
public class LoaderTests
{
    private static string GlobalPackages =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set ? set : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    private static string? NewestStable(string id)
    {
        var folder = Path.Combine(GlobalPackages, id.ToLowerInvariant());
        return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder).Select(Path.GetFileName).Where(v => v is not null && !v.Contains('-')).Order().LastOrDefault() : null;
    }

    private static async Task<Closure> ResolveAsync(Feed temp, string id)
    {
        var version = NewestStable(id);
        Assert.SkipUnless(version is not null, $"{id} is not in the machine's NuGet package folder");
        var options = new PackageResolverOptions
        {
            Store = new PackageStore(temp.Cache),
            Sources = [new PackageSource(GlobalPackages)],
            HostProvides = name => Platform.FrameworkAssemblies().ContainsKey(name),
        };
        return await new PackageResolver(options).ResolveAsync([PackageRequest.Exact(id, version!)]);
    }

    [Fact]
    public async Task A_closure_loads_into_its_own_context_and_its_types_are_not_the_hosts()
    {
        using var temp = new Feed();
        var closure = await ResolveAsync(temp, "Npgsql");
        using var loader = new ClosureLoader();

        var context = loader.Load("npgsql", closure);
        var assembly = context.LoadFromAssemblyName(new AssemblyName("Npgsql"));

        Assert.Same(context, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly));
        Assert.NotNull(assembly.GetType("Npgsql.NpgsqlConnection"));
        Assert.DoesNotContain(System.Runtime.Loader.AssemblyLoadContext.Default.Assemblies, a => a.GetName().Name == "Npgsql");
    }

    [Fact]
    public async Task A_name_the_host_shares_is_answered_by_the_default_context()
    {
        using var temp = new Feed();
        var closure = await ResolveAsync(temp, "Npgsql");
        closure.Managed["DbDataKit.Scripting"] = typeof(ClosureLoader).Assembly.Location;     // a package that ships a copy of the host's own contract assembly
        using var loader = new ClosureLoader(shared: name => name == "DbDataKit.Scripting");

        var context = loader.Load("shared", closure);

        Assert.Same(typeof(ClosureLoader).Assembly, context.LoadFromAssemblyName(new AssemblyName("DbDataKit.Scripting")));
    }

    [Fact]
    public async Task DuckDB_runs_a_query_through_its_native_library_from_the_closure_and_the_managed_side_unloads()
    {
        using var temp = new Feed();
        var closure = await ResolveAsync(temp, "DuckDB.NET.Data.Full");
        Assert.NotEmpty(closure.Native);
        using var loader = new ClosureLoader();

        var (answer, tracked) = RunAndRelease(loader, closure);

        Assert.Equal(42L, answer);
        Assert.True(await ClosureLoader.UnloadedAsync(tracked), "the context was still referenced after it was unloaded");
        Assert.False(loader.IsLoaded("duckdb"));
    }

    // everything that touches the loaded types stays in this method, so nothing of theirs is on the caller's stack when it collects
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (object? Answer, WeakReference Tracked) RunAndRelease(ClosureLoader loader, Closure closure)
    {
        var context = loader.Load("duckdb", closure);
        var tracked = ClosureLoader.Track(context);
        var data = context.LoadFromAssemblyName(new AssemblyName("DuckDB.NET.Data"));
        var connection = Activator.CreateInstance(data.GetType("DuckDB.NET.Data.DuckDBConnection")!, "Data Source=:memory:")!;
        var open = connection.GetType().GetMethod("Open")!;
        open.Invoke(connection, null);
        var command = connection.GetType().GetMethod("CreateCommand")!.Invoke(connection, null)!;
        command.GetType().GetProperty("CommandText")!.SetValue(command, "SELECT 42::BIGINT");
        var answer = command.GetType().GetMethod("ExecuteScalar")!.Invoke(command, null);
        ((IDisposable)command).Dispose();
        ((IDisposable)connection).Dispose();
        loader.Unload("duckdb");
        return (answer, tracked);
    }

    [Fact]
    public async Task Loading_under_the_same_key_again_replaces_the_context()
    {
        using var temp = new Feed();
        var closure = await ResolveAsync(temp, "Npgsql");
        using var loader = new ClosureLoader();

        var first = loader.Load("pg", closure);
        var second = loader.Load("pg", closure);

        Assert.NotSame(first, second);
        Assert.True(loader.IsLoaded("pg"));
        Assert.True(loader.Unload("pg"));
        Assert.False(loader.Unload("pg"));
    }
}
