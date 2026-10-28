using System.Data.Common;
using DbDataKit.Scripting;

namespace DbDataSync.Libraries;

/// <summary>
/// Loads every <c>&lt;repo&gt;/libraries/*/library.csx</c> and registers its
/// <see cref="DbProviderFactory"/> by name, so any consumer — the state store, a driver, a script —
/// gets one without the solution ever referencing the underlying NuGet package. See
/// <c>architecture/planning/todo/nuget-loaded-drivers.md</c> §*The provider layer*.
/// <para>
/// **Registration is the string overload** —
/// <see cref="DbProviderFactories.RegisterFactory(string, string)"/> — which does a lazy
/// <c>Type.GetType()</c> on first <see cref="DbProviderFactories.GetFactory(string)"/>, so the
/// assembly only has to be *loadable* at that point, never referenced by anything in this solution.
/// </para>
/// <para>
/// **Loadable** means: the library's packages are resolved from its own store (<c>libraries/&lt;id&gt;/lib/</c>, checked against
/// <c>library.lock</c>, no network) and bound to the *default* load context by <see cref="DefaultContextBinder"/>, so a library's own managed dependencies and
/// <c>runtimes/&lt;rid&gt;/native/</c> assets resolve. Libraries load into the *default* context deliberately, not a per-library isolated one — a <c>SqlConnection</c>
/// from a library and one the built-in driver is compiled against must be the same <c>Type</c>, which an isolated
/// context would break. The cost is that a library is never unloaded.
/// </para>
/// </summary>
public sealed class LibraryRegistry
{
    private readonly string _librariesRoot;

    public LibraryRegistry(string repoRoot) => _librariesRoot = LibraryPaths.LibrariesDir(repoRoot);

    /// <summary>Every installed library's manifest, id-keyed. Populated by <see cref="LoadAll"/>;
    /// empty (not an error) for a repo with no <c>libraries/</c> directory at all.</summary>
    public IReadOnlyDictionary<string, LibraryManifest> Installed { get; private set; } =
        new Dictionary<string, LibraryManifest>(StringComparer.Ordinal);

    /// <summary>Libraries whose files are written but whose packages could not be loaded from the store (not restored yet, a hash that no longer matches), with the reason. They are in <see cref="Installed"/> too.</summary>
    public IReadOnlyDictionary<string, string> NotRestored { get; private set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Things worth knowing that did not stop a library loading: another library already supplies one of its assemblies (the first one loaded is used, as the default context allows only one).</summary>
    public IReadOnlyDictionary<string, string> Warnings { get; private set; } = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Scans <c>libraries/*/library.csx</c>, binds each one's packages and registers every factory. An absent or empty <c>libraries/</c> directory is a silent no-op — most
    /// deployments have none. A folder that still holds the old <c>library.json</c> and no <c>library.csx</c> stops the load with a message naming the file.
    /// </summary>
    public LibraryRegistry LoadAll()
    {
        var installed = new Dictionary<string, LibraryManifest>(StringComparer.Ordinal);
        var problems = new Dictionary<string, string>(StringComparer.Ordinal);
        var warnings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (Directory.Exists(_librariesRoot))
        {
            foreach (var dir in Directory.EnumerateDirectories(_librariesRoot))
            {
                RefuseOldFormat(dir);
                if (!File.Exists(LibraryPaths.ManifestPath(dir)))
                    continue;

                var manifest = Load(dir, problems, warnings);
                installed[manifest.Id] = manifest;
            }
        }

        Installed = installed;
        NotRestored = problems;
        Warnings = warnings;
        return this;
    }

    private static void RefuseOldFormat(string libraryDir)
    {
        var old = Path.Combine(libraryDir, LibraryPaths.RefusedManifestFileName);
        if (File.Exists(old) && !File.Exists(LibraryPaths.ManifestPath(libraryDir)))
            throw new InvalidDataException(
                $"'{old}' is the old library format and is no longer read. A library is now a library.csx of #r \"nuget: Id, 1.2.3\" lines: " +
                $"run `dbdatasync library install {Path.GetFileName(libraryDir)}` to write it, or write it by hand and run `dbdatasync library sync`.");
    }

    private static LibraryManifest Load(string libraryDir, Dictionary<string, string> problems, Dictionary<string, string> warnings)
    {
        var manifest = LibraryManifest.Read(LibraryPaths.ManifestPath(libraryDir));
        var factoryType = manifest.FactoryType;
        var libDir = LibraryPaths.LibDir(libraryDir);
        if (Directory.Exists(libDir))
        {
            try
            {
                var closure = LibraryResolution.LoadAsync(libraryDir, manifest.Packages).GetAwaiter().GetResult();
                var conflicts = DefaultContextBinder.Bind(closure, [libDir]);
                if (conflicts.Count > 0)
                    warnings[manifest.Id] = $"another library already supplies {string.Join(", ", conflicts)}; the first one loaded is used.";
                if (factoryType.Length == 0)
                {
                    var discovery = FactoryTypeReflector.Discover(closure.Managed.Values.ToList());
                    if (discovery.Status == FactoryTypeDiscoveryStatus.Found) factoryType = discovery.FactoryType!;
                    else problems[manifest.Id] = "the packages carry no single DbProviderFactory; name it with a `// factory: Type, Assembly` line.";
                }
            }
            catch (Exception ex) when (ex is PackageResolutionException or PackageIntegrityException or InvalidDataException)
            {
                problems[manifest.Id] = ex.Message;
            }
        }
        else
        {
            problems[manifest.Id] = $"not restored: run `dbdatasync library sync {manifest.Id}`.";
        }

        if (factoryType.Length > 0) DbProviderFactories.RegisterFactory(manifest.Id, factoryType);
        return manifest with { FactoryType = factoryType };
    }

    /// <summary>
    /// Loads and registers exactly one library already on disk (by id) into this same instance's
    /// <see cref="Installed"/>, without rescanning — for a caller (phase 120's <c>POST /api/libraries</c>)
    /// that just wrote it and wants this process's own view of "is it installed" to reflect that
    /// immediately, without a restart. A silent no-op if <paramref name="id"/> isn't actually on disk.
    /// </summary>
    public void RegisterInstalled(string id)
    {
        var libraryDir = Path.Combine(_librariesRoot, id);
        RefuseOldFormat(libraryDir);
        if (!File.Exists(LibraryPaths.ManifestPath(libraryDir)))
            return;

        var problems = new Dictionary<string, string>(NotRestored, StringComparer.Ordinal);
        problems.Remove(id);
        var warnings = new Dictionary<string, string>(Warnings, StringComparer.Ordinal);
        warnings.Remove(id);
        var manifest = Load(libraryDir, problems, warnings);
        NotRestored = problems;
        Warnings = warnings;
        Installed = new Dictionary<string, LibraryManifest>(Installed, StringComparer.Ordinal) { [manifest.Id] = manifest };
    }

    /// <summary>Drops <paramref name="id"/> from <see cref="Installed"/> — for
    /// <c>DELETE /api/libraries/{id}</c> (phase 120), so this process's own view stops claiming a
    /// removed library is installed. Does not (cannot) un-bind its assemblies or un-register its factory
    /// name; <see cref="GetFactory"/> refuses by <see cref="Installed"/> membership
    /// first, before ever reaching <see cref="DbProviderFactories.GetFactory(string)"/>.</summary>
    public void Remove(string id)
    {
        if (!Installed.ContainsKey(id))
            return;

        Installed = Installed.Where(kv => kv.Key != id).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        NotRestored = NotRestored.Where(kv => kv.Key != id).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    /// <summary>
    /// <see cref="DbProviderFactories.GetFactory(string)"/>, with a message that names the fix
    /// (<c>dbdatasync library install</c>) instead of the BCL's generic "no factory registered".
    /// <para>
    /// A <see cref="KnownLibraries"/> entry has two different ids for the same library — a short catalog
    /// id (<c>"mysql-connector"</c>) and the real NuGet package id (<c>"MySqlConnector"</c>); see
    /// <see cref="LibraryCatalogEntry"/>. Which one an install ends up keyed by depends on which endpoint
    /// installed it (see
    /// <c>architecture/planning/todo/follow-up-library-install-paths-disagree-on-the-resulting-library-id.md</c>).
    /// When <paramref name="id"/> isn't installed but names one of those two ids for an entry installed
    /// under the other one, the message points at the installed id directly, as a "did you mean" — not
    /// the generic "not installed", which reads identically to a genuine miss.
    /// </para>
    /// </summary>
    public DbProviderFactory GetFactory(string id)
    {
        if (!Installed.ContainsKey(id))
            throw new InvalidOperationException(NotInstalledMessage(id));

        return DbProviderFactories.GetFactory(id);
    }

    /// <summary>The plain "not installed" message, unless <paramref name="id"/> is a
    /// <see cref="KnownLibraries"/> entry's catalog id or package id and the library is installed under
    /// that entry's other id — the one case worth naming explicitly, since every other "not installed"
    /// is a plain miss with no better answer to give.</summary>
    private string NotInstalledMessage(string id)
    {
        var entry = KnownLibraries.TryGetByIdOrPackageId(id);

        if (entry is not null)
        {
            var installedId = string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase) ? entry.PackageId : entry.Id;
            var installedIdKind = installedId == entry.PackageId ? "package id" : "catalog id";

            if (Installed.ContainsKey(installedId))
                return $"Library '{id}' not found. A library is installed under the {installedIdKind} " +
                       $"'{installedId}'. Did you mean '{installedId}', or install '{id}' explicitly with " +
                       $"`dbdatasync library install {id}`?";
        }

        return $"Library '{id}' is not installed. Install it with `dbdatasync library install {id}`.";
    }
}

/// <summary>The on-disk layout every library (and, from 109d/109e on, every driver) shares — one
/// place both the installer and the registry agree on it.</summary>
public static class LibraryPaths
{
    public const string ManifestFileName = "library.csx";
    public const string RefusedManifestFileName = "library.json";
    public const string LibDirName = "lib";

    public static string LibrariesDir(string repoRoot) => Path.Combine(repoRoot, "libraries");
    public static string LibraryDir(string repoRoot, string id) => Path.Combine(LibrariesDir(repoRoot), id);
    public static string ManifestPath(string libraryDir) => Path.Combine(libraryDir, ManifestFileName);
    public static string LibDir(string libraryDir) => Path.Combine(libraryDir, LibDirName);
}
