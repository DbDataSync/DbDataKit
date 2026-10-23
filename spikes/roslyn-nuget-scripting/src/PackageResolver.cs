using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace RoslynNuGetScripting;

public sealed record ResolvedPackage(string Id, NuGetVersion Version, string Directory, string Sha512);

/// <summary>What the loader and the compiler need from a resolved set of packages.</summary>
public sealed class Closure
{
    public List<ResolvedPackage> Packages { get; } = [];
    /// <summary>Assembly simple name to the file to load at run time.</summary>
    public Dictionary<string, string> Managed { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Assembly files to compile against (ref/ assets when a package has them, lib/ otherwise).</summary>
    public Dictionary<string, string> Compile { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Native file name to its path, taken from runtimes/&lt;rid&gt;/native/.</summary>
    public Dictionary<string, string> Native { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class PackageResolverOptions
{
    public required PackageStore Store { get; init; }
    /// <summary>nuget.org-style index URLs or folders (a global-packages-style folder works as a feed).</summary>
    public required IReadOnlyList<string> Sources { get; init; }
    public NuGetFramework Target { get; init; } = NuGetFramework.Parse($"net{Environment.Version.Major}.{Environment.Version.Minor}");
    /// <summary>Assemblies the host already provides (shared framework); a package's copy of these is not used.</summary>
    public Func<string, bool> HostProvides { get; init; } = _ => false;
    /// <summary>The allow-list hook: called with the chosen id and version before anything is downloaded.</summary>
    public Func<PackageIdentity, bool> IsAllowed { get; init; } = _ => true;
    /// <summary>Expected base64 SHA-512 of the .nupkg, keyed by "id/version" (lower case).</summary>
    public IReadOnlyDictionary<string, string> PinnedHashes { get; init; } = new Dictionary<string, string>();
    public Action<string> Log { get; init; } = _ => { };
}

public sealed class PackageResolver(PackageResolverOptions options)
{
    readonly PackageStore store = options.Store;
    readonly SourceCacheContext cache = new() { NoCache = true, DirectDownload = true };
    readonly ILogger nugetLog = NullLogger.Instance;

    public async Task<Closure> ResolveAsync(IEnumerable<PackageRequest> roots, CancellationToken ct = default)
    {
        var chosen = new Dictionary<string, ResolvedPackage>(StringComparer.OrdinalIgnoreCase);
        var direct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<PackageRequest>();
        foreach (var root in roots) { direct.Add(root.Id); queue.Enqueue(root); }

        while (queue.TryDequeue(out var request))
        {
            if (chosen.TryGetValue(request.Id, out var current))
            {
                if (request.Range.Satisfies(current.Version)) continue;
                // Same rule as NuGet: a package the script names itself wins over a transitive request.
                if (direct.Contains(request.Id)) { options.Log($"warning: {request.Id} {current.Version} is kept, though a dependency wants {request.Range}"); continue; }
            }
            var package = await EnsureAsync(request, ct);
            chosen[request.Id] = package;
            var reader = new PackageFolderReader(package.Directory);
            var group = NuGetFrameworkUtility.GetNearest(reader.GetPackageDependencies(), options.Target);
            // Packages that only matter to the build (analyzers, build props) are not loaded, so their dependencies are not followed either.
            foreach (var dep in group?.Packages ?? [])
                queue.Enqueue(new PackageRequest(dep.Id, dep.VersionRange));
        }

        var closure = new Closure();
        foreach (var package in chosen.Values.OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            closure.Packages.Add(package);
            AddAssets(closure, package);
        }
        return closure;
    }

    async Task<ResolvedPackage> EnsureAsync(PackageRequest request, CancellationToken ct)
    {
        var (id, range) = (request.Id, request.Range);
        // A fixed range that the cache already satisfies never touches the network, which is what makes offline runs work.
        if (!range.IsFloating && range.FindBestMatch(store.CachedVersions(id)) is { } cached)
            return Open(id, cached);

        var offered = new List<(FindPackageByIdResource Source, string Name, NuGetVersion Version)>();
        foreach (var source in options.Sources)
        {
            try
            {
                var resource = await Repository.Factory.GetCoreV3(source).GetResourceAsync<FindPackageByIdResource>(ct)
                    ?? throw new InvalidOperationException("the source offers no package download service");
                foreach (var v in await resource.GetAllVersionsAsync(id, cache, nugetLog, ct) ?? []) offered.Add((resource, source, v));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                options.Log($"warning: source {source} unavailable: {e.GetBaseException().Message}");
            }
        }
        var best = range.FindBestMatch(offered.Select(o => o.Version).Distinct());
        if (best is null)
        {
            // Floating range with every source down: fall back to what is on disk.
            if (range.FindBestMatch(store.CachedVersions(id)) is { } fallback) return Open(id, fallback);
            throw new InvalidOperationException($"No version of {id} satisfies {range} in the cache or in {string.Join(", ", options.Sources)}");
        }
        var identity = new PackageIdentity(id, best);
        if (!options.IsAllowed(identity)) throw new UnauthorizedAccessException($"{identity} is not on the allow-list");
        if (store.CachedVersions(id).Contains(best)) return Open(id, best);

        var from = offered.First(o => o.Version == best);
        options.Log($"downloading {identity} from {from.Name}");
        var temp = Path.Combine(Path.GetTempPath(), "nupkg-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = File.Create(temp))
                if (!await from.Source.CopyNupkgToStreamAsync(id, best, file, cache, nugetLog, ct))
                    throw new InvalidOperationException($"{from.Name} listed {identity} but would not serve it");
            store.Add(identity, temp, Pinned(id, best));
        }
        finally { File.Delete(temp); }
        return Open(id, best);
    }

    ResolvedPackage Open(string id, NuGetVersion version)
    {
        if (!options.IsAllowed(new PackageIdentity(id, version))) throw new UnauthorizedAccessException($"{id} {version} is not on the allow-list");
        return new ResolvedPackage(id, version, store.DirectoryOf(id, version), store.Verify(id, version, Pinned(id, version)));
    }

    string? Pinned(string id, NuGetVersion v) => options.PinnedHashes.TryGetValue($"{id}/{v.ToNormalizedString()}".ToLowerInvariant(), out var h) ? h : null;

    void AddAssets(Closure closure, ResolvedPackage package)
    {
        var reader = new PackageFolderReader(package.Directory);
        var target = options.Target;
        var rids = Platform.RidChain();

        // runtimes/<rid>/lib/<tfm>/ beats lib/<tfm>/ for the best rid: it is the platform-specific build of the same assembly.
        var runtimeLibs = reader.GetFiles("runtimes")
            .Select(f => f.Split('/'))
            .Where(p => p.Length >= 5 && p[2] == "lib" && Array.IndexOf(rids, p[1]) >= 0 && p[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p[1]).OrderBy(g => Array.IndexOf(rids, g.Key)).FirstOrDefault();
        var libGroup = NuGetFrameworkUtility.GetNearest(reader.GetLibItems(), target);
        var refGroup = NuGetFrameworkUtility.GetNearest(reader.GetReferenceItems(), target);

        var runtimeAssets = runtimeLibs is null ? Dll(libGroup?.Items) : PickTfm(runtimeLibs.Select(p => string.Join('/', p)), target);
        foreach (var rel in runtimeAssets) AddAssembly(closure.Managed, package, rel, "run");
        foreach (var rel in Dll(refGroup?.Items ?? libGroup?.Items)) AddAssembly(closure.Compile, package, rel, "compile");

        // Native files: the first rid in the chain that ships a file name wins. build/ props and targets are never evaluated.
        foreach (var rid in rids)
            foreach (var rel in reader.GetFiles($"runtimes/{rid}/native"))
                closure.Native.TryAdd(Path.GetFileName(rel), Path.Combine(package.Directory, rel));
    }

    static IEnumerable<string> Dll(IEnumerable<string>? items) => (items ?? []).Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

    static IEnumerable<string> PickTfm(IEnumerable<string> files, NuGetFramework target)
    {
        // runtimes/<rid>/lib/<tfm>/x.dll: choose the nearest tfm folder.
        var groups = files.GroupBy(f => NuGetFramework.ParseFolder(f.Split('/')[3])).Select(g => new FrameworkSpecificGroup(g.Key, g)).ToList();
        return NuGetFrameworkUtility.GetNearest(groups, target)?.Items ?? [];
    }

    void AddAssembly(Dictionary<string, string> into, ResolvedPackage package, string relative, string what)
    {
        var path = Path.Combine(package.Directory, relative);
        var name = Path.GetFileNameWithoutExtension(path);
        // The shared framework wins over a package's copy (the SDK does the same by default); loading both would split every type.
        if (options.HostProvides(name)) { options.Log($"  {what}: {name} is provided by the host, package copy ignored"); return; }
        if (into.TryGetValue(name, out var existing) && Version(existing) >= Version(path)) return;
        into[name] = path;
    }

    static Version Version(string path)
    {
        try { return System.Reflection.AssemblyName.GetAssemblyName(path).Version ?? new Version(); }
        catch (BadImageFormatException) { return new Version(); }
    }
}
