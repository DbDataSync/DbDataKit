using DbDataKit.Scripting;

namespace DbDataKit.Scripting.Tests;

/// <summary>The resolver on real packages, when the machine has them: NuGet's global-packages folder is itself a feed. Skipped where Npgsql was never restored (a fresh machine).</summary>
public class RealPackagesTests
{
    private static string GlobalPackages =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set ? set : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    [Fact]
    public async Task Npgsql_and_what_it_needs_resolve_from_the_machines_package_folder_with_framework_packages_left_out()
    {
        var npgsql = Path.Combine(GlobalPackages, "npgsql");
        Assert.SkipUnless(Directory.Exists(npgsql) && Directory.EnumerateDirectories(npgsql).Any(), "Npgsql is not in the machine's NuGet package folder");
        var version = Directory.EnumerateDirectories(npgsql).Select(Path.GetFileName).Where(v => v is not null && !v.Contains('-')).Order().Last()!;

        using var temp = new Feed();
        var options = new PackageResolverOptions
        {
            Store = new PackageStore(temp.Cache),
            Sources = [new PackageSource(GlobalPackages)],
            HostProvides = name => Platform.FrameworkAssemblies().ContainsKey(name),
        };

        var closure = await new PackageResolver(options).ResolveAsync([PackageRequest.Exact("Npgsql", version)]);

        Assert.Contains(closure.Packages, p => p.Id == "Npgsql");
        Assert.Contains("Npgsql", closure.Managed.Keys);
        Assert.All(closure.Managed.Values, path => Assert.StartsWith(Path.GetFullPath(temp.Cache), path));        // everything is taken from the cache, not from the machine's folder
        Assert.All(closure.Packages, p => Assert.False(Platform.FrameworkOwnedPackages.Contains(p.Id)));
        Assert.DoesNotContain(closure.Managed.Keys, name => name.StartsWith("System.", StringComparison.Ordinal) && Platform.FrameworkAssemblies().ContainsKey(name));
    }
}
