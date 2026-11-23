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
    /// Where the <c>checks:</c> section of <paramref name="text"/> is: from the comment lines directly above its key to the end of its last entry (the line end after it), or null when there is none or the text does not read. A comment after the
    /// last entry belongs to nothing and is not in it.
    /// </summary>
    private static (int Start, int End)? Locate(string text)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text, "", diagnostics);
        if (diagnostics.Count > 0 || root is not YamlMapping map || map.GetEntry(CheckSection.Key) is not { } entry) return null;

        var start = text.LastIndexOf('\n', Math.Max(0, entry.Key.Start - 1)) + 1;
        // the comment lines right above the key are the section's own
        while (start > 1)
        {
            var before = text.LastIndexOf('\n', start - 2) + 1;
            if (!text[before..start].TrimStart().StartsWith('#')) break;
            start = before;
        }

        var end = text.IndexOf('\n', Math.Min(entry.Value.End, text.Length));
        return (start, end < 0 ? text.Length : end);
    }

    /// <summary>The section as the file has it (comments inside it and directly above it included, line ends as <c>\n</c>), or the empty string when it has none.</summary>
    public static string Section(string text) => Locate(text) is { } at ? text[at.Start..at.End].TrimEnd('\r', '\n').Replace("\r\n", "\n") + "\n" : "";

    /// <summary>What a screen quotes when it saves: the SHA-256 (hex) of <see cref="Section"/>. A change anywhere else in the file does not change it, so a save of the section is refused only when the section itself was changed since it was read.</summary>
    public static string Version(string text) => Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Section(text))));

    /// <summary>
    /// <paramref name="newText"/> (a task the serializer just wrote, which has no checks) with the <c>checks:</c> section of <paramref name="previousText"/> added at its end, exactly as written: its comments, quoting and order stay.
    /// A previous text that has no section, or that does not read, changes nothing. The comment lines directly above the key and those inside the section are carried; a comment after its last entry belongs to nothing and is not.
    /// </summary>
    public static string Carry(string previousText, string newText)
    {
        if (Locate(previousText) is not { } at) return newText;
        var block = previousText[at.Start..at.End].TrimEnd('\r', '\n');
        var nl = newText.Contains("\r\n") ? "\r\n" : "\n";
        block = block.Replace("\r\n", "\n").Replace("\n", nl);
        return (newText.EndsWith('\n') ? newText : newText + nl) + block + nl;
    }

    /// <summary>
    /// <paramref name="previousText"/> with its <c>checks:</c> section (and the comment lines directly above its key) replaced by <paramref name="section"/> (block YAML ending in a newline; empty removes it), written at the end of the file. Everything else
    /// stays as written, with the file's own line ends. Comments inside the replaced section are not carried: a screen rewrites the whole section.
    /// </summary>
    public static string Replace(string previousText, string section)
    {
        var nl = previousText.Contains("\r\n") ? "\r\n" : "\n";
        var without = Locate(previousText) is { } at ? previousText.Remove(at.Start, at.End - at.Start).TrimEnd('\r', '\n') : previousText.TrimEnd('\r', '\n');
        // a line end the removed section left behind in the middle of the file is one blank line at most
        var kept = without.Length == 0 ? "" : without + nl;
        if (section.Length == 0) return kept;
        return (kept.Length == 0 ? "" : kept + nl) + section.Replace("\n", nl);
    }

    /// <summary>
    /// <paramref name="text"/> with its top-level <c>schemaVersion:</c> line set to the current format of a task file, the rest as written (a save writes the current format, plan 33 6d). A text with no such line is unchanged.
    /// </summary>
    public static string WithCurrentVersion(string text) =>
        System.Text.RegularExpressions.Regex.Replace(text, @"^schemaVersion:[ \t]*\d+", $"schemaVersion: {ConfigSchema.TaskCurrent}", System.Text.RegularExpressions.RegexOptions.Multiline, TimeSpan.FromSeconds(1));
}
