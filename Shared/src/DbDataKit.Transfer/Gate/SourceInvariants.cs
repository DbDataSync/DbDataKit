using System.Text.RegularExpressions;

namespace DbDataKit.Transfer;

/// <summary>
/// The source-scanning invariant pattern (Build's GateInvariantTests) as a reusable helper: "only these files may touch driver
/// command APIs". A product test calls <see cref="FindOffenders"/> over its source root and asserts the list is empty.
/// </summary>
public static partial class SourceInvariants
{
    /// <summary>Calls that create or run a command, or open a bulk route. Writes must reach these only through the gate.</summary>
    public static readonly Regex DriverCalls = DriverCallsRegex();

    [GeneratedRegex(@"\b(ExecuteNonQuery(Async)?|ExecuteScalar(Async)?|ExecuteReader(Async)?|CreateCommand|SqlCommand|NpgsqlCommand|MySqlCommand|OracleCommand|DbCommand|NpgsqlBatch|SqlBatch|DbBatch|SqlBulkCopy|NpgsqlBinaryImporter|BeginBinaryImport(Async)?|BeginBinaryExport(Async)?|BeginTextImport(Async)?|BeginTextExport(Async)?|BeginRawBinaryCopy(Async)?|MySqlBulkCopy|OracleBulkCopy)\b")]
    private static partial Regex DriverCallsRegex();

    /// <summary>Walks up from <paramref name="startDir"/> to the first directory containing <paramref name="marker"/> (a file or directory name).</summary>
    public static string? FindRoot(string startDir, string marker)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, marker)) || Directory.Exists(Path.Combine(dir.FullName, marker))) return dir.FullName;
        return null;
    }

    /// <summary>
    /// Returns the relative paths (forward slashes) of <c>*.cs</c> files under <paramref name="sourceRoot"/> whose code (comments and string literals removed)
    /// matches <paramref name="pattern"/>, except files whose relative path equals or starts with an entry of <paramref name="allowed"/>.
    /// </summary>
    public static IReadOnlyList<string> FindOffenders(string sourceRoot, Regex pattern, IEnumerable<string> allowed)
    {
        var allow = allowed.Select(a => a.Replace('\\', '/')).ToList();
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceRoot, file).Replace('\\', '/');
            if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.StartsWith("obj/") || rel.StartsWith("bin/")) continue;
            if (allow.Any(a => rel == a || rel.StartsWith(a, StringComparison.Ordinal))) continue;
            if (pattern.IsMatch(StripCommentsAndStrings(File.ReadAllText(file)))) offenders.Add(rel);
        }
        offenders.Sort(StringComparer.Ordinal);
        return offenders;
    }

    /// <summary>Removes comments and string/char literals so a word in documentation or a message is not a call. Good enough for an invariant test, not a parser.</summary>
    public static string StripCommentsAndStrings(string code)
    {
        var sb = new System.Text.StringBuilder(code.Length);
        for (var i = 0; i < code.Length; i++)
        {
            var c = code[i];
            var next = i + 1 < code.Length ? code[i + 1] : '\0';
            if (c == '/' && next == '/') { while (i < code.Length && code[i] != '\n') i++; sb.Append('\n'); continue; }
            if (c == '/' && next == '*') { i += 2; while (i + 1 < code.Length && !(code[i] == '*' && code[i + 1] == '/')) i++; i++; continue; }
            if (c == '"')
            {
                var verbatim = i > 0 && (code[i - 1] == '@' || (i > 1 && code[i - 1] == '$' && code[i - 2] == '@'));
                var raw = i + 2 < code.Length && code[i + 1] == '"' && code[i + 2] == '"';
                if (raw)
                {
                    var end = code.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    i = end < 0 ? code.Length : end + 2; continue;
                }
                i++;
                while (i < code.Length)
                {
                    if (verbatim) { if (code[i] == '"') { if (i + 1 < code.Length && code[i + 1] == '"') { i += 2; continue; } break; } }
                    else { if (code[i] == '\\') { i += 2; continue; } if (code[i] == '"') break; }
                    i++;
                }
                continue;
            }
            if (c == '\'' && next != '\0')
            {
                var close = code.IndexOf('\'', i + (next == '\\' ? 3 : 2));
                if (close > 0 && close - i <= 8) { i = close; continue; }
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}
