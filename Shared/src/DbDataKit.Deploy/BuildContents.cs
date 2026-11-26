using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Authoring;

namespace DbDataKit.Deploy;

/// <summary>One file of a build, by its path in the build's folder (forward slashes), its SHA-256 (lower-case hex) and its size.</summary>
public sealed record BuildFile(string Path, string Sha256, long Size);

/// <summary>
/// One thing a build holds to send, by a route of its kind (<see cref="ProjectPublishRoute"/>): its id (<c>package</c>, <c>semantic-model</c>, <c>model</c>, <c>model-package/&lt;model&gt;</c>, <c>app</c>), the product it goes to, what
/// arrives there, the model it is of (a Store build's packages), the folder in the build that a deploy reads it from (<see cref="Root"/>), its files, and <see cref="ManifestSha256"/>: the SHA-256 of its list of files (each file's path
/// under <see cref="Root"/>, a tab, its SHA-256 and a line feed, in path order), which names this content whatever build made it.
/// </summary>
public sealed record BuildItem(string Id, string Product, string What, string? Model, string Root, IReadOnlyList<BuildFile> Files, string ManifestSha256)
{
    public JsonObject ToJson() => new()
    {
        ["id"] = Id, ["product"] = Product, ["what"] = What, ["model"] = Model, ["root"] = Root, ["manifest_sha256"] = ManifestSha256,
        ["files"] = new JsonArray([.. Files.Select(f => (JsonNode)new JsonObject { ["path"] = f.Path, ["sha256"] = f.Sha256, ["size"] = f.Size })]),
    };

    /// <summary>An item as <see cref="ToJson"/> wrote it; a part that is missing or not of its form is a <see cref="DeployException"/> naming it.</summary>
    public static BuildItem FromJson(JsonObject json)
    {
        static string Text(JsonObject o, string field) => o[field] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0 ? s : throw new DeployException($"The Workspace server's description of the build has no \"{field}\".", 502);
        var files = (json["files"] as JsonArray ?? throw new DeployException("The Workspace server's description of the build has no \"files\".", 502)).OfType<JsonObject>()
            .Select(f => new BuildFile(BuildContents.SafePath(Text(f, "path")), Text(f, "sha256"), f["size"] is JsonValue size && size.TryGetValue<long>(out var n) ? n : throw new DeployException("A file of the build has no size.", 502))).ToList();
        return new BuildItem(Text(json, "id"), Text(json, "product"), Text(json, "what"), json["model"] is JsonValue m && m.TryGetValue<string>(out var model) ? model : null,
            BuildContents.SafePath(Text(json, "root")), files, Text(json, "manifest_sha256"));
    }
}

/// <summary>
/// What a build holds to send, read from the build's folder by its kind's routes: a Runner package (<c>artifact/package.yaml</c> and <c>artifact/files/</c>), a Build project's semantic models and views (<c>artifact/gateway/</c>), a Store
/// project's models (<c>artifact/models/</c>) and one deploy package per model (<c>artifact/runner/&lt;model&gt;/</c>), and an app (<c>app/</c>). A route that made nothing in this build has no item. The Workspace server lists them for the
/// control service, which fetches each file by its hash, lays the files out under their paths again and deploys the item's <see cref="BuildItem.Root"/>.
/// Design: docs/publishing.md#deploying-a-build-from-the-control-service
/// </summary>
public static class BuildContents
{
    /// <summary>The folders of a Build project's views under its models folder, and the definition kind each holds: what a build copies and a deploy sends after the models.</summary>
    public static readonly IReadOnlyList<(string Folder, string Kind)> ViewFolders = [("dashboards", "dashboard"), ("reports", "report")];

    /// <summary>The items of a build of a kind with <paramref name="routes"/>, whose folder is <paramref name="buildFolder"/> (it holds <c>artifact/</c>, or <c>app/</c> for an app).</summary>
    public static IReadOnlyList<BuildItem> Of(IReadOnlyList<ProjectPublishRoute> routes, string buildFolder)
    {
        var items = new List<BuildItem>();
        foreach (var route in routes)
            switch (route.What)
            {
                case "package":
                    Add(items, "package", route, null, "artifact", buildFolder, ["package.yaml"], ["files"]);
                    break;
                case "semantic-model":
                    Add(items, "semantic-model", route, null, "artifact", buildFolder, [], ["gateway"]);
                    break;
                case "model":
                    Add(items, "model", route, null, "artifact", buildFolder, [], ["models"]);
                    break;
                case "model-package":
                    var runner = Path.Combine(buildFolder, "artifact", "runner");
                    if (Directory.Exists(runner))
                        foreach (var model in Directory.EnumerateDirectories(runner).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal))
                            Add(items, "model-package/" + model, route, model, "artifact/runner/" + model, buildFolder, ["package.yaml"], ["files"]);
                    break;
                case "app":
                    Add(items, "app", route, null, "app", buildFolder, [], [""]);
                    break;
            }
        return items;
    }

    private static void Add(List<BuildItem> items, string id, ProjectPublishRoute route, string? model, string root, string buildFolder, string[] files, string[] folders)
    {
        var rootFolder = Path.Combine(buildFolder, root);
        var found = new List<string>();
        foreach (var file in files)
            if (File.Exists(Path.Combine(rootFolder, file))) found.Add(Path.Combine(rootFolder, file));
        foreach (var folder in folders)
        {
            var under = folder.Length == 0 ? rootFolder : Path.Combine(rootFolder, folder);
            if (Directory.Exists(under)) found.AddRange(Directory.EnumerateFiles(under, "*", SearchOption.AllDirectories));
        }
        if (found.Count == 0) return;
        var listed = found.Distinct(StringComparer.Ordinal).Select(f =>
        {
            using var stream = File.OpenRead(f);
            return new BuildFile(Path.GetRelativePath(buildFolder, f).Replace(Path.DirectorySeparatorChar, '/'), Convert.ToHexStringLower(SHA256.HashData(stream)), stream.Length);
        }).OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
        items.Add(new BuildItem(id, route.Product, route.What, model, root, listed, ManifestSha256(root, listed)));
    }

    /// <summary>The SHA-256 of an item's list of files: each path under <paramref name="root"/>, a tab, its SHA-256 and a line feed, in path order.</summary>
    public static string ManifestSha256(string root, IEnumerable<BuildFile> files)
    {
        var prefix = root.Length == 0 ? "" : root.TrimEnd('/') + "/";
        var text = new StringBuilder();
        foreach (var file in files.OrderBy(f => f.Path, StringComparer.Ordinal))
            text.Append(file.Path.StartsWith(prefix, StringComparison.Ordinal) ? file.Path[prefix.Length..] : file.Path).Append('\t').Append(file.Sha256).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }

    /// <summary>A path of a build as the Workspace server named it, refused when it could leave the folder it is laid out in (absolute, <c>..</c>, a backslash, an empty part).</summary>
    public static string SafePath(string path)
    {
        if (path.Length == 0 || path.StartsWith('/') || path.Contains('\\') || path.Contains(':') || path.Split('/').Any(p => p is "" or "." or ".."))
            throw new DeployException($"The Workspace server named a file of the build '{path}', which is not a path inside the build.", 502);
        return path;
    }

    /// <summary>
    /// Lays the files of <paramref name="item"/> out under <paramref name="folder"/> at their paths, reading each by its hash with <paramref name="read"/>; a file whose bytes do not have its SHA-256 or its size is a
    /// <see cref="DeployException"/> naming it, so what is deployed is exactly what the build listed. Returns the folder the item is deployed from.
    /// </summary>
    public static async Task<string> FetchAsync(BuildItem item, string folder, Func<string, CancellationToken, Task<byte[]>> read, CancellationToken ct = default)
    {
        if (BuildContents.ManifestSha256(item.Root, item.Files) != item.ManifestSha256)
            throw new DeployException($"The list of files of {item.Id} does not match its hash {item.ManifestSha256}.", 502);
        foreach (var file in item.Files)
        {
            var bytes = await read(file.Sha256, ct);
            if (bytes.LongLength != file.Size || Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
                throw new DeployException($"The file {file.Path} the Workspace server sent is not the one the build lists (SHA-256 {file.Sha256}).", 502);
            var path = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes, ct);
        }
        return Path.Combine(folder, item.Root.Replace('/', Path.DirectorySeparatorChar));
    }
}
