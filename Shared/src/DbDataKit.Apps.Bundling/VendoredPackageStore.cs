using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataKit.Apps.Bundling;

/// <summary>The store cannot be used: its manifest does not read, or a file differs from it. The message names the file.</summary>
public sealed class VendoredStoreException(string message) : Exception(message);

/// <summary>
/// The vendored package store of the app build (plan 22a GP33, QG12): a folder with <c>store.json</c> and <c>node_modules/</c>, made by <c>Shared/web/app-store/build.mjs</c> from packages an administrator installed where a network is. <c>store.json</c> lists
/// every file under <c>node_modules</c> with its SHA-256, the packages by name and version, and the <b>library</b>: the package names an app may import with no allow-list entry. A build copies the store into a folder of its own, checks the copy
/// against the list, and only then runs the bundler, so a changed, added or missing file is refused by name and a change made between the check and the build is not possible.
/// <para>What the hashes protect: the files named in <c>store.json</c>. What protects <c>store.json</c> itself is the file system (who may write the folder) and, when the administrator sets it, the digest of the file (<c>apps.packageStoreSha256</c>).</para>
/// </summary>
public sealed partial class VendoredPackageStore
{
    public const string ManifestName = "store.json";
    public const int SchemaVersion = 1;

    /// <summary>Most problems <see cref="Verify"/> lists; a store with more is as refused as one with one.</summary>
    private const int MaxProblems = 20;

    private readonly Dictionary<string, string> _files;
    private readonly Dictionary<string, string> _versions;

    private VendoredPackageStore(string folder, string digest, IReadOnlyList<string> library, Dictionary<string, string> versions, Dictionary<string, string> files)
    {
        Folder = folder;
        Digest = digest;
        Library = library;
        _versions = versions;
        _files = files;
    }

    /// <summary>The folder that holds <c>store.json</c> and <c>node_modules</c>.</summary>
    public string Folder { get; }

    /// <summary>SHA-256 (hex, lower case) of <c>store.json</c> as read.</summary>
    public string Digest { get; }

    /// <summary>The names an app may import without an allow-list entry.</summary>
    public IReadOnlyList<string> Library { get; }

    /// <summary>The packages at the top of the store's <c>node_modules</c>, by name, with the version each has.</summary>
    public IReadOnlyDictionary<string, string> Packages => _versions.Where(p => !p.Key.Contains("/node_modules/", StringComparison.Ordinal)).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

    public int FileCount => _files.Count;

    /// <summary>Reads <c>store.json</c> of <paramref name="folder"/>. Refuses a manifest that does not read (naming the file and what is wrong) and, when <paramref name="expectedDigest"/> is given, one whose digest is another.</summary>
    public static VendoredPackageStore Load(string folder, string? expectedDigest = null)
    {
        var path = Path.Combine(folder, ManifestName);
        if (!Directory.Exists(folder)) throw new VendoredStoreException($"The package store {folder} does not exist. Make one with Shared/web/app-store/build.mjs and set apps.packageStore to it.");
        if (!File.Exists(path)) throw new VendoredStoreException($"{path} does not exist: a package store is a folder with {ManifestName} and node_modules (Shared/web/app-store/build.mjs makes one).");
        var bytes = File.ReadAllBytes(path);
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (expectedDigest is { Length: > 0 } && !string.Equals(expectedDigest.Trim(), digest, StringComparison.OrdinalIgnoreCase))
            throw new VendoredStoreException($"{path} has the digest {digest}, not the {expectedDigest.Trim()} that apps.packageStoreSha256 names: the store's manifest was changed or replaced.");
        try
        {
            using var document = JsonDocument.Parse(bytes);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new VendoredStoreException($"{path}: the file is not a JSON object.");
            var version = root.TryGetProperty("schemaVersion", out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
            if (version != SchemaVersion) throw new VendoredStoreException($"{path}: schemaVersion is {(version == 0 ? "missing" : version.ToString())}; this program reads {SchemaVersion}. Make the store again with this release's Shared/web/app-store/build.mjs.");
            var library = Strings(root, "library", path);
            var versions = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!root.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Object) throw new VendoredStoreException($"{path}: there is no \"packages\" object.");
            foreach (var package in packages.EnumerateObject())
                versions[Relative(package.Name, path)["node_modules/".Length..]] = package.Value.TryGetProperty("version", out var pv) && pv.ValueKind == JsonValueKind.String ? pv.GetString()! : "";
            var files = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!root.TryGetProperty("files", out var listed) || listed.ValueKind != JsonValueKind.Object) throw new VendoredStoreException($"{path}: there is no \"files\" object.");
            foreach (var file in listed.EnumerateObject())
            {
                var hash = file.Value.ValueKind == JsonValueKind.String ? file.Value.GetString()! : "";
                if (!Sha256Text().IsMatch(hash)) throw new VendoredStoreException($"{path}: the hash of {file.Name} is not a SHA-256 (64 hexadecimal digits).");
                files[Relative(file.Name, path)] = hash.ToLowerInvariant();
            }
            foreach (var name in library)
                if (!versions.ContainsKey(name)) throw new VendoredStoreException($"{path}: the library names {name}, which is not among the store's packages.");
            return new VendoredPackageStore(Path.GetFullPath(folder), digest, library, versions, files);
        }
        catch (JsonException e)
        {
            throw new VendoredStoreException($"{path}: not valid JSON ({e.Message}).");
        }
    }

    /// <summary>Files of the store are <c>node_modules/...</c> with forward slashes and no way out of the folder.</summary>
    private static string Relative(string name, string manifestPath)
    {
        if (!name.StartsWith("node_modules/", StringComparison.Ordinal) || name.Contains('\\') || name.Split('/').Any(p => p is "" or "." or ".."))
            throw new VendoredStoreException($"{manifestPath}: '{name}' is not a path under node_modules.");
        return name;
    }

    private static List<string> Strings(JsonElement root, string name, string path)
    {
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) throw new VendoredStoreException($"{path}: there is no \"{name}\" list.");
        return array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : throw new VendoredStoreException($"{path}: \"{name}\" holds something that is not text.")).ToList();
    }

    /// <summary>Compares the files under <paramref name="root"/> (default: the store's own folder) with the manifest: every problem is one line naming the file. Empty when they agree.</summary>
    public IReadOnlyList<string> Verify(string? root = null)
    {
        root ??= Folder;
        var problems = new List<string>();
        void Add(string problem) { if (problems.Count < MaxProblems) problems.Add(problem); }
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var modules = Path.Combine(root, "node_modules");
        if (Directory.Exists(modules))
            foreach (var path in Directory.EnumerateFileSystemEntries(modules, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
                var info = new FileInfo(path);
                if (info.LinkTarget is not null) { Add($"{relative} is a link; the store holds files only."); continue; }
                if (Directory.Exists(path)) continue;
                seen.Add(relative);
                if (!_files.TryGetValue(relative, out var expected)) Add($"{relative} is not in {ManifestName}: a file was added.");
                else if (!string.Equals(Hash(path), expected, StringComparison.Ordinal)) Add($"{relative} differs from {ManifestName} (its SHA-256 is not the recorded one): the file was changed.");
            }
        foreach (var file in _files.Keys.Order(StringComparer.Ordinal))
            if (!seen.Contains(file)) Add($"{file} is listed in {ManifestName} and is missing.");
        return problems;
    }

    /// <summary>Copies the store's files into <paramref name="destination"/> (<c>node_modules/...</c>) and leaves its own folder as it was.</summary>
    public void CopyTo(string destination)
    {
        foreach (var file in _files.Keys)
        {
            var target = Path.Combine(destination, file.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var source = Path.Combine(Folder, file.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(source)) File.Copy(source, target);   // a missing file is not copied; verifying the copy names it
        }
    }

    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256Text();
}
