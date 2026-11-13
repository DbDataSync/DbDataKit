using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataSync.Libraries;

/// <summary>One file of a definition as it goes into a replication set's package: where it goes inside it (<c>repo/...</c>) and where it is now.</summary>
public sealed record DefinitionFile(string Path, string Source);

/// <summary>
/// The files a replication set publishes (plan 28c E08, E12g): everything of <c>config/</c>, <c>libraries/</c> (their scripts and pins), <c>drivers/</c> and <c>files/</c> except what is local to a machine (<c>secrets/</c>, <c>.git</c>, the
/// state, the script cache) and a library's or driver's own <c>lib/</c>, which is rebuilt for the platform, under <see cref="RepoDir"/> in the package. One list for the package builder and for the "unpublished changes" check, so the two cannot
/// disagree about what a publish holds.
/// </summary>
public static class DefinitionFiles
{
    /// <summary>Where in the package the definition is: the <c>repo_dir</c> input of the drain.</summary>
    public const string RepoDir = "repo";

    private static readonly string[] SkippedDirectories = [".git", "secrets", "state", "script-cache", "node_modules"];

    public static IReadOnlyList<DefinitionFile> Enumerate(string repoRoot)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var top in new[] { "config", "libraries", "drivers", "files" })
        {
            var root = Path.Combine(repoRoot, top);
            if (!Directory.Exists(root)) continue;
            foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
                var parts = relative.Split('/');
                if (parts.Take(parts.Length - 1).Any(p => SkippedDirectories.Contains(p, StringComparer.OrdinalIgnoreCase))) continue;
                if (top is "libraries" or "drivers" && parts.Length > 3 && parts[2] == LibraryPaths.LibDirName) continue;
                files[RepoDir + "/" + relative] = path;
            }
        }

        return [.. files.Select(f => new DefinitionFile(f.Key, f.Value))];
    }
}

/// <summary>What a definition holds, as digests: the files every replication shares (connections, libraries, drivers, files) and, per replication, the files of its own folder <c>config/replications/&lt;name&gt;/</c>.</summary>
public sealed record DefinitionDigest(string Shared, IReadOnlyDictionary<string, string> Replications)
{
    private const string ReplicationsPrefix = DefinitionFiles.RepoDir + "/config/replications/";

    private static readonly string[] TextExtensions = [".yml", ".yaml", ".sql", ".csx", ".json", ".md", ".txt", ".lock"];

    /// <summary>The digest of the working tree in <paramref name="repoRoot"/>; text files are read with LF line endings, as the package writes them, so a checkout's line endings are not a change.</summary>
    public static DefinitionDigest Of(string repoRoot)
    {
        var shared = new List<string>();
        var perReplication = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var file in DefinitionFiles.Enumerate(repoRoot))
        {
            var line = $"{file.Path}\t{HashOf(file.Source, file.Path)}";
            if (file.Path.StartsWith(ReplicationsPrefix, StringComparison.Ordinal) && file.Path[ReplicationsPrefix.Length..].Split('/', 2) is [var name, _])
            {
                if (!perReplication.TryGetValue(name, out var list)) perReplication[name] = list = [];
                list.Add(line);
            }
            else shared.Add(line);
        }

        return new DefinitionDigest(Combine(shared), perReplication.ToDictionary(p => p.Key, p => Combine(p.Value), StringComparer.Ordinal));
    }

    private static string HashOf(string source, string path)
    {
        var bytes = File.ReadAllBytes(source);
        if (TextExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase) && !bytes.Contains((byte)0) && bytes.Contains((byte)'\r'))
            bytes = new UTF8Encoding(false).GetBytes(new UTF8Encoding(false).GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal));
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string Combine(List<string> lines) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines))));
}

/// <summary>What a replication set looked like when it was last published to a runner: written by <c>package publish</c> (the command line and the Sync API's publish action run the same verb).</summary>
public sealed record PublishedMarker(string Package, string Channel, string Server, int? Version, string Mode, DateTimeOffset PublishedAtUtc, DefinitionDigest Digest);

/// <summary>Where a replication set stands against what was last published (plan 28c E12g, E22 precondition P9): a publish is explicit, so an edited replication runs its old definition on the runner until somebody publishes; this says so.</summary>
/// <param name="EverPublished">A publish of this package and channel was made from this repository.</param>
/// <param name="Unpublished">Something changed since: <see cref="Replications"/> and <see cref="SharedChanged"/> say what.</param>
/// <param name="Replications">The replications whose own files changed, were added or were removed since the last publish (a replication added or removed is in the list too).</param>
/// <param name="SharedChanged">The connections, libraries, drivers or files, which every replication uses, changed.</param>
public sealed record PublishState(bool EverPublished, bool Unpublished, bool SharedChanged, IReadOnlyList<string> Replications, DateTimeOffset? PublishedAtUtc, int? Version, string? Mode);

/// <summary>The marker file <c>&lt;repo&gt;/state/runner-published.json</c> (the <c>state</c> folder never goes into a package), written after a successful publish and compared with the working tree.</summary>
public static class PublishedDefinition
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string PathOf(string repoRoot) => Path.Combine(repoRoot, "state", "runner-published.json");

    /// <summary>Records that <paramref name="digest"/> (taken before the package was built, so an edit made while it was sent counts as unpublished) was published as <paramref name="version"/>.</summary>
    public static void Record(string repoRoot, PublishedMarker marker)
    {
        var path = PathOf(repoRoot);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(marker, Json), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The marker, or null when none was written or it does not read (a marker that does not read is "never published", which only shows the indicator).</summary>
    public static PublishedMarker? Read(string repoRoot)
    {
        try { return File.Exists(PathOf(repoRoot)) ? JsonSerializer.Deserialize<PublishedMarker>(File.ReadAllText(PathOf(repoRoot)), Json) : null; }
        catch (Exception ex) when (ex is JsonException or IOException) { return null; }
    }

    /// <summary>The state of the tree in <paramref name="repoRoot"/> against the last publish to <paramref name="package"/> in <paramref name="channel"/>.</summary>
    public static PublishState StateOf(string repoRoot, string package, string channel)
    {
        if (Read(repoRoot) is not { } marker || marker.Package != package || marker.Channel != channel)
            return new PublishState(false, false, false, [], null, null, null);
        var now = DefinitionDigest.Of(repoRoot);
        var sharedChanged = now.Shared != marker.Digest.Shared;
        var changed = now.Replications.Keys.Union(marker.Digest.Replications.Keys, StringComparer.Ordinal)
            .Where(name => !now.Replications.TryGetValue(name, out var a) || !marker.Digest.Replications.TryGetValue(name, out var b) || a != b)
            .Order(StringComparer.Ordinal).ToList();
        return new PublishState(true, sharedChanged || changed.Count > 0, sharedChanged, changed, marker.PublishedAtUtc, marker.Version, marker.Mode);
    }
}
