using System.Text.Json.Nodes;
using DbDataKit.Access;
using DbDataKit.Config.Yaml;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

public static partial class CatalogLoader
{
    /// <summary>
    /// The <c>access</c> section of a catalog (format 2, plan 22a GP01), read from its YAML nodes so each problem has the file, line and column of what is wrong, and checked against the catalog's tables, columns and sources. Schema errors about
    /// the section are left out when the access reader has said what is wrong, and kept when it has not (the schema and the reader disagree).
    /// </summary>
    private static void ReadAccess(YamlMapping root, string file, List<QisError> errors)
    {
        if (root.Get(AccessSection.Key) is not { } node || errors.Any(x => x.Path == "$/access")) return;
        var diagnostics = new List<ConfigDiagnostic>();
        AccessSectionReader.Read(node, file, diagnostics, new CatalogAccessTarget(ToJson(root)));
        var found = diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => Err("invalid_access", "$.access", d.ToString())).ToList();
        if (found.Count == 0) return;
        errors.RemoveAll(x => x.Path.StartsWith("$/access/", StringComparison.Ordinal));
        errors.AddRange(found);
    }

    /// <summary>The access section of a catalog that has no YAML text (an imported one): written out as a YAML flow collection, which is JSON, and read as the file would be.</summary>
    internal static List<QisError> AccessOf(JsonObject json)
    {
        var errors = new List<QisError>();
        if (json[AccessSection.Key] is not { } section) return errors;
        var diagnostics = new List<ConfigDiagnostic>();
        AccessSectionReader.ReadText(section.ToJsonString(), "access", diagnostics, new CatalogAccessTarget(json));
        errors.AddRange(diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(d => Err("invalid_access", "$.access", d.Message)));
        return errors;
    }

    internal static AccessSection? ReadAccess(JsonObject json)
    {
        if (json[AccessSection.Key] is not { } section) return null;
        var diagnostics = new List<ConfigDiagnostic>();
        return AccessSectionReader.ReadText(section.ToJsonString(), "access", diagnostics);
    }
}

/// <summary>What a catalog says about its datasets to the access reader: its tables (by catalog name) with their columns and kinds, the source each is on, and which tables the model itself reads.</summary>
internal sealed class CatalogAccessTarget : IAccessTarget
{
    private readonly JsonObject _json;
    private readonly Dictionary<string, AccessDataset> _tables = new(StringComparer.Ordinal);
    private readonly HashSet<string> _read;

    public CatalogAccessTarget(JsonObject json)
    {
        _json = json;
        foreach (var table in (json["tables"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (table["name"]?.GetValue<string>() is not { } name) continue;
            var source = table["x_source"]?.GetValue<string>() ?? "";
            var kind = (json["sources"] as JsonObject)?[source]?["kind"]?.GetValue<string>();
            Dictionary<string, AccessColumnKind>? columns = null;
            if (table["columns"] is JsonArray list && list.Count > 0)
            {
                columns = new Dictionary<string, AccessColumnKind>(StringComparer.Ordinal);
                foreach (var column in list.OfType<JsonObject>())
                    if (column["name"]?.GetValue<string>() is { } columnName) columns[columnName] = KindOf(column["datatype"]?.GetValue<string>());
            }

            _tables[name] = new AccessDataset(name, source, columns, kind);
        }

        _read = ReadByTheModel(json);
    }

    private static AccessColumnKind KindOf(string? datatype) => datatype switch
    {
        "String" => AccessColumnKind.Text,
        "Integer" or "Decimal" or "Float" => AccessColumnKind.Number,
        "Boolean" => AccessColumnKind.Boolean,
        "Date" or "Time" or "DateTime" or "DateTimeTz" => AccessColumnKind.Date,
        "Opaque" => AccessColumnKind.Other,
        _ => AccessColumnKind.Unknown,
    };

    /// <summary>The tables a dimension, a measure, a hierarchy, a calendar or a relationship names: anything that has a <c>table</c> (a binding, a measure's <c>of</c>), or a relationship's <c>from</c> and <c>to</c>.</summary>
    private static HashSet<string> ReadByTheModel(JsonObject json)
    {
        var read = new HashSet<string>(StringComparer.Ordinal);
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    foreach (var (key, value) in o)
                    {
                        if (key == "table" && value is JsonValue t && t.TryGetValue<string>(out var name)) read.Add(name);
                        else Walk(value);
                    }
                    break;
                case JsonArray a:
                    foreach (var item in a) Walk(item);
                    break;
            }
        }

        foreach (var key in new[] { "dimensions", "measures", "hierarchies", "calendars", "segments", "queries" }) Walk(json[key]);
        foreach (var relationship in (json["relationships"] as JsonArray)?.OfType<JsonObject>() ?? [])
        {
            if (relationship["from"]?.GetValue<string>() is { } from) read.Add(from);
            if (relationship["to"]?.GetValue<string>() is { } to) read.Add(to);
        }

        return read;
    }

    public AccessDataset? Dataset(string name) => _tables.GetValueOrDefault(name);

    public IReadOnlyList<string> DatasetNames => [.. _tables.Keys];

    public AccessDataset? RulesTable(string reference, AccessDataset filtered) => _tables.GetValueOrDefault(reference);

    public string? WhyNotRulesTable(AccessDataset table) => _read.Contains(table.Name) ? "a dimension, measure or relationship of the model reads it, so a person could browse who may see what" : null;

    public string? WhyCannotFilter(AccessDataset dataset) => dataset.SourceKind == "ssas" ? "its source is Analysis Services, which row rules cannot reach (the connection is the server's)" : null;

    public bool CacheSharedAcrossCallers => _json["cache"]?["scope"]?.GetValue<string>() == "shared" && _json["cache"]?["enabled"]?.GetValue<bool>() != false;
}
