using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Presentation;

/// <summary>One field of a result's descriptor, as a view sees it.</summary>
public sealed record ResultField(string Name, string Type, string Kind, string? Binding, bool Additive, JsonObject? Format = null);

/// <summary>
/// A result as a view reads it: the descriptor's fields, the rows, and which rows are totals. A view resolves fields and
/// classifies rows from this and never computes a value.
/// </summary>
public sealed class ResultTable
{
    public List<ResultField> Fields { get; }
    public List<JsonObject> Rows { get; }
    /// <summary>The column holding each row's rollup level, or null when the result has no subtotals.</summary>
    public string? LevelField { get; }
    /// <summary>For each rollup level present: the logical attributes it has rolled up.</summary>
    public Dictionary<int, HashSet<string>> RolledUpByLevel { get; } = new();

    public ResultTable(JsonObject result)
    {
        // A whole result, or just a result set.
        var resultSet = result["combined"] as JsonObject ?? result;
        var descriptor = resultSet["descriptor"]!.AsObject();
        Fields = descriptor["schema"]!["fields"]!.AsArray().Select(f => f!.AsObject()).Select(f => new ResultField(
            f["name"]!.GetValue<string>(), f["type"]?.GetValue<string>() ?? "string", f["x_kind"]?.GetValue<string>() ?? "column",
            f["x_binding"]?.GetValue<string>(),
            // x_additivity says it outright; a descriptor without it is read from the rollup default.
            f["x_additivity"]?.GetValue<string>() is { } additivity ? additivity == "additive"
                : f["x_semantics"]?["rollup"]?["default"]?.GetValue<string>() == "sum",
            f["x_format"] as JsonObject)).ToList();
        Rows = resultSet["rows"]?.AsArray().Select(r => r!.AsObject()).ToList() ?? [];
        if (descriptor["x_totals"] is JsonObject totals)
        {
            LevelField = totals["level_field"]!.GetValue<string>();
            foreach (var level in totals["levels"]!.AsArray())
                RolledUpByLevel[level!["level"]!.GetValue<int>()] = level["rolled_up"]!.AsArray().Select(d => d!.GetValue<string>()).ToHashSet();
        }
    }

    /// <summary>A field by the name of an output (an alias, a transform) or by logical name (orders.net_revenue).</summary>
    public ResultField? Resolve(string reference) =>
        Fields.FirstOrDefault(f => f.Name == reference)
        ?? Fields.FirstOrDefault(f => f.Name == Catalog.ColName(reference))
        ?? Fields.FirstOrDefault(f => f.Binding == reference && f.Kind == "attribute");

    public List<string> Suggest(string reference) =>
        Fields.Where(f => f.Name != LevelField).Select(f => f.Binding ?? f.Name).Distinct()
            .OrderByDescending(n => Fuzzy.JaroWinkler(Fuzzy.Normalize(reference), Fuzzy.Normalize(n))).Take(3).ToList();

    public bool IsAttribute(ResultField f) => f.Kind == "attribute";

    /// <summary>The attributes a row has rolled up (none for a detail row).</summary>
    public HashSet<string> RolledUp(JsonObject row)
    {
        if (LevelField is null || row[LevelField] is not JsonValue level || level.GetValueKind() != JsonValueKind.Number) return [];
        return RolledUpByLevel.TryGetValue(level.GetValue<int>(), out var rolled) ? rolled : [];
    }

    public bool IsDetail(JsonObject row) => RolledUp(row).Count == 0;

    /// <summary>The text that stands for a member: its value, or (none) when the member really is NULL.</summary>
    public static string Label(JsonNode? value) => value is null ? "(none)" : value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();

    public static double? Number(JsonNode? value) =>
        value is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;
}
