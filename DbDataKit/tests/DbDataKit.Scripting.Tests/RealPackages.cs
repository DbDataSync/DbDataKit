using DbDataKit.Scripting;

namespace DbDataKit.Scripting.Tests;

/// <summary>Real packages from the machine's NuGet folder, for the tests that need an actual library; they are skipped where it was never restored.</summary>
internal static class RealPackages
{
    internal static string GlobalPackages =>
        Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } set ? set : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");

    internal static string? NewestStable(string id)
    {
        var folder = Path.Combine(GlobalPackages, id.ToLowerInvariant());
        return Directory.Exists(folder) ? Directory.EnumerateDirectories(folder).Select(Path.GetFileName).Where(v => v is not null && !v.Contains('-')).Order().LastOrDefault() : null;
    }

    internal static async Task<Closure> ResolveAsync(Feed temp, string id)
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
}
