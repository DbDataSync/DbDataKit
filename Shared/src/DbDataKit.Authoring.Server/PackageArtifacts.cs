using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.Authoring;

namespace DbDataKit.Authoring.Server;

/// <summary>What the kinds that publish a <c>package</c> to a Runner share (plan 33 SC20d, SC22b): the package's name from a project's id and what the product's own program printed about the package it wrote.</summary>
public static partial class PackageArtifacts
{
    [GeneratedRegex("[^a-z0-9_-]", RegexOptions.NonBacktracking)]
    private static partial Regex NotAllowed();

    /// <summary>The package a project becomes on a Runner: its id as the Runner's name rule writes it (lower-case letters, digits, <c>-</c> and <c>_</c>, starting with a letter or digit, at most 64).</summary>
    public static string NameOf(string projectId)
    {
        var name = NotAllowed().Replace(projectId.ToLowerInvariant(), "-").TrimStart('-', '_');
        if (name.Length == 0) name = "project";
        return name.Length > 64 ? name[..64] : name;
    }

    /// <summary>
    /// The artifact of a program that wrote a package into <paramref name="folder"/> (<c>package.yaml</c> and <c>files/</c>) and printed <paramref name="output"/>, a JSON document: the package, the number of files and the tasks, which the product
    /// names in <paramref name="listKey"/> (Sync's <c>replications</c> each with a <c>task</c>) or in <c>tasks</c> (Build's, each with a <c>name</c>); a Build document wraps them in <c>data</c>.
    /// </summary>
    public static ProjectArtifact FromOutput(string package, string folder, string output, string listKey)
    {
        if (!File.Exists(Path.Combine(folder, "package.yaml")))
            return new(false, "The program said it wrote the package but package.yaml is not there.", [new("error", "DBD-WS-PACKAGE", "The program said it wrote the package but package.yaml is not there.")]);
        var tasks = new List<string>();
        var files = 0;
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object) root = data;
            if (root.ValueKind == JsonValueKind.Object)
            {
                if (root.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Number) files = f.GetInt32();
                foreach (var key in new[] { listKey, "tasks" })
                    if (root.TryGetProperty(key, out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var item in list.EnumerateArray())
                            if ((item.ValueKind == JsonValueKind.Object ? (item.TryGetProperty("task", out var t) ? t : item.TryGetProperty("name", out var n) ? n : default) : default) is { ValueKind: JsonValueKind.String } text)
                                tasks.Add(text.GetString()!);
            }
        }
        catch (JsonException) { /* the package is on disk; what the program said about it is a convenience */ }
        if (files == 0) files = Directory.Exists(Path.Combine(folder, "files")) ? Directory.EnumerateFiles(Path.Combine(folder, "files"), "*", SearchOption.AllDirectories).Count() : 0;
        var info = new Dictionary<string, string> { ["package"] = package, ["tasks"] = string.Join(", ", tasks.Distinct()), ["files"] = files.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        return new(true, $"The package {package}: {tasks.Distinct().Count()} task(s), {files} file(s).", [], info);
    }
}
