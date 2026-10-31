using DbDataKit.Scripting;

namespace DbDataSync.Libraries;

/// <summary>
/// How a library's packages become files: the resolver of <c>DbDataKit.Scripting</c> with the library's own folder as its package store (<c>libraries/&lt;id&gt;/lib/</c>, which is not tracked in git) and
/// <c>library.lock</c> beside <c>library.csx</c> as its pins. Installing resolves against a feed (<see cref="DefaultSource"/> unless one is named); loading resolves from the store alone, so a started process never needs the network.
/// </summary>
public static class LibraryResolution
{
    public const string DefaultSource = "https://api.nuget.org/v3/index.json";

    public const string LockFileName = "library.lock";

    public static string LockPath(string libraryDir, string lockFileName = LockFileName) => Path.Combine(libraryDir, lockFileName);

    /// <summary>How a message names the lock file: its folder (the library's id) and name, for example <c>mysql/library.lock</c>.</summary>
    private static string DisplayName(string libraryDir, string lockFileName) => $"{Path.GetFileName(libraryDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))}/{lockFileName}";

    private static PackageResolverOptions Options(string libraryDir, IReadOnlyList<PackageSource> sources, bool offline, LockFile pins) => new()
    {
        Store = new PackageStore(LibraryPaths.LibDir(libraryDir)),
        Sources = sources,
        Offline = offline,
        Lock = pins,
        HostProvides = name => Platform.FrameworkAssemblies().ContainsKey(name),
    };

    /// <summary>Resolves the packages from a feed into the library's store, records their hashes in <c>library.lock</c> (the lock file is created by the first resolution; a package whose bytes differ from its pin is refused, naming the package, the version and the file) and returns the closure.</summary>
    public static async Task<Closure> RestoreAsync(string libraryDir, IReadOnlyList<PackageRef> packages, string? source, CancellationToken cancellationToken = default, string lockFileName = LockFileName)
    {
        var pins = LockFile.Read(LockPath(libraryDir, lockFileName), DisplayName(libraryDir, lockFileName));
        var resolver = new PackageResolver(Options(libraryDir, [new PackageSource(source ?? DefaultSource)], offline: false, pins));
        var closure = await resolver.ResolveAsync(packages.Select(p => PackageRequest.Exact(p.Id, p.Version)), cancellationToken).ConfigureAwait(false);
        pins.Replace(closure.Packages);
        pins.Write(LockPath(libraryDir, lockFileName));
        return closure;
    }

    /// <summary>Resolves from what is already in the library's store (and checks it against <c>library.lock</c>); throws <see cref="PackageResolutionException"/> when something is missing.</summary>
    public static Task<Closure> LoadAsync(string libraryDir, IReadOnlyList<PackageRef> packages, CancellationToken cancellationToken = default, string lockFileName = LockFileName)
    {
        var pins = LockFile.Read(LockPath(libraryDir, lockFileName), DisplayName(libraryDir, lockFileName));
        var resolver = new PackageResolver(Options(libraryDir, [], offline: true, pins));
        return resolver.ResolveAsync(packages.Select(p => PackageRequest.Exact(p.Id, p.Version)), cancellationToken);
    }
}
