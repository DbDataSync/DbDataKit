using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.Scripting;

namespace DbDataSync.Libraries;

/// <summary>What <see cref="DriverLibrariesPart.BuildAsync"/> wrote for one library or compiled driver.</summary>
/// <param name="Kind"><c>library</c> (<c>libraries/&lt;id&gt;</c>) or <c>driver</c> (<c>drivers/&lt;id&gt;</c>, a <c>driver.csx</c>).</param>
/// <param name="Packages">The packages of the closure, <c>Id version</c>, in the order of the lock file.</param>
/// <param name="Files">How many files the folder holds (the store and its pins).</param>
public sealed record DriverLibrariesPartEntry(string Kind, string Id, IReadOnlyList<string> Packages, int Files);

/// <summary>The index <c>driver-libraries.json</c> of a part: the platform it was restored for and what it holds.</summary>
public sealed record DriverLibrariesPartIndex(string Format, string Platform, string DuckDb, IReadOnlyList<DriverLibrariesPartEntry> Entries);

/// <summary>
/// The <c>driver-libraries</c> part of a definition package (plan 28c E06c): what <c>library install</c> and <c>driver install</c> leave under <c>libraries/&lt;id&gt;/lib/</c> and <c>drivers/&lt;id&gt;/lib/</c> on the machine that
/// authors a repository, restored at publish time for the platform being published, so a runner that has no feed and no .NET SDK finds every closure in the read-only view.
/// <para>
/// The part has the layout of the repository it belongs to (<c>libraries/&lt;id&gt;/{library.csx, library.lock, lib/}</c>, <c>drivers/&lt;id&gt;/{driver.csx, driver.lock, lib/}</c>), so the publishing action overlays it on the snapshot of
/// <c>config/</c> and <see cref="LibraryRegistry"/> and <see cref="DbDataSync.Drivers.Descriptor.DriverLoader"/> read it unchanged. Hashes already pinned in the repository's lock files are kept: a package whose bytes differ from
/// its pin is refused naming the package, as an install on a developer's machine does. A package that the platform needs natives for (<c>runtimes/&lt;rid&gt;/native</c>) is resolved for that platform, not the publisher's; the DuckDB
/// engine is the pinned one for the platform (<c>duckdb.pin</c>, hash-checked) and is refused for a platform the pin does not name.
/// </para>
/// </summary>
public static class DriverLibrariesPart
{
    public const string IndexFileName = "driver-libraries.json";
    public const string IndexFormat = "driver-libraries/1";

    /// <summary>The platforms a definition can be published for (plan 28c QE8): the runner's own, <c>linux-x64</c> and <c>win-x64</c>.</summary>
    public static readonly IReadOnlyList<string> Platforms = ["linux-x64", "win-x64"];

    /// <summary>The runtime identifiers to look under, most specific first, for a platform.</summary>
    public static IReadOnlyList<string> RidChainOf(string platform)
    {
        if (!Platforms.Contains(platform, StringComparer.Ordinal))
            throw new ArgumentException($"'{platform}' is not a platform a driver-libraries part is built for ({string.Join(", ", Platforms)}).", nameof(platform));
        var parts = platform.Split('-', 2);
        return Platform.RidChain(parts[0] == "win" ? "windows" : parts[0], parts[1]);
    }

    /// <summary>
    /// Restores every library and compiled driver of <paramref name="repoRoot"/> into <paramref name="partRoot"/> for <paramref name="platform"/> and writes the index. <paramref name="partRoot"/> must be empty or absent: the part is
    /// rebuilt whole, never merged into an older one.
    /// </summary>
    public static async Task<DriverLibrariesPartIndex> BuildAsync(
        string repoRoot, string partRoot, string platform, string? nugetSource = null, CancellationToken cancellationToken = default)
    {
        var rids = RidChainOf(platform);
        if (Directory.Exists(partRoot) && Directory.EnumerateFileSystemEntries(partRoot).Any())
            throw new InvalidOperationException($"'{partRoot}' is not empty: a driver-libraries part is built into an empty folder.");
        Directory.CreateDirectory(partRoot);

        var entries = new List<DriverLibrariesPartEntry>();
        var duckDb = "";
        foreach (var (kind, root, manifestName, lockName) in new[]
        {
            ("library", Path.Combine(repoRoot, "libraries"), LibraryPaths.ManifestFileName, LibraryResolution.LockFileName),
            ("driver", Path.Combine(repoRoot, "drivers"), "driver.csx", "driver.lock"),
        })
        {
            if (!Directory.Exists(root)) continue;
            foreach (var sourceDir in Directory.EnumerateDirectories(root).OrderBy(d => d, StringComparer.Ordinal))
            {
                var manifestPath = Path.Combine(sourceDir, manifestName);
                if (!File.Exists(manifestPath)) continue;
                var id = Path.GetFileName(sourceDir);
                var targetDir = Path.Combine(partRoot, Path.GetFileName(root), id);
                Directory.CreateDirectory(targetDir);
                File.Copy(manifestPath, Path.Combine(targetDir, manifestName));
                var pins = Path.Combine(sourceDir, lockName);
                if (File.Exists(pins)) File.Copy(pins, Path.Combine(targetDir, lockName));

                IReadOnlyList<PackageRef> packages;
                try
                {
                    packages = kind == "library"
                        ? LibraryManifest.Read(manifestPath).Packages
                        : Drivers(manifestPath);
                }
                catch (InvalidDataException ex) { throw new InvalidOperationException(ex.Message, ex); }
                if (packages.Count == 0) continue;

                Closure closure;
                try { closure = await LibraryResolution.RestoreAsync(targetDir, packages, nugetSource, cancellationToken, lockName, rids); }
                catch (Exception ex) when (ex is PackageResolutionException or PackageIntegrityException)
                {
                    throw new InvalidOperationException($"{kind} '{id}' cannot be restored for {platform}: {ex.Message}", ex);
                }
                if (DuckDbEngine.Needs(packages))
                {
                    var pin = DuckDbEngine.ParsePin(DuckDbEngine.ReadEmbeddedPin());
                    await DuckDbEngine.EnsureAsync(LibraryPaths.LibDir(targetDir), DuckDbEngine.ReadEmbeddedPin(), platform, Environment.GetEnvironmentVariable(DuckDbEngine.SourceVariable), cancellationToken);
                    duckDb = pin["VERSION"] + " " + platform;
                }

                // what a runner does with it: resolve from the store alone, against the pins, for the same platform
                try { await LibraryResolution.LoadAsync(targetDir, packages, cancellationToken, lockName, rids); }
                catch (Exception ex) when (ex is PackageResolutionException or PackageIntegrityException)
                {
                    throw new InvalidOperationException($"{kind} '{id}' does not load from its own store for {platform}: {ex.Message}", ex);
                }

                entries.Add(new DriverLibrariesPartEntry(
                    kind, id, [.. closure.Packages.Select(p => $"{p.Id} {p.Version}")],
                    Directory.EnumerateFiles(targetDir, "*", SearchOption.AllDirectories).Count()));
            }
        }

        var index = new DriverLibrariesPartIndex(IndexFormat, platform, duckDb, entries);
        File.WriteAllText(Path.Combine(partRoot, IndexFileName), JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }) + "\n", new UTF8Encoding(false));
        return index;
    }

    /// <summary>Reads the index of a part (or of a definition view that carries one); null when there is none.</summary>
    public static DriverLibrariesPartIndex? ReadIndex(string root)
    {
        var path = Path.Combine(root, IndexFileName);
        if (!File.Exists(path)) return null;
        try
        {
            var index = JsonSerializer.Deserialize<DriverLibrariesPartIndex>(File.ReadAllText(path));
            if (index is null || index.Format != IndexFormat)
                throw new InvalidDataException($"'{path}' has format '{index?.Format}', not '{IndexFormat}'.");
            return index;
        }
        catch (JsonException ex) { throw new InvalidDataException($"'{path}' is not a driver-libraries index: {ex.Message}", ex); }
    }

    /// <summary>The names and SHA-256 hashes of every file under <paramref name="root"/>, relative and sorted: a snapshot to compare before and after a run, to prove nothing under a view was written, added or removed.</summary>
    public static IReadOnlyList<string> Snapshot(string root) =>
        [.. Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(root, f).Replace('\\', '/') + " " + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))))
            .OrderBy(x => x, StringComparer.Ordinal)];

    private static IReadOnlyList<PackageRef> Drivers(string manifestPath)
    {
        var text = File.ReadAllText(manifestPath);
        try
        {
            var (requests, _) = ScriptDirectives.Extract(text, exactOnly: true);
            return [.. requests.Select(r => new PackageRef(r.Id, r.Range.MinVersion!.ToNormalizedString()))];
        }
        catch (ScriptDirectiveException ex) { throw new InvalidDataException($"'{manifestPath}': {ex.Message}", ex); }
    }
}
