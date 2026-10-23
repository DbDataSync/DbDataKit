using System.IO.Compression;
using System.Security.Cryptography;
using NuGet.Packaging.Core;
using NuGet.Versioning;

namespace RoslynNuGetScripting;

/// <summary>
/// A global-packages-style cache: <c>root/&lt;id lower&gt;/&lt;version&gt;/</c> holds the extracted package, the .nupkg and its SHA-512.
/// A folder appears only by an atomic rename of a fully extracted temp folder, so a crashed download never leaves half a package.
/// </summary>
public sealed class PackageStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string DirectoryOf(string id, NuGetVersion version) => Path.Combine(Root, id.ToLowerInvariant(), version.ToNormalizedString().ToLowerInvariant());

    public IEnumerable<NuGetVersion> CachedVersions(string id)
    {
        var dir = Path.Combine(Root, id.ToLowerInvariant());
        if (!Directory.Exists(dir)) yield break;
        foreach (var sub in Directory.EnumerateDirectories(dir))
            if (NuGetVersion.TryParse(Path.GetFileName(sub), out var v) && File.Exists(Path.Combine(sub, FileName(id, v) + ".sha512"))) yield return v;
    }

    static string FileName(string id, NuGetVersion v) => $"{id.ToLowerInvariant()}.{v.ToNormalizedString().ToLowerInvariant()}.nupkg";

    /// <summary>Re-hashes the stored .nupkg and compares it with the recorded hash (and with a pinned one, if given).</summary>
    public string Verify(string id, NuGetVersion version, string? pinnedHash)
    {
        var dir = DirectoryOf(id, version);
        var nupkg = Path.Combine(dir, FileName(id, version));
        var recorded = File.ReadAllText(nupkg + ".sha512").Trim();
        var actual = HashOf(nupkg);
        if (actual != recorded) throw new InvalidDataException($"{id} {version}: the cached package does not match its recorded SHA-512 ({nupkg})");
        if (pinnedHash is not null && pinnedHash != actual) throw new InvalidDataException($"{id} {version}: SHA-512 {actual} is not the pinned {pinnedHash}");
        return actual;
    }

    /// <summary>Stores a downloaded .nupkg (already written to <paramref name="downloaded"/>) and extracts it.</summary>
    public string Add(PackageIdentity identity, string downloaded, string? pinnedHash)
    {
        var id = identity.Id;
        var version = identity.Version;
        var hash = HashOf(downloaded);
        if (pinnedHash is not null && pinnedHash != hash) throw new InvalidDataException($"{id} {version}: downloaded SHA-512 {hash} is not the pinned {pinnedHash}");
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

    static string HashOf(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToBase64String(SHA512.HashData(stream));
    }

    static void Extract(string nupkg, string target)
    {
        var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(nupkg);
        foreach (var entry in zip.Entries)
        {
            // Entry names in a .nupkg are URL-encoded; the OPC bookkeeping files are not package content.
            var name = Uri.UnescapeDataString(entry.FullName);
            if (name.EndsWith('/') || name.StartsWith("_rels/") || name.StartsWith("package/") || name == "[Content_Types].xml") continue;
            var path = Path.GetFullPath(Path.Combine(target, name));
            // A hostile package must not be able to write outside its own folder ("..", absolute paths).
            if (!path.StartsWith(root, StringComparison.Ordinal)) throw new InvalidDataException($"Package entry escapes its folder: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path);
        }
    }
}
