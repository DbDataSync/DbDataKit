using System.Text;
using System.Text.Json;

namespace DbData.Config.Yaml;

/// <summary>
/// JSON as an input format behind the same node tree (Proxy can keep <c>appsettings.json</c>). The text is first checked as strict JSON
/// (no comments, no trailing commas) so syntax errors carry JSON-accurate positions; JSON is a subset of YAML flow syntax, so the same strict reader then
/// builds the nodes (scalars stay strings, duplicate keys are errors) and every node keeps its line and column.
/// </summary>
public static class JsonInput
{
    public static bool IsJsonFile(string file) => file.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

    /// <returns>The root node, or null for an empty file or when the file could not be read (see diagnostics).</returns>
    public static YamlNode? Read(string text, string file, List<ConfigDiagnostic> diagnostics, IDiagnosticCatalog? catalog = null)
    {
        catalog ??= DefaultDiagnosticCatalog.Instance;
        if (string.IsNullOrWhiteSpace(text)) return null;
        try
        {
            using var doc = JsonDocument.Parse(Encoding.UTF8.GetBytes(text), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Disallow, AllowTrailingCommas = false, MaxDepth = 64 });
        }
        catch (JsonException ex)
        {
            diagnostics.Add(catalog.Create(SharedCodes.YamlSyntax, file, (int)(ex.LineNumber ?? -1) + 1, (int)(ex.BytePositionInLine ?? -1) + 1, "Invalid JSON: " + ex.Message));
            return null;
        }
        return StrictYamlReader.Read(text, file, diagnostics, catalog);
    }

    /// <summary>Reads by file extension: <c>.json</c> as JSON, anything else as YAML.</summary>
    public static YamlNode? ReadByExtension(string text, string file, List<ConfigDiagnostic> diagnostics, IDiagnosticCatalog? catalog = null) =>
        IsJsonFile(file) ? Read(text, file, diagnostics, catalog) : StrictYamlReader.Read(text, file, diagnostics, catalog);
}
