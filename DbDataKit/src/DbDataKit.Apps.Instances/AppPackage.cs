using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Apps;

/// <summary>A package that is damaged, too large, or names a file outside itself.</summary>
public sealed class AppPackageException(string message) : Exception(message);

/// <summary>
/// What is published for an app version (plan 26): the manifest and the files the host serves, the built bundle and the declarative views (plan 22: builds are offline, so what is published is what was built). It is one piece of text
/// in the app's definition, so a version is the manifest and the files together and a changed file is a new version.
/// </summary>
public sealed class AppPackage
{
    /// <summary>The most the files of one version add up to; a larger package is refused rather than stored.</summary>
    public const long MaximumBytes = 16L * 1024 * 1024;

    public AppPackage(string manifestYaml, IReadOnlyDictionary<string, byte[]>? files = null)
    {
        ManifestYaml = manifestYaml;
        Files = files ?? new Dictionary<string, byte[]>();
        foreach (var path in Files.Keys)
            if (!IsPlain(path)) throw new AppPackageException($"'{path}' is not a path inside the app: use '/' between folders, with no '..', no leading '/' and no empty part.");
        if (Files.Values.Sum(f => (long)f.Length) > MaximumBytes) throw new AppPackageException($"The files add up to more than {MaximumBytes / (1024 * 1024)} MB.");
    }

    public string ManifestYaml { get; }

    /// <summary>The files by path relative to the app's root, with <c>/</c> between folders.</summary>
    public IReadOnlyDictionary<string, byte[]> Files { get; }

    public static bool IsPlain(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':') && path.Split('/').All(part => part.Length > 0 && part != "." && part != "..");

    /// <summary>The manifest and every file under <paramref name="directory"/> (a build's output folder, with the views beside it).</summary>
    public static AppPackage FromDirectory(string manifestYaml, string directory)
    {
        var root = Path.GetFullPath(directory);
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            files[Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/')] = File.ReadAllBytes(file);
        return new AppPackage(manifestYaml, files);
    }

    /// <summary>
    /// What <c>apps publish</c> uploads for a folder: the manifest is read from <paramref name="manifestPath"/> (default <c>app.yaml</c> in the folder) and the files are the folder's without it. Approving a folder and publishing it hash the same package.
    /// </summary>
    public static AppPackage FromFolder(string folder, string? manifestPath = null)
    {
        folder = Path.GetFullPath(folder);
        manifestPath = manifestPath is { Length: > 0 } ? Path.GetFullPath(manifestPath) : Path.Combine(folder, "app.yaml");
        var package = FromDirectory(File.ReadAllText(manifestPath), folder);
        var files = package.Files.Where(f => Path.GetFullPath(Path.Combine(folder, f.Key)) != manifestPath).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);
        return new AppPackage(package.ManifestYaml, files);
    }

    public string Serialize()
    {
        var files = new JsonObject();
        foreach (var (path, bytes) in Files.OrderBy(f => f.Key, StringComparer.Ordinal)) files[path] = Convert.ToBase64String(bytes);
        return new JsonObject { ["manifest"] = ManifestYaml, ["files"] = files }.ToJsonString();
    }

    public static AppPackage Parse(string text)
    {
        try
        {
            var node = JsonNode.Parse(text) as JsonObject ?? throw new AppPackageException("The package is not an object.");
            var files = (node["files"] as JsonObject ?? []).ToDictionary(p => p.Key, p => Convert.FromBase64String((string)p.Value!), StringComparer.Ordinal);
            return new AppPackage((string)node["manifest"]!, files);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidCastException or NullReferenceException)
        {
            throw new AppPackageException("The package is damaged: " + ex.Message);
        }
    }
}
