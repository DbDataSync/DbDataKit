using DbDataKit.Config.Yaml;
using DbDataKit.Quality;

namespace DbDataSync.Core.Config;

/// <summary>
/// The <c>checks:</c> section of a replication's <c>task.yaml</c> (plan 23a FQ04, format 2 of Sync's files). The strict check (<see cref="StrictConfigFile"/>) already reported every fault of the section with file and line; this reads what is valid
/// and carries the section across a save.
/// </summary>
internal static class ReplicationChecks
{
    /// <summary>The checks of a task file's text; empty when it has none. A fault is a <see cref="ConfigFileException"/> naming the file and line (the same one the strict check throws).</summary>
    public static IReadOnlyList<CheckDefinition> Read(string text, string file)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text, file, diagnostics);
        if (diagnostics.Count == 0 && root is YamlMapping map && map.GetEntry(CheckSection.Key) is { } entry)
        {
            var checks = CheckSection.Read(entry.Value, file, diagnostics);
            if (diagnostics.Count == 0) return checks ?? [];
        }
        if (diagnostics.Count > 0) throw new ConfigFileException(file, diagnostics);
        return [];
    }

    /// <summary>
    /// <paramref name="newText"/> (a task the serializer just wrote, which has no checks) with the <c>checks:</c> section of <paramref name="previousText"/> added at its end, exactly as written: its comments, quoting and order stay.
    /// A previous text that has no section, or that does not read, changes nothing. The comment lines directly above the key and those inside the section are carried; a comment after its last entry belongs to nothing and is not.
    /// </summary>
    public static string Carry(string previousText, string newText)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(previousText, "", diagnostics);
        if (diagnostics.Count > 0 || root is not YamlMapping map || map.GetEntry(CheckSection.Key) is not { } entry) return newText;

        var start = previousText.LastIndexOf('\n', Math.Max(0, entry.Key.Start - 1)) + 1;
        // the comment lines right above the key are the section's own
        while (start > 1)
        {
            var before = previousText.LastIndexOf('\n', start - 2) + 1;
            if (!previousText[before..start].TrimStart().StartsWith('#')) break;
            start = before;
        }

        var end = previousText.IndexOf('\n', Math.Min(entry.Value.End, previousText.Length));
        var block = previousText[start..(end < 0 ? previousText.Length : end)].TrimEnd('\r', '\n');

        var nl = newText.Contains("\r\n") ? "\r\n" : "\n";
        block = block.Replace("\r\n", "\n").Replace("\n", nl);
        return (newText.EndsWith('\n') ? newText : newText + nl) + block + nl;
    }
}
