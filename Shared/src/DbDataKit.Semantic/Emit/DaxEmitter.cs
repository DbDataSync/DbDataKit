using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Emit;

/// <summary>
/// Compiles one source's part of a request to DAX.
/// Output columns are renamed with SELECTCOLUMNS to the result contract's column names.
/// </summary>
public static class DaxEmitter
{
    private static string Lit(JsonNode v) => v.GetValueKind() switch
    {
        JsonValueKind.String => "\"" + v.GetValue<string>().Replace("\"", "\"\"") + "\"",
        JsonValueKind.True => "TRUE",
        JsonValueKind.False => "FALSE",
        JsonValueKind.Number => v.GetValue<decimal>().ToString(CultureInfo.InvariantCulture),
        _ => "BLANK ()",
    };

    private static string Lit(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";

    private static string Col(Catalog cat, string source, string attr) => cat.Binding(attr, source)!.Dax!;

    /// <summary>An ISO date as a DAX date value.</summary>
    private static string DateLit(JsonNode v)
    {
        var d = DateOnly.ParseExact(v.GetValue<string>(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        return $"DATE ( {d.Year}, {d.Month}, {d.Day} )";
    }

    private static string SetOf(List<JsonNode> vals, bool date = false) =>
        "{ " + string.Join(", ", vals.Select(v => date ? DateLit(v) : Lit(v))) + " }";

    private static string LeafBool(Catalog cat, string source, LeafFilter f)
    {
        var col = Col(cat, source, f.Name);
        var vals = f.Values;
        var date = cat.TypeOf(f.Name) == "date";
        string V(JsonNode v) => date ? DateLit(v) : Lit(v);
        return f.Operator switch
        {
            "equals" => $"{col} IN {SetOf(vals, date)}",
            // SQL's <> and NOT LIKE drop a NULL; DAX's NOT ( col IN ... ) keeps a blank (measured on SSAS, plan 19 J22), so the blank is excluded here
            "not_equals" => $"( NOT ISBLANK ( {col} ) && NOT ( {col} IN {SetOf(vals, date)} ) )",
            "between" => $"( {col} >= {V(vals[0])} && {col} <= {V(vals[1])} )",
            "contains" => "( " + string.Join(" || ", vals.Select(v => $"CONTAINSSTRING ( {col}, {Lit(v)} )")) + " )",
            "not_contains" => "( NOT ISBLANK ( " + col + " ) && " + string.Join(" && ", vals.Select(v => $"NOT CONTAINSSTRING ( {col}, {Lit(v)} )")) + " )",
            "starts_with" => "( " + string.Join(" || ", vals.Select(v => $"LEFT ( {col}, {v.GetValue<string>().Length} ) = {Lit(v)}")) + " )",
            "gt" => $"{col} > {V(vals[0])}",
            "gte" => $"{col} >= {V(vals[0])}",
            "lt" => $"{col} < {V(vals[0])}",
            "lte" => $"{col} <= {V(vals[0])}",
            "set" => $"NOT ISBLANK ( {col} )",
            "not_set" => $"ISBLANK ( {col} )",
            _ => throw new NotSupportedException($"operator {f.Operator} not implemented"),
        };
    }

    private static string HavingBool(string col, LeafFilter l)
    {
        var vals = l.Values;
        return l.Operator switch
        {
            "equals" => $"{col} IN {SetOf(vals)}", "not_equals" => $"( NOT ISBLANK ( {col} ) && NOT ( {col} IN {SetOf(vals)} ) )",
            "between" => $"( {col} >= {Lit(vals[0])} && {col} <= {Lit(vals[1])} )",
            "gt" => $"{col} > {Lit(vals[0])}", "gte" => $"{col} >= {Lit(vals[0])}",
            "lt" => $"{col} < {Lit(vals[0])}", "lte" => $"{col} <= {Lit(vals[0])}",
            "set" => $"NOT ISBLANK ( {col} )", "not_set" => $"ISBLANK ( {col} )",
            _ => throw new NotSupportedException($"operator {l.Operator} on a measure"),
        };
    }

    private static string Bool(Catalog cat, string source, Filter f) => f switch
    {
        AndFilter a => "( " + string.Join(" && ", a.Items.Select(x => Bool(cat, source, x))) + " )",
        OrFilter o => "( " + string.Join(" || ", o.Items.Select(x => Bool(cat, source, x))) + " )",
        LeafFilter l => LeafBool(cat, source, l),
        _ => throw new NotSupportedException(),
    };

    private static string FilterArg(Catalog cat, string source, Filter f)
    {
        // A single equals filter is a plain TREATAS; everything else filters the columns involved.
        if (f is LeafFilter { Operator: "equals" } leaf)
            return $"TREATAS ( {SetOf(leaf.Values, cat.TypeOf(leaf.Name) == "date")}, {Col(cat, source, leaf.Name)} )";
        var cols = f.Names().OrderBy(m => m, StringComparer.Ordinal).Select(m => Col(cat, source, m)).ToList();
        var table = cols.Count == 1
            ? $"ALL ( {cols[0]} )"
            : "CROSSJOIN ( " + string.Join(", ", cols.Select(c => $"ALL ( {c} )")) + " )";
        return $"FILTER ( {table}, {Bool(cat, source, f)} )";
    }

    /// <summary>
    /// The measure's own expression. A filtered measure wraps it in CALCULATE with KEEPFILTERS, so its filters
    /// intersect with the query's instead of replacing them.
    /// </summary>
    private static string MeasureExpr(Catalog cat, string source, string measure)
    {
        var def = cat.Doc.Measures[measure];
        // A catalog measure's own filters are already inside its model measure; only the request's are added here.
        if (def.RequestFilters == 0) return def.Dax!;
        var keep = string.Join(", ", def.Filters.TakeLast(def.RequestFilters).Select(f => $"KEEPFILTERS ( {FilterArg(cat, source, f)} )"));
        return $"CALCULATE ( {def.Dax}, {keep} )";
    }

    public static string BuildDax(
        Catalog cat, string source, List<string> attrs, List<string> measures,
        List<Filter> filters, List<(string Member, string Direction)> order, int? limit,
        List<string>? rollup = null, List<Filter>? having = null, bool cube = false)
    {
        const string ind = "        ";
        rollup = rollup is { Count: > 0 } ? rollup : null;
        // Native subtotals: each listed attribute gets its own ROLLUPADDISSUBTOTAL flag column. DAX then produces every
        // combination, so the filter keeps only the ones where an outer attribute is rolled up after the inner ones
        // (the chain SQL ROLLUP gives), and the level is the number of flags set.
        string Flag(string d) => $"_sub_{Catalog.ColName(d)}";
        var scArgs = attrs.Select(d => rollup is not null && rollup.Contains(d)
            ? $"ROLLUPADDISSUBTOTAL ( {Col(cat, source, d)}, {Lit(Flag(d))} )" : Col(cat, source, d)).ToList();
        scArgs.AddRange(filters.Select(f => FilterArg(cat, source, f)));
        scArgs.AddRange(measures.Select(m => $"{Lit(Catalog.ColName(m))}, {MeasureExpr(cat, source, m)}"));
        // SUMMARIZECOLUMNS leaves out a group whose measures are all blank, but SQL keeps every group that has fact rows. A filtered measure can be blank for a
        // group that has fact rows, so a hidden column that is 1 when the fact table has rows in the group, and blank otherwise, keeps those groups (and only those:
        // an expression that isn't blank elsewhere would make SSAS evaluate every combination of the dimensions).
        scArgs.AddRange(measures.Where(m => cat.Doc.Measures[m].RequestFilters > 0 && cat.Doc.Measures[m].DaxFact is not null)
            .Select(m => cat.Doc.Measures[m].DaxFact!).Distinct()
            .Select((t, i) => $"{Lit("_has_facts_" + i)}, IF ( NOT ISEMPTY ( {t} ), 1 )"));
        var summarize = "SUMMARIZECOLUMNS (\n" + string.Join(",\n", scArgs.Select(a => ind + a)) + "\n    )";
        if (rollup is not null)
        {
            var chain = new List<string>();
            for (int i = 0; i < rollup.Count - 1 && !cube; i++)
                chain.Add($"NOT ( [{Flag(rollup[i])}] && NOT [{Flag(rollup[i + 1])}] )");
            if (chain.Count > 0)
                summarize = "FILTER (\n    " + summarize.Replace("\n", "\n    ") + ",\n    " + string.Join(" && ", chain) + "\n)";
        }

        // A filter on a measure is a condition on the column SUMMARIZECOLUMNS made for it.
        if (having is { Count: > 0 })
        {
            string Cond(Filter f) => f switch
            {
                AndFilter a => "( " + string.Join(" && ", a.Items.Select(Cond)) + " )",
                OrFilter o => "( " + string.Join(" || ", o.Items.Select(Cond)) + " )",
                LeafFilter l => HavingBool($"[{Catalog.ColName(l.Name)}]", l),
                _ => throw new NotSupportedException(),
            };
            summarize = "FILTER (\n    " + summarize.Replace("\n", "\n    ") + ",\n    " + string.Join(" && ", having.Select(Cond)) + "\n)";
        }

        var sel = attrs.Select(d => $"{Lit(Catalog.ColName(d))}, {Col(cat, source, d)}").ToList();
        if (rollup is not null)
        {
            // A chain's level is the number of flags set; a cube's is looked up from them, in the numbering the engine uses.
            string level;
            if (!cube) level = string.Join(" + ", rollup.Select(d => $"IF ( [{Flag(d)}], 1, 0 )"));
            else
            {
                var n = rollup.Count;
                var bits = string.Join(" + ", rollup.Select((d, i) => $"IF ( [{Flag(d)}], {1 << (n - 1 - i)}, 0 )"));
                level = $"SWITCH ( {bits}, " + string.Join(", ", RollupSets.Subsets(n, true).Select((s, l) => $"{RollupSets.Mask(s, n)}, {l}")) + " )";
            }
            sel.Add($"{Lit(SqlEmitter.RollupLevelColumn)}, {level}");
        }
        sel.AddRange(measures.Select(m => $"{Lit(Catalog.ColName(m))}, [{Catalog.ColName(m)}]"));
        var table = "SELECTCOLUMNS (\n    " + summarize + ",\n" + string.Join(",\n", sel.Select(s => "    " + s)) + "\n)";

        var orderCols = SqlEmitter.OrderColumns(attrs, order);
        string body;
        if (limit is not null && attrs.Count > 0)
        {
            var topnArgs = string.Join(", ", orderCols.Select(c => $"[{c.Column}], {c.Direction}"));
            body = $"TOPN (\n    {limit},\n    " + table.Replace("\n", "\n    ") + $",\n    {topnArgs}\n)";
        }
        else body = table;

        var text = "EVALUATE\n" + body;
        if (orderCols.Count > 0)
            text += "\nORDER BY " + string.Join(", ", orderCols.Select(c => $"[{c.Column}] {c.Direction}"));
        return text;
    }

    /// <summary>
    /// The first and last day of every member of a calendar level, read from the model's date table: the same rows as <see cref="SqlEmitter.BuildLevelRanges"/>. The level
    /// and the date are columns of one model table (the caller checks they are bound to one join and both have DAX). Columns: <c>lvl</c>, <c>lo</c>, <c>hi</c>.
    /// </summary>
    public static string BuildLevelRanges(Catalog cat, string source, string level, string date)
    {
        var (levelDax, dateDax) = (Col(cat, source, level), Col(cat, source, date));
        return "EVALUATE\nSELECTCOLUMNS (\n"
             + $"    SUMMARIZECOLUMNS ( {levelDax}, \"lo\", MIN ( {dateDax} ), \"hi\", MAX ( {dateDax} ) ),\n"
             + $"    \"lvl\", {levelDax},\n    \"lo\", [lo],\n    \"hi\", [hi]\n)";
    }

    /// <summary>
    /// The distinct pairs of two attributes that are columns of one model table, from the table itself (so a member with no fact rows is there), as
    /// <see cref="SqlEmitter.BuildPairs"/> reads them. Columns are named as the attributes are (<see cref="Catalog.ColName"/>).
    /// </summary>
    public static string BuildPairs(Catalog cat, string source, string a, string b)
    {
        var (da, db) = (Col(cat, source, a), Col(cat, source, b));
        var table = System.Text.RegularExpressions.Regex.Match(da, @"^\s*('[^']+'|[A-Za-z_]\w*)\s*\[").Groups[1].Value;
        return $"EVALUATE\nDISTINCT (\n    SELECTCOLUMNS ( {table}, {Lit(Catalog.ColName(a))}, {da}, {Lit(Catalog.ColName(b))}, {db} )\n)";
    }
}
