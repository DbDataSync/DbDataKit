using System.Text.RegularExpressions;

namespace DbDataSync.Core.Config;

/// <summary>
/// The text of a configuration file as a publish sends it (plan 33 section 6d, rule 5): the file of a kind that has a format number, in the current format, the rest as written. A replication's <c>task.yaml</c> read as format 1 leaves
/// as 2, a connection file read as <c>connection/1</c> leaves as <c>connection/2</c>; the file in the repository is not touched, so its comments and layout stay as the person wrote them. A path of no versioned kind, and a text
/// without the version line, come back unchanged.
/// </summary>
public static class PublishedConfigText
{
    /// <summary><paramref name="path"/> is the file's path under the repository (<c>config/replications/orders/task.yaml</c>), with <c>/</c> or <c>\</c>.</summary>
    public static string Current(string path, string text)
    {
        var parts = path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var at = Array.IndexOf(parts, "config");
        if (at < 0 || parts.Length - at < 3) return text;
        var kind = parts[at + 1];
        var name = parts[^1];
        if (!name.EndsWith(".yaml", StringComparison.Ordinal)) return text;
        if (kind == "replications" && parts.Length - at == 4 && name == "task.yaml") return ReplicationChecks.WithCurrentVersion(text);
        if (kind == "replications" && parts.Length - at == 5 && parts[at + 3] == "table-mappings") return WithVersion(text, ConfigSchema.MappingCurrent);
        if (kind == "scripts" && parts.Length - at == 3) return WithVersion(text, ConfigSchema.ScriptCurrent);
        if (kind == "connections" && parts.Length - at == 3) return DbDataKit.Connections.ConnectionDefinitionLoader.WithCurrentVersion(text);
        return text;
    }

    private static string WithVersion(string text, int version) =>
        Regex.Replace(text, @"^schemaVersion:[ \t]*\d+", $"schemaVersion: {version}", RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
