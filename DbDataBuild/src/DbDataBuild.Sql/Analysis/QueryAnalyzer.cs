using System.Text.Json;
using DbDataBuild.Sql.Ast;
using DbDataBuild.Targets.DuckDb;

namespace DbDataBuild.Sql.Analysis;

public sealed record SchemaColumnSpec(string Name, string Type, bool Nullable);

/// <summary>A table the analyzer may resolve columns against (an upstream model or source), with its declared columns.</summary>
public sealed record SchemaTableSpec(string? SchemaName, string Name, IReadOnlyList<SchemaColumnSpec> Columns);

public sealed record ColumnRef(string? Table, string Column);

/// <param name="Nullability">non_null, nullable or unknown (conservative, from lineage and declared nullability).</param>
public sealed record ProjectionFact(int Index, string? Name, string TransformKind, string? CastType, string? TypeHint, string Nullability, IReadOnlyList<ColumnRef> Upstream);

public sealed record BaseTable(string? SchemaName, string Table)
{
    public string QualifiedName => SchemaName == null ? Table : $"{SchemaName}.{Table}";
}

/// <summary>Where a query uses columns: the clause (`filter`, `join`, `group`, `having`, `order`, `window_partition`, `window_order`), the expression's text, and the columns it names.</summary>
public sealed record ColumnUse(string Context, string ExpressionSql, IReadOnlyList<ColumnRef> References);

/// <param name="GroupedColumns">Columns named in GROUP BY, in order (resolved by lineage), when the query groups.</param>
public sealed record QueryFacts(
    IReadOnlyList<ProjectionFact> Projections,
    IReadOnlyList<BaseTable> BaseTables,
    IReadOnlyList<ColumnRef> GroupedColumns,
    bool IsDistinct,
    int JoinCount,
    bool IsSetOperation)
{
    public IReadOnlyList<ColumnUse> ColumnUses { get; init; } = [];
}

/// <summary>Typed view of polyglot's <c>analyze_query</c> (lineage, nullability, base tables) plus a few AST facts. Offline.</summary>
public static class QueryAnalyzer
{
    public static (QueryFacts? Facts, string? Error) Analyze(string sql, IReadOnlyList<SchemaTableSpec>? schema = null)
    {
        var options = new Dictionary<string, object?> { ["dialect"] = "duckdb" };
        if (schema is { Count: > 0 })
            options["schema"] = new
            {
                tables = schema.Select(t => new
                {
                    name = t.Name,
                    schema = t.SchemaName,
                    columns = t.Columns.Select(c => new { name = c.Name, type = c.Type, nullable = c.Nullable }),
                }),
            };

        var analysis = Polyglot.AnalyzeQuery(sql, JsonSerializer.Serialize(options));
        // PIVOT and UNPIVOT are DuckDB syntax the offline parser reads only as far as the statement: it neither analyzes a PIVOT statement nor looks into the query a PIVOT reads from.
        // DuckDB's own parser says which tables they read; lineage and nullability are then unknown (the output columns still come from DuckDB's describe).
        if (!analysis.Ok && analysis.Error is { } failure && failure.Contains("requires a SELECT or set operation", StringComparison.Ordinal) && DuckParseTree.BaseTables(sql) is ({ } fallbackTables, _))
            return (new QueryFacts([], fallbackTables.Select(t => new BaseTable(t.SchemaName, t.Name)).ToList(), [], false, 0, false), null);
        if (!analysis.Ok) return (null, analysis.Error);
        var parsed = Polyglot.Parse(sql, Dialects.Canonical);
        if (!parsed.Ok) return (null, parsed.Error);

        using var doc = JsonDocument.Parse(analysis.Data!);
        var root = doc.RootElement;
        var projections = root.GetProperty("projections").EnumerateArray().Select(p => new ProjectionFact(
            p.GetProperty("index").GetInt32(),
            Str(p, "name"),
            Str(p, "transformKind") ?? "",
            Str(p, "castType"),
            Str(p, "typeHint"),
            Str(p, "nullability") ?? "unknown",
            p.GetProperty("upstream").EnumerateArray().Select(Ref).ToList())).ToList();
        var baseTables = root.GetProperty("baseTables").EnumerateArray().Select(t => new BaseTable(Str(t, "schema"), Str(t, "table") ?? Str(t, "name") ?? "")).ToList();
        if (parsed.Data!.Contains("\"pivot\"", StringComparison.Ordinal) || parsed.Data.Contains("\"unpivot\"", StringComparison.Ordinal))
            foreach (var t in DuckParseTree.BaseTables(sql).Tables ?? [])
                if (!baseTables.Any(b => string.Equals(b.SchemaName ?? "", t.SchemaName ?? "", StringComparison.OrdinalIgnoreCase) && string.Equals(b.Table, t.Name, StringComparison.OrdinalIgnoreCase)))
                    baseTables.Add(new BaseTable(t.SchemaName, t.Name));
        var grouped = root.TryGetProperty("columnUses", out var uses)
            ? uses.EnumerateArray().Where(u => Str(u, "context") == "group").SelectMany(u => u.GetProperty("references").EnumerateArray()).Select(Ref).ToList()
            : [];
        var shape = Str(root, "shape");

        var ast = AstNode.Parse(parsed.Data!);
        var select = ast.Children.FirstOrDefault(n => n.Type == "select");
        var distinct = select != null && Detectors_IsTrue(select, "distinct") && !HasValue(select, "distinct_on");
        var joins = select != null && select.TryGet("joins", out var j) && j.ValueKind == JsonValueKind.Array ? j.GetArrayLength() : 0;
        var columnUses = root.TryGetProperty("columnUses", out var cu)
            ? cu.EnumerateArray().Select(u => new ColumnUse(Str(u, "context") ?? "", Str(u, "expressionSql") ?? "", u.GetProperty("references").EnumerateArray().Select(Ref).ToList())).ToList()
            : [];
        return (new QueryFacts(projections, baseTables, grouped, distinct, joins, shape == "set_operation") { ColumnUses = columnUses }, null);
    }

    private static ColumnRef Ref(JsonElement r) => new(Str(r, "table"), Str(r, "column") ?? "");

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Detectors_IsTrue(AstNode n, string field) => n.TryGet(field, out var v) && v.ValueKind == JsonValueKind.True;

    private static bool HasValue(AstNode n, string field) => n.TryGet(field, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.False);
}
