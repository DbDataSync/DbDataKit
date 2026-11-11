using System.Text;
using System.Text.Json.Nodes;

namespace DbDataKit.Semantic.Sql;

/// <summary>A request that has no semantic SQL form, with why.</summary>
public sealed class SemanticSqlException(string message) : Exception(message);

/// <summary>
/// A semantic request as its one canonical semantic SQL text (docs/semantic/semantic-sql.md): the shortest unambiguous form, the one shown in explain
/// views and run records. <see cref="SemanticSqlTranslator"/> reads it back to the same request (up to <see cref="RequestNormalizer"/>).
/// </summary>
public static class SemanticSqlRenderer
{
    private static readonly HashSet<string> AggregateFunctions = ["sum", "avg", "min", "max", "count", "count_distinct", "median"];
    private static readonly HashSet<string> Handled = ["measures", "attributes", "filters", "post_filters", "order", "limit", "totals"];

    /// <summary>
    /// Totals that GROUP BY cannot say, because its grouping sets would be the same without them (one listed attribute and no grand total, or a cube of one), or that carry
    /// a key it has no place for (<c>compute</c>, anything unknown): they are written whole in the FROM options, <c>totals := {...}</c>.
    /// </summary>
    private static bool NeedsOption(JsonObject totals)
    {
        if (totals.Any(p => p.Key is not ("subtotal_by" or "grand_total" or "cube"))) return true;
        var listed = (totals["subtotal_by"] as JsonArray)?.Count ?? 0;
        var grandTotal = totals["grand_total"]?.GetValue<bool>() ?? false;
        var cube = totals["cube"]?.GetValue<bool>() ?? false;
        return listed == 0 || (listed == 1 && (cube || !grandTotal));
    }

    /// <summary>The SQL text of a request of <c>kind: query</c>; <paramref name="model"/> is the name of the semantic model after FROM.</summary>
    public static string Render(JsonObject request, string model)
    {
        if (request["kind"]?.GetValue<string>() != "query" || request["query"] is not JsonObject q)
            throw new SemanticSqlException("only a request of kind query has a semantic SQL form");

        var attributes = (q["attributes"] as JsonArray ?? []).Select(a => a!.GetValue<string>()).ToList();
        var measureNames = (q["measures"] as JsonArray ?? []).OfType<JsonValue>().Select(m => m.GetValue<string>()).ToHashSet();

        var select = attributes.Select(SqlText.Path).ToList();
        foreach (var entry in q["measures"] as JsonArray ?? []) select.Add(Entry(entry!));
        if (select.Count == 0) throw new SemanticSqlException("a query with no attributes and no measures has no SELECT list");

        var sb = new StringBuilder();
        sb.Append("SELECT ").Append(string.Join(",\n       ", select)).Append('\n');
        sb.Append("FROM ").Append(SqlText.Ident(model));
        var options = Options(request, q);
        var escapedTotals = q["totals"] is JsonObject t && NeedsOption(t);
        if (escapedTotals) options.Add(Option("totals", q["totals"]));
        if (options.Count > 0) sb.Append('(').Append(string.Join(", ", options)).Append(')');
        sb.Append('\n');

        // Top-level filters are ANDed; those that only read measures are conditions on aggregates (HAVING), the rest are WHERE.
        var where = new List<string>();
        var having = new List<string>();
        foreach (var f in RequestNormalizer.TopLevelFilters(q["filters"] as JsonArray))
            (RequestNormalizer.ReadsOnlyMeasures(f) ? having : where).Add(Filter(f, nested: false));
        if (where.Count > 0) sb.Append("WHERE ").Append(string.Join("\n  AND ", where)).Append('\n');
        if (attributes.Count > 0) sb.Append("GROUP BY ").Append(GroupBy(attributes, escapedTotals ? null : q["totals"] as JsonObject)).Append('\n');
        if (having.Count > 0) sb.Append("HAVING ").Append(string.Join("\n   AND ", having)).Append('\n');
        if (q["post_filters"] is JsonArray post && post.Count > 0)
            sb.Append("QUALIFY ").Append(string.Join("\n    AND ", RequestNormalizer.TopLevelFilters(post).Select(f => Filter(f, nested: false)))).Append('\n');
        if (q["order"] is JsonArray order && order.Count > 0)
            sb.Append("ORDER BY ").Append(string.Join(", ", order.Select(o => OrderItem(o!.AsArray(), attributes, measureNames)))).Append('\n');
        if (q["limit"] is { } limit) sb.Append("LIMIT ").Append(SqlText.Literal(limit)).Append('\n');
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    // ---------- the select list ----------

    private static string Ref(string name) => name.Contains('.') ? $"MEASURE({SqlText.Path(name)})" : SqlText.Ident(name);

    private static string Alias(JsonObject o) => o["as"] is { } a ? " AS " + SqlText.Ident(a.GetValue<string>()) : "";

    private static string NamedArgs(JsonObject o, params string[] skip)
    {
        var args = new List<string>();
        foreach (var (key, value) in o)
        {
            if (skip.Contains(key)) continue;
            if (!SqlText.IsArgumentName(key)) throw new SemanticSqlException($"'{key}' can't be written as an argument name");
            args.Add($"{SqlText.Ident(key)} := {SqlText.Literal(value)}");
        }
        return string.Join(", ", args);
    }

    private static string Entry(JsonNode entry)
    {
        if (entry is JsonValue v) return $"MEASURE({SqlText.Path(v.GetValue<string>())})";
        var o = entry.AsObject();
        if (o["transform"] is { } kind)
        {
            // a transform is a function of the measure it reads (a catalog measure, or the alias of an earlier entry) with the other keys as named arguments
            var args = NamedArgs(o, "transform", "measure", "as");
            return $"{SqlText.Ident(kind.GetValue<string>())}({Ref(o["measure"]!.GetValue<string>())}{(args.Length > 0 ? ", " + args : "")}){Alias(o)}";
        }
        if (o["measure"] is { } measure)
        {
            var args = NamedArgs(o, "measure", "as", "filters");
            var text = $"MEASURE({SqlText.Path(measure.GetValue<string>())}{(args.Length > 0 ? ", " + args : "")})";
            if (o["filters"] is JsonArray filters && filters.Count > 0)
                text += " FILTER (WHERE " + string.Join(" AND ", RequestNormalizer.TopLevelFilters(filters).Select(f => Filter(f, nested: false))) + ")";
            return text + Alias(o);
        }
        if (o["agg"] is { } agg && o["of"] is { } of && AggregateFunctions.Contains(agg.GetValue<string>()))
        {
            var function = agg.GetValue<string>();
            var column = SqlText.Path(of.GetValue<string>());
            var call = function == "count_distinct" ? $"count(DISTINCT {column})" : $"{function}({column})";
            var extra = NamedArgs(o, "agg", "of", "as");
            if (extra.Length > 0) throw new SemanticSqlException("an aggregate of a column takes no other keys");
            return call + Alias(o);
        }
        throw new SemanticSqlException("a measure entry that is not a name, a measure with overrides, a transform or an aggregate of a column");
    }

    // ---------- the FROM options: what has no clause of its own ----------

    private static List<string> Options(JsonObject request, JsonObject q)
    {
        var options = new List<string>();
        foreach (var (key, value) in request)
            if (key is not ("spec_version" or "kind" or "query"))
                options.Add(Option(key, value));
        foreach (var (key, value) in q)
            if (!Handled.Contains(key)) options.Add(Option(key, value));
        return options;
    }

    private static string Option(string key, JsonNode? value)
    {
        if (!SqlText.IsArgumentName(key)) throw new SemanticSqlException($"'{key}' can't be written as an option name");
        return $"{SqlText.Ident(key)} := {SqlText.Literal(value)}";
    }

    // ---------- filters ----------

    private static string Filter(JsonNode f, bool nested)
    {
        var o = f.AsObject();
        if (o["or"] is JsonArray or) return Group(or, " OR ", nested);
        if (o["and"] is JsonArray and) return Group(and, " AND ", nested);

        var subject = o["attribute"] is { } attribute ? SqlText.Path(attribute.GetValue<string>())
            : o["measure"] is { } measure ? Ref(measure.GetValue<string>())
            : throw new SemanticSqlException("a filter that names neither an attribute nor a measure");
        var op = o["operator"]?.GetValue<string>() ?? throw new SemanticSqlException("a filter with no operator");
        foreach (var key in o.Select(p => p.Key))
            if (key is not ("attribute" or "measure" or "operator" or "values" or "count"))
                throw new SemanticSqlException($"a filter with the key '{key}' has no semantic SQL form");
        var values = o["values"] as JsonArray ?? [];
        var count = o["count"];
        string V(int i) => SqlText.Literal(values[i]);
        string All() => string.Join(", ", values.Select(SqlText.Literal));

        // The operators with a form of their own, when they have the values that form takes; anything else (an operator this version does not know, or a
        // known one with the wrong number of values, which the engine then refuses with its own code) is written as operator(attribute, 'name', values...).
        var standard = (op, values.Count, count is null) switch
        {
            ("equals" or "not_equals" or "contains" or "starts_with" or "not_contains", >= 1, true) => true,
            ("gt" or "gte" or "lt" or "lte", 1, true) => true,
            ("between", 2, true) => true,
            ("set" or "not_set" or "current" or "period_to_date", 0, true) => true,
            ("last_complete", 0, false) => true,
            _ => false,
        };
        if (!standard)
            return $"operator({subject}, {SqlText.Quote(op)}{(values.Count > 0 ? ", " + All() : "")}{(count is null ? "" : ", count := " + SqlText.Literal(count))})";
        return op switch
        {
            "equals" => values.Count == 1 ? $"{subject} = {V(0)}" : $"{subject} IN ({All()})",
            "not_equals" => values.Count == 1 ? $"{subject} <> {V(0)}" : $"{subject} NOT IN ({All()})",
            "gt" => $"{subject} > {V(0)}",
            "gte" => $"{subject} >= {V(0)}",
            "lt" => $"{subject} < {V(0)}",
            "lte" => $"{subject} <= {V(0)}",
            "between" => $"{subject} BETWEEN {V(0)} AND {V(1)}",
            "contains" or "starts_with" => $"{op}({subject}, {All()})",
            "not_contains" => $"NOT contains({subject}, {All()})",
            "set" => $"{subject} IS NOT NULL",
            "not_set" => $"{subject} IS NULL",
            "last_complete" => $"last_complete({subject}, {SqlText.Literal(count)})",
            _ => $"{op}({subject})",
        };
    }

    private static string Group(JsonArray items, string joiner, bool nested)
    {
        var text = string.Join(joiner, items.Select(i => Filter(i!, nested: true)));
        return nested ? "(" + text + ")" : items.Count > 1 ? "(" + text + ")" : text;
    }

    // ---------- group by, totals ----------

    private static string GroupBy(List<string> attributes, JsonObject? totals)
    {
        if (totals is null) return "ALL";
        var rollup = (totals["subtotal_by"] as JsonArray ?? []).Select(a => a!.GetValue<string>()).ToList();
        if (rollup.Count == 0) return "ALL";
        var partition = attributes.Where(a => !rollup.Contains(a)).Select(SqlText.Path).ToList();
        var listed = rollup.Select(SqlText.Path).ToList();
        var grandTotal = totals["grand_total"]?.GetValue<bool>() ?? false;
        var cube = totals["cube"]?.GetValue<bool>() ?? false;

        string set;
        if (cube && grandTotal) set = $"CUBE ({string.Join(", ", listed)})";
        else if (!cube && grandTotal) set = $"ROLLUP ({string.Join(", ", listed)})";
        else
        {
            // no grand total: write the sets out (the chain's prefixes, or every non-empty subset)
            var sets = new List<List<string>>();
            if (cube)
                for (int mask = (1 << listed.Count) - 1; mask >= 1; mask--)
                    sets.Add(Enumerable.Range(0, listed.Count).Where(i => (mask & (1 << (listed.Count - 1 - i))) != 0).Select(i => listed[i]).ToList());
            else
                for (int n = listed.Count; n >= 1; n--) sets.Add(listed.Take(n).ToList());
            return "GROUPING SETS (" + string.Join(", ", sets.Select(s => "(" + string.Join(", ", partition.Concat(s)) + ")")) + ")";
        }
        return string.Join(", ", partition.Append(set));
    }

    // ---------- order ----------

    private static string OrderItem(JsonArray item, List<string> attributes, HashSet<string> measures)
    {
        var member = item[0]!.GetValue<string>();
        var direction = item.Count > 1 ? item[1]!.GetValue<string>() : "asc";
        string expression = attributes.Contains(member) ? SqlText.Path(member)
            : measures.Contains(member) ? $"MEASURE({SqlText.Path(member)})"
            : member.Contains('.') ? SqlText.Path(member) : SqlText.Ident(member);
        return direction == "desc" ? expression + " DESC" : expression;
    }
}
