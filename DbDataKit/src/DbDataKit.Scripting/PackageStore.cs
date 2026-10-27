using System.IO.Compression;
using System.Security.Cryptography;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace DbDataKit.Scripting;

/// <summary>A package was not what it was recorded or pinned to be, or was built to write outside its own folder.</summary>
public sealed class PackageIntegrityException(string message) : Exception(message);

/// <summary>
/// A global-packages-style cache: <c>root/&lt;id lower&gt;/&lt;version&gt;/</c> holds the extracted package, the .nupkg and its SHA-512. A folder appears only by an atomic rename of a fully extracted temp folder, so a crashed
/// download never leaves half a package and two processes racing for one package lose gracefully. Nothing is ever extracted outside the package's own folder.
/// </summary>
public sealed class PackageStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    /// <summary>Where a service keeps its cache by default: under the product's data directory (<c>ProgramData</c> on Windows), never <c>%TEMP%</c> and never a user profile, which a service account may not have.</summary>
    public static string DefaultRoot(string product) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), product, "packages");

    public string DirectoryOf(string id, NuGetVersion version) => Path.Combine(Root, id.ToLowerInvariant(), version.ToNormalizedString().ToLowerInvariant());

    public IEnumerable<NuGetVersion> CachedVersions(string id)
    {
        var dir = Path.Combine(Root, id.ToLowerInvariant());
        if (!Directory.Exists(dir)) yield break;
        foreach (var sub in Directory.EnumerateDirectories(dir))
            if (NuGetVersion.TryParse(Path.GetFileName(sub), out var v) && File.Exists(Path.Combine(sub, FileName(id, v) + ".sha512"))) yield return v;
    }

    private static string FileName(string id, NuGetVersion v) => $"{id.ToLowerInvariant()}.{v.ToNormalizedString().ToLowerInvariant()}.nupkg";

    /// <summary>Re-hashes the stored .nupkg and compares it with the recorded hash (and with a pinned one, if given). Returns the hash.</summary>
    public string Verify(string id, NuGetVersion version, string? pinnedHash)
    {
        var nupkg = Path.Combine(DirectoryOf(id, version), FileName(id, version));
        var recorded = File.ReadAllText(nupkg + ".sha512").Trim();
        var actual = HashOf(nupkg);
        if (actual != recorded) throw new PackageIntegrityException($"{id} {version}: the cached package does not match its recorded SHA-512, so it was changed after it was stored");
        if (pinnedHash is not null && pinnedHash != actual) throw new PackageIntegrityException($"{id} {version}: its SHA-512 is not the one pinned in the lock file");
        return actual;
    }

    /// <summary>Stores a downloaded .nupkg (already written to <paramref name="downloaded"/>) and extracts it. Returns its hash.</summary>
    public string Add(PackageIdentity identity, string downloaded, string? pinnedHash)
    {
        var (id, version) = (identity.Id, identity.Version);
        var hash = HashOf(downloaded);
        if (pinnedHash is not null && pinnedHash != hash) throw new PackageIntegrityException($"{id} {version}: the downloaded package's SHA-512 is not the one pinned in the lock file");
        var final = DirectoryOf(id, version);
        var temp = final + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temp);
        try
        {
            Extract(downloaded, temp);
            File.Copy(downloaded, Path.Combine(temp, FileName(id, version)));
            File.WriteAllText(Path.Combine(temp, FileName(id, version) + ".sha512"), hash);
            Directory.CreateDirectory(Path.GetDirectoryName(final)!);
            try { Directory.Move(temp, final); }
            catch (IOException) when (Directory.Exists(final)) { /* another process won the race; its copy is equivalent */ }
        }
        finally
        {
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }
        return hash;
    }

    public static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToBase64String(SHA512.HashData(stream));
    }

    private static void Extract(string nupkg, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(nupkg);
        foreach (var entry in zip.Entries)
        {
            // Entry names in a .nupkg are URL-encoded; the OPC bookkeeping files are not package content.
            var name = Uri.UnescapeDataString(entry.FullName);
            if (name.EndsWith('/') || name.StartsWith("_rels/", StringComparison.Ordinal) || name.StartsWith("package/", StringComparison.Ordinal) || name == "[Content_Types].xml") continue;
            var path = Path.GetFullPath(Path.Combine(target, name));
            // A hostile package must not be able to write outside its own folder ("..", absolute paths, a drive letter).
            if (!path.StartsWith(root, StringComparison.Ordinal) || Path.IsPathRooted(name)) throw new PackageIntegrityException($"A package entry would be written outside the package's own folder: {Printable(entry.FullName)}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
    }

    private static string Printable(string s) => s.Length > 80 ? string.Concat(s.Take(80).Select(c => char.IsControl(c) ? '?' : c)) + "..." : string.Concat(s.Select(c => char.IsControl(c) ? '?' : c));
}
