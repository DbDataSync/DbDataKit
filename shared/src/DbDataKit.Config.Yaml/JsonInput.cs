using System.Text;
using System.Text.Json;

namespace DbDataKit.Config.Yaml;

/// <summary>
/// JSON as an input format behind the same node tree (Proxy can keep <c>appsettings.json</c>). Like appsettings.json, <c>//</c> and <c>/* */</c> comments and
/// trailing commas are allowed: they are blanked out (positions unchanged) and the text is checked as JSON so syntax errors carry JSON-accurate positions.
/// JSON is a subset of YAML flow syntax, so the same strict reader then builds the nodes (scalars stay strings, duplicate keys are errors) with line and column.
/// </summary>
public static class JsonInput
{
    public static bool IsJsonFile(string file) => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <returns>The root node, or null for an empty file or when the file could not be read (see diagnostics).</returns>
    public static YamlNode? Read(string text, string file, List<ConfigDiagnostic> diagnostics, IDiagnosticCatalog? catalog = null)
    {
        catalog ??= DefaultDiagnosticCatalog.Instance;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = Blank(text);
        if (string.IsNullOrWhiteSpace(clean)) return null;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetBytes(clean), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(catalog.Create(SharedCodes.YamlSyntax, file, (int)(ex.LineNumber ?? -1) + 1, (int)(ex.BytePositionInLine ?? -1) + 1, "Invalid JSON: " + ex.Message));
            return null;
        }
        return StrictYamlReader.Read(clean, file, diagnostics, catalog);
    }

    /// <summary>Reads by file extension: <c>.json</c> as JSON, anything else as YAML.</summary>
    public static YamlNode? ReadByExtension(string text, string file, List<ConfigDiagnostic> diagnostics, IDiagnosticCatalog? catalog = null) =>
        IsJsonFile(file) ? Read(text, file, diagnostics, catalog) : StrictYamlReader.Read(text, file, diagnostics, catalog);

    /// <summary>Replaces comments and trailing commas with spaces (newlines kept) so positions are unchanged and the rest is plain JSON.</summary>
    internal static string Blank(string t)
    {
        var c = t.ToCharArray();
        var i = 0;
        var lastComma = -1;
        while (i < c.Length)
        {
            var ch = c[i];
            if (ch == '"')
            {
                for (i++; i < c.Length && c[i] != '"' && c[i] != '\n'; i++)
                {
                    if (c[i] == '\\') i++;
                }

                i++;
                lastComma = -1;
                continue;
            }

            if (ch == '/' && i + 1 < c.Length && c[i + 1] == '/')
            {
                while (i < c.Length && c[i] != '\n' && c[i] != '\r') c[i++] = ' ';
                continue;
            }

            if (ch == '/' && i + 1 < c.Length && c[i + 1] == '*')
            {
                var end = t.IndexOf("*/", i + 2, StringComparison.Ordinal);
                var stop = end < 0 ? c.Length : end + 2;
                for (; i < stop; i++)
                {
                    if (c[i] != '\n' && c[i] != '\r') c[i] = ' ';
                }

                continue;
            }

            if (ch == ',') lastComma = i;
            else if (ch is '}' or ']')
            {
                if (lastComma >= 0) c[lastComma] = ' ';
                lastComma = -1;
            }
            else if (!char.IsWhiteSpace(ch)) lastComma = -1;
            i++;
        }

        return new string(c);
    }
}
