using System.Text.Json.Nodes;

namespace DbDataBuild.Sql.Analysis;

/// <summary>Why the filter of a load's slice cannot be applied below part of a query: what stands in the way, and where.</summary>
/// <param name="Kind">aggregate_output, window_output, window_partition, limit or distinct_on.</param>
public sealed record SliceBlocker(string Kind, string Detail);

/// <summary>
/// A load strategy slices the *finished* query: `SELECT * FROM (<body>) WHERE slice_column >= @watermark`. That is always correct, and it is cheap when the engine can apply the
/// filter early, below the expensive part of the body. It cannot when the column is computed by an aggregate or a window function, when a window is not partitioned by it, or
/// when the body has a LIMIT or DISTINCT ON (applying the filter early would change the rows), so every load then does the work for the whole history and discards most of it.
/// This follows the slice column down through the body's derived tables, CTEs and set operations and reports what it meets. It reports only what it can prove: a column it
/// cannot trace (a star, an ambiguous name) gives no finding.
/// </summary>
public static class SliceAnalyzer
{
    private static readonly HashSet<string> Aggregates = new(StringComparer.OrdinalIgnoreCase)
    {
        "sum", "avg", "min", "max", "count", "median", "mode", "stddev", "stddev_pop", "stddev_samp", "variance", "var_pop", "var_samp", "string_agg", "group_concat",
        "array_agg", "list", "first", "last", "any_value", "arg_min", "arg_max", "bool_and", "bool_or", "bit_and", "bit_or", "bit_xor", "approx_count_distinct", "quantile", "quantile_cont", "quantile_disc",
    };

    public static IReadOnlyList<SliceBlocker> Analyze(string sql, string column)
    {
        var parsed = Polyglot.Parse(sql, Dialects.Canonical);
        if (!parsed.Ok || JsonNode.Parse(parsed.Data!) is not JsonArray { Count: > 0 } statements) return [];
        return Node(statements[0]!, column, new Dictionary<string, JsonNode>(StringComparer.OrdinalIgnoreCase)).DistinctBy(b => (b.Kind, b.Detail)).ToList();
    }

    private static JsonObject? Body(JsonNode n) => n is JsonObject o && o.Count == 1 ? o.First().Value as JsonObject : null;
    private static string? Kind(JsonNode n) => n is JsonObject o && o.Count == 1 ? o.First().Key : null;
    /// <summary>The text of an identifier node (`{"name": "d", "quoted": false}`), null when there is none.</summary>
    private static string? Ident(JsonNode? n) => n is JsonObject o && o["name"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static IEnumerable<SliceBlocker> Node(JsonNode node, string column, Dictionary<string, JsonNode> ctes)
    {
        switch (Kind(node))
        {
            case "select": return Select(Body(node)!, column, ctes);
            case "subquery": return Body(node)!["this"] is { } inner ? Node(inner, column, ctes) : [];
            case "union" or "intersect" or "except":
                var b = Body(node)!;
                return (b["left"] is { } l ? Node(l, column, ctes) : []).Concat(b["right"] is { } r ? Node(r, column, ctes) : []);
            default: return [];
        }
    }

    private static IEnumerable<SliceBlocker> Select(JsonObject sel, string column, Dictionary<string, JsonNode> outer)
    {
        var ctes = new Dictionary<string, JsonNode>(outer, StringComparer.OrdinalIgnoreCase);
        if (sel["with"]?["ctes"] is JsonArray defs)
            foreach (var cte in defs) if (Ident(cte!["alias"]) is { } n && cte["this"] is { } body) ctes[n] = body;

        var found = new List<SliceBlocker>();
        if (sel["limit"] is not null) found.Add(new("limit", "the query has a LIMIT, so the filter cannot be applied before it"));
        if (sel["distinct_on"] is not null) found.Add(new("distinct_on", "the query uses DISTINCT ON, so the filter cannot be applied before it"));

        var projection = Projection(sel, column);
        if (projection == null) return found;                       // a star or a name that is not there: nothing can be proved
        var expr = projection;
        if (Contains(expr, n => Kind(n) is { } k && (Aggregates.Contains(k) || Kind(n) == "function" && Ident(Body(n)) is { } fn && Aggregates.Contains(fn)), out var agg))
        {
            found.Add(new("aggregate_output", $"`{column}` is computed by an aggregate ({AggregateName(agg!)})"));
            return found;
        }
        if (Contains(expr, n => Kind(n) == "window_function", out _))
        {
            found.Add(new("window_output", $"`{column}` is computed by a window function"));
            return found;
        }

        var refs = Columns(expr).ToList();
        if (refs.Count == 0) return found;                          // a constant: nothing below it to filter
        var groupBy = sel["group_by"] as JsonObject;
        if (groupBy != null && groupBy["all"]?.GetValue<bool?>() != true)
        {
            var keys = (groupBy["expressions"] as JsonArray)?.SelectMany(e => Columns(e!)).Select(c => c.Name.ToLowerInvariant()).ToHashSet() ?? [];
            if (refs.Any(r => !keys.Contains(r.Name.ToLowerInvariant()))) return found;     // not a grouping key and not an aggregate: unusual, so prove nothing
        }

        // a window function in this select that is not partitioned by the column keeps it from being applied early
        foreach (var w in Windows(sel))
        {
            var partition = (w["over"]?["partition_by"] as JsonArray)?.SelectMany(p => Columns(p!)).Select(c => c.Name.ToLowerInvariant()).ToHashSet() ?? [];
            if (!refs.All(r => partition.Contains(r.Name.ToLowerInvariant())))
            {
                found.Add(new("window_partition", $"a window function in the query is not partitioned by `{column}`, so every window is computed over all rows before the filter applies"));
                break;
            }
        }

        // follow the column into the table, CTE or derived table it comes from
        var first = refs[0];
        var source = SourceOf(sel, first);
        if (source == null) return found;
        if (Kind(source) == "subquery") return found.Concat(Body(source)!["this"] is { } sub ? Node(sub, first.Name, ctes) : []);
        if (Kind(source) == "table" && Body(source) is { } t && t["schema"] is null && Ident(t["name"]) is { } tableName && ctes.TryGetValue(tableName, out var cteBody))
            return found.Concat(Node(cteBody, first.Name, ctes));
        return found;
    }

    private sealed record ColumnRef(string Name, string? Table);

    /// <summary>The expression the select produces for the named output column; null when it cannot be found (a star, or no such column).</summary>
    private static JsonNode? Projection(JsonObject sel, string column)
    {
        foreach (var item in sel["expressions"] as JsonArray ?? [])
        {
            if (item is null) continue;
            if (Kind(item) == "alias" && Body(item) is { } a)
            {
                if (string.Equals(Ident(a["alias"]), column, StringComparison.OrdinalIgnoreCase)) return a["this"];
            }
            else if (Kind(item) == "column" && string.Equals(Ident(Body(item)!["name"]), column, StringComparison.OrdinalIgnoreCase)) return item;
            else if (Kind(item) is "star") return null;
        }
        return null;
    }

    private static IEnumerable<ColumnRef> Columns(JsonNode n)
    {
        switch (n)
        {
            case JsonObject o:
                if (o.Count == 1 && o.First().Key == "column" && o.First().Value is JsonObject c && Ident(c["name"]) is { } name)
                    yield return new ColumnRef(name, Ident(c["table"]));
                else
                    foreach (var (_, v) in o) if (v != null) foreach (var r in Columns(v)) yield return r;
                break;
            case JsonArray a:
                foreach (var v in a) if (v != null) foreach (var r in Columns(v)) yield return r;
                break;
        }
    }

    private static bool Contains(JsonNode n, Func<JsonNode, bool> match, out JsonNode? hit)
    {
        hit = null;
        switch (n)
        {
            case JsonObject o:
                if (o.Count == 1 && match(o)) { hit = o; return true; }
                if (o.Count == 1 && o.First().Key is "subquery" or "select") return false;       // a scalar subquery is its own scope
                foreach (var (_, v) in o) if (v != null && Contains(v, match, out hit)) return true;
                break;
            case JsonArray a:
                foreach (var v in a) if (v != null && Contains(v, match, out hit)) return true;
                break;
        }
        return false;
    }

    private static string AggregateName(JsonNode n) => (Kind(n) == "function" ? Ident(Body(n)) : Kind(n))?.ToUpperInvariant() ?? "aggregate";

    private static IEnumerable<JsonObject> Windows(JsonObject sel)
    {
        var found = new List<JsonObject>();
        void Walk(JsonNode? n)
        {
            switch (n)
            {
                case JsonObject o:
                    if (o.Count == 1 && o.First().Key == "window_function" && o.First().Value is JsonObject w) found.Add(w);
                    foreach (var (k, v) in o) if (k is not ("subquery" or "select")) Walk(v);
                    break;
                case JsonArray a:
                    foreach (var v in a) Walk(v);
                    break;
            }
        }
        Walk(sel["expressions"]);
        return found;
    }

    /// <summary>The FROM item (a table or a derived table) a column comes from: by qualifier when it has one, the only item, or the derived table that outputs that name.</summary>
    private static JsonNode? SourceOf(JsonObject sel, ColumnRef c)
    {
        var items = new List<JsonNode>();
        foreach (var f in sel["from"]?["expressions"] as JsonArray ?? []) if (f != null) items.Add(f);
        foreach (var j in sel["joins"] as JsonArray ?? []) if (Body(j!) is { } jb ? jb["this"] is { } t : j!["this"] is { }) items.Add((j!["this"] ?? Body(j!)!["this"])!);
        static string? AliasOf(JsonNode i) => Body(i) is { } b ? (Ident(b["alias"]) ?? (Kind(i) == "table" ? Ident(b["name"]) : null)) : null;
        if (c.Table != null) return items.FirstOrDefault(i => string.Equals(AliasOf(i), c.Table, StringComparison.OrdinalIgnoreCase));
        if (items.Count == 1) return items[0];
        var holders = items.Where(i => Kind(i) == "subquery" && Body(i)!["this"] is { } inner && OutputNames(inner).Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        return holders.Count == 1 ? holders[0] : null;
    }

    private static IEnumerable<string> OutputNames(JsonNode node)
    {
        var sel = Kind(node) == "select" ? Body(node) : Kind(node) is "union" or "intersect" or "except" && Body(node)!["left"] is { } l ? Body(l) : null;
        foreach (var item in sel?["expressions"] as JsonArray ?? [])
        {
            if (item is null) continue;
            if (Kind(item) == "alias" && Ident(Body(item)!["alias"]) is { } a) yield return a;
            else if (Kind(item) == "column" && Ident(Body(item)!["name"]) is { } n) yield return n;
        }
    }
}
