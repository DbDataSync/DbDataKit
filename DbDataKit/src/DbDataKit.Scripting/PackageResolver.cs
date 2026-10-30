using NuGet.Common;
using NuGet.Frameworks;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;

namespace DbDataKit.Scripting;

/// <summary>A package chosen for a closure: which version, where it is on disk, and the hash of its .nupkg.</summary>
public sealed record ResolvedPackage(string Id, NuGetVersion Version, string Directory, string Sha512);

/// <summary>Something the resolver decided that the person who wrote the script should know: a version kept against a dependency's wish, a source that did not answer. Never an error: an error is an exception.</summary>
public sealed record ResolutionWarning(string Code, string Message);

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

    public List<ResolutionWarning> Warnings { get; } = [];

    /// <summary>Packages the closure did not take because the framework the host runs on owns what they stand for.</summary>
    public List<string> Skipped { get; } = [];
}

public sealed class PackageResolverOptions
{
    public required PackageStore Store { get; init; }
    public required IReadOnlyList<PackageSource> Sources { get; init; }
    public NuGetFramework Target { get; init; } = NuGetFramework.Parse($"net{Environment.Version.Major}.{Environment.Version.Minor}");

    /// <summary>Assemblies the host already provides (shared framework); a package's copy of these is not used.</summary>
    public Func<string, bool> HostProvides { get; init; } = _ => false;

    /// <summary>The allow-list hook: called with the chosen id and version before anything is downloaded or taken from the cache.</summary>
    public Func<PackageIdentity, bool> IsAllowed { get; init; } = _ => true;

    /// <summary>The hash pins (the lock file). A new package's hash is added to it; a known one must match.</summary>
    public LockFile Lock { get; init; } = new();

    /// <summary>A package with no pin in the lock file is refused: for scripts and libraries that are stored and run, where nobody is there to trust a first download.</summary>
    public bool RequirePinned { get; init; }

    /// <summary>Use the cache only; a version that is not there is an error, and no source is asked.</summary>
    public bool Offline { get; init; }

    /// <summary>The runtime identifiers to take native and platform-specific assets for, most specific first.</summary>
    public IReadOnlyList<string> Rids { get; init; } = Platform.RidChain();

    public IReadOnlySet<string> FrameworkOwnedPackages { get; init; } = Platform.FrameworkOwnedPackages;
}

/// <summary>A package could not be had: no version satisfies a request, no source serves it, it is not allowed, or it is not pinned when it must be.</summary>
public sealed class PackageResolutionException(string message) : InvalidOperationException(message);

/// <summary>
/// Turns package requests into a closure on disk, with only the .NET runtime (no SDK, no MSBuild, no restore): the nearest target framework's dependencies are followed, versions are picked by NuGet's rules (the lowest that
/// satisfies a fixed range, the highest of a floating one), a package the script names itself wins over a transitive request, and the assets taken are the run-time and compile assemblies and the native files of the best runtime
/// identifier. <c>build/</c> props and targets, analyzers and tools are never evaluated. Every package is hashed when it is stored and checked on every use.
/// </summary>
public sealed class PackageResolver(PackageResolverOptions options)
{
    private readonly PackageStore _store = options.Store;
    private readonly SourceCacheContext _cache = new() { NoCache = true, DirectDownload = true };

    public async Task<Closure> ResolveAsync(IEnumerable<PackageRequest> roots, CancellationToken ct = default)
    {
        var closure = new Closure();
        var requested = roots.ToList();
        var chosen = new Dictionary<string, (ResolvedPackage Package, PackageRequest By)>(StringComparer.OrdinalIgnoreCase);
        var direct = requested.Select(r => r.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<PackageRequest>(requested);
        var wanted = new Dictionary<string, List<PackageRequest>>(StringComparer.OrdinalIgnoreCase);

        while (queue.TryDequeue(out var request))
        {
            if (options.FrameworkOwnedPackages.Contains(request.Id)) { if (!closure.Skipped.Contains(request.Id, StringComparer.OrdinalIgnoreCase)) closure.Skipped.Add(request.Id); continue; }
            if (!wanted.TryGetValue(request.Id, out var list)) wanted[request.Id] = list = [];
            list.Add(request);

            if (chosen.TryGetValue(request.Id, out var current))
            {
                if (request.Range.Satisfies(current.Package.Version)) continue;
                // Same rule as NuGet: a package the script names itself wins over a transitive request.
                if (direct.Contains(request.Id))
                {
                    closure.Warnings.Add(new("scripting.version.kept", $"{request.Id} {current.Package.Version} is used, as the script asks for it, though a dependency wants {request.Range.PrettyPrint()}."));
                    continue;
                }
                closure.Warnings.Add(new("scripting.version.raised", $"{request.Id} is raised from {current.Package.Version} to satisfy {request.Range.PrettyPrint()}."));
            }

            var package = await EnsureAsync(request, closure, ct);
            chosen[request.Id] = (package, request);
            var reader = new PackageFolderReader(package.Directory);
            var group = NuGetFrameworkUtility.GetNearest(reader.GetPackageDependencies(), options.Target);
            foreach (var dep in group?.Packages ?? []) queue.Enqueue(new PackageRequest(dep.Id, dep.VersionRange));
        }

        // A version that was replaced may have asked for packages nothing asks for now: only what the final versions reach is in the closure.
        var reachable = Reachable(requested.Select(r => r.Id), chosen);
        foreach (var package in chosen.Values.Select(c => c.Package).Where(p => reachable.Contains(p.Id)).OrderBy(p => p.Id, StringComparer.OrdinalIgnoreCase))
        {
            closure.Packages.Add(package);
            AddAssets(closure, package);
        }
        return closure;
    }

    private HashSet<string> Reachable(IEnumerable<string> roots, Dictionary<string, (ResolvedPackage Package, PackageRequest By)> chosen)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var todo = new Stack<string>(roots);
        while (todo.TryPop(out var id))
        {
            if (!chosen.TryGetValue(id, out var c) || !seen.Add(id)) continue;
            var group = NuGetFrameworkUtility.GetNearest(new PackageFolderReader(c.Package.Directory).GetPackageDependencies(), options.Target);
            foreach (var dep in group?.Packages ?? []) todo.Push(dep.Id);
        }
        return seen;
    }

    private async Task<ResolvedPackage> EnsureAsync(PackageRequest request, Closure closure, CancellationToken ct)
    {
        var (id, range) = (request.Id, request.Range);
        // A fixed range that the cache already satisfies never touches the network, which is what makes offline runs work.
        if (!range.IsFloating && range.FindBestMatch(_store.CachedVersions(id)) is { } cached) return Open(id, cached);
        if (options.Offline)
        {
            if (range.FindBestMatch(_store.CachedVersions(id)) is { } offlineBest) return Open(id, offlineBest);
            throw new PackageResolutionException($"{id} {range.PrettyPrint()} is not in the cache, and the resolver is offline.");
        }

        var sources = PackageSource.For(options.Sources, id);
        if (sources.Count == 0) throw new PackageResolutionException($"No package source is mapped for '{id}'.");
        var offered = new List<(FindPackageByIdResource Source, string Name, NuGetVersion Version)>();
        foreach (var source in sources)
        {
            try
            {
                var resource = await Repository.Factory.GetCoreV3(source.Location).GetResourceAsync<FindPackageByIdResource>(ct)
                    ?? throw new InvalidOperationException("the source offers no package download service");
                foreach (var v in await resource.GetAllVersionsAsync(id, _cache, NullLogger.Instance, ct) ?? []) offered.Add((resource, source.Location, v));
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                closure.Warnings.Add(new("scripting.source.unavailable", $"The source {source.Location} did not answer: {e.GetBaseException().Message}"));
            }
        }
        var best = range.FindBestMatch(offered.Select(o => o.Version).Distinct());
        if (best is null)
        {
            // A floating range with every source down falls back to what is on disk.
            if (range.FindBestMatch(_store.CachedVersions(id)) is { } fallback) return Open(id, fallback);
            throw new PackageResolutionException($"No version of {id} satisfies {range.PrettyPrint()} in the cache or in {string.Join(", ", sources.Select(s => s.Location))}.");
        }
        var identity = new PackageIdentity(id, best);
        if (!options.IsAllowed(identity)) throw new PackageResolutionException($"{identity} is not on the allow-list.");
        if (_store.CachedVersions(id).Contains(best)) return Open(id, best);

        var from = offered.First(o => o.Version == best);
        var temp = Path.Combine(Path.GetTempPath(), "nupkg-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using (var file = File.Create(temp))
                if (!await from.Source.CopyNupkgToStreamAsync(id, best, file, _cache, NullLogger.Instance, ct))
                    throw new PackageResolutionException($"{from.Name} listed {identity} but would not serve it.");
            RequirePin(id, best);
            var hash = _store.Add(identity, temp, Pinned(id, best), options.Lock.Name);
            options.Lock.Pin(id, best.ToNormalizedString(), hash);
        }
        finally { File.Delete(temp); }
        return Open(id, best);
    }

    private ResolvedPackage Open(string id, NuGetVersion version)
    {
        if (!options.IsAllowed(new PackageIdentity(id, version))) throw new PackageResolutionException($"{id} {version} is not on the allow-list.");
        RequirePin(id, version);
        var hash = _store.Verify(id, version, Pinned(id, version), options.Lock.Name);
        options.Lock.Pin(id, version.ToNormalizedString(), hash);           // a package that was cached before the lock file existed is recorded now
        return new ResolvedPackage(id, version, _store.DirectoryOf(id, version), hash);
    }

    private string? Pinned(string id, NuGetVersion v) => options.Lock.Pins.TryGetValue(LockFile.KeyOf(id, v.ToNormalizedString()), out var h) ? h : null;

    private void RequirePin(string id, NuGetVersion v)
    {
        if (options.RequirePinned && Pinned(id, v) is null) throw new PackageResolutionException($"{id} {v} has no hash in the lock file, and a stored script may only use pinned packages. Add it with the command that adds a library.");
    }

    private void AddAssets(Closure closure, ResolvedPackage package)
    {
        var reader = new PackageFolderReader(package.Directory);
        var target = options.Target;
        var rids = options.Rids;

        // runtimes/<rid>/lib/<tfm>/ beats lib/<tfm>/ for the best rid: it is the platform-specific build of the same assembly.
        var runtimeLibs = reader.GetFiles("runtimes")
            .Select(f => f.Split('/'))
            .Where(p => p.Length >= 5 && p[2] == "lib" && rids.Contains(p[1]) && p[^1].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            .GroupBy(p => p[1]).OrderBy(g => rids.ToList().IndexOf(g.Key)).FirstOrDefault();
        var libGroup = NuGetFrameworkUtility.GetNearest(reader.GetLibItems(), target);
        var refGroup = NuGetFrameworkUtility.GetNearest(reader.GetItems("ref"), target);       // the ref/ folder; GetReferenceItems would be lib/ filtered by the nuspec's <references>

        var runtimeAssets = runtimeLibs is null ? Dll(libGroup?.Items) : PickTfm(runtimeLibs.Select(p => string.Join('/', p)), target);
        foreach (var rel in runtimeAssets) AddAssembly(closure, closure.Managed, package, rel, "run");
        foreach (var rel in Dll(refGroup?.Items ?? libGroup?.Items)) AddAssembly(closure, closure.Compile, package, rel, "compile");

        // Native files: the first rid in the chain that ships a file name wins. build/ props and targets are never evaluated.
        foreach (var rid in rids)
            foreach (var rel in reader.GetFiles($"runtimes/{rid}/native"))
                closure.Native.TryAdd(Path.GetFileName(rel), Path.GetFullPath(Path.Combine(package.Directory, rel)));
    }

    private static IEnumerable<string> Dll(IEnumerable<string>? items) => (items ?? []).Where(i => i.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> PickTfm(IEnumerable<string> files, NuGetFramework target)
    {
        // runtimes/<rid>/lib/<tfm>/x.dll: choose the nearest tfm folder.
        var groups = files.GroupBy(f => NuGetFramework.ParseFolder(f.Split('/')[3])).Select(g => new FrameworkSpecificGroup(g.Key, g)).ToList();
        return NuGetFrameworkUtility.GetNearest(groups, target)?.Items ?? [];
    }

    private void AddAssembly(Closure closure, Dictionary<string, string> into, ResolvedPackage package, string relative, string what)
    {
        var path = Path.GetFullPath(Path.Combine(package.Directory, relative));       // a package lists its files with / on every platform
        var name = Path.GetFileNameWithoutExtension(path);
        // The shared framework wins over a package's copy (the SDK does the same by default); loading both would split every type.
        if (options.HostProvides(name))
        {
            if (what == "run") closure.Warnings.Add(new("scripting.host.provides", $"{name} comes from the host's framework; the copy in {package.Id} {package.Version} is not used."));
            return;
        }
        if (into.TryGetValue(name, out var existing) && AssemblyVersion(existing) >= AssemblyVersion(path)) return;
        into[name] = path;
    }

    private static Version AssemblyVersion(string path)
    {
        try { return System.Reflection.AssemblyName.GetAssemblyName(path).Version ?? new Version(); }
        catch (BadImageFormatException) { return new Version(); }
    }
}
