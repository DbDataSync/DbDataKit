using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Dialect;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic.Emit;

/// <summary>
/// Compiles one source's part of a request to SQL.
/// The dialect is a shared <see cref="ISqlDialect"/> (<see cref="EmitDialects"/>): it writes the parameter markers and the row limit;
/// the time zone and bucketing functions are the emitter's, per engine. SQL Server's text is sent to SQL Server, PostgreSQL's to PostgreSQL; the DuckDB text is the twin that
/// runs on DuckDB (the conformance seed data, a DuckDB source).
/// </summary>
public static class SqlEmitter
{
    private static readonly Dictionary<string, string> AggSql = new()
    {
        ["sum"] = "SUM({0})", ["avg"] = "AVG({0})", ["min"] = "MIN({0})", ["max"] = "MAX({0})",
        ["count"] = "COUNT({0})", ["count_distinct"] = "COUNT(DISTINCT {0})",
    };

    private static bool TSql(ISqlDialect dialect) => dialect.Id == EngineId.SqlServer;

    private static bool Postgres(ISqlDialect dialect) => dialect.Id == EngineId.Postgres;

    /// <summary>OFFSET/FETCH where the dialect has it (the query is always ordered), else the dialect's own style. PostgreSQL has both and reads LIMIT.</summary>
    private static RowLimitStyle RowLimitStyleFor(ISqlDialect dialect) =>
        !Postgres(dialect) && dialect.SupportedRowLimitStyles.Contains(RowLimitStyle.OffsetFetch) ? RowLimitStyle.OffsetFetch : dialect.DefaultRowLimitStyle;

    /// <summary>PostgreSQL stores no NUL character in text, so a value holding one (the engine's "matches no member" marker) cannot be bound and cannot equal anything.</summary>
    private static bool HoldsNul(JsonNode v) => v.GetValueKind() == JsonValueKind.String && v.GetValue<string>().Contains('\0');

    private sealed class Params(ISqlDialect dialect)
    {
        public ISqlDialect Dialect => dialect;
        public List<object?> Values { get; } = new();
        public string Add(JsonNode value)
        {
            Values.Add(value.GetValueKind() switch
            {
                JsonValueKind.String => value.GetValue<string>(),
                JsonValueKind.Number => value.GetValue<decimal>(),
                JsonValueKind.True or JsonValueKind.False => value.GetValue<bool>(),
                _ => null,
            });
            // the first parameter is p1 where the style is named, $1 where it is numbered
            return dialect.ParameterName(Values.Count - 1, $"p{Values.Count}");
        }
        public string AddText(string value) => Add(JsonValue.Create(value)!);
    }

    private static string LikeEscape(string v) =>
        v.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>The column a native subtotal query adds. It is the same column the combined result carries.</summary>
    public const string RollupLevelColumn = "_rollup_level";

    /// <summary>
    /// The level of a row from the GROUPING() of each rolled attribute (1 when rolled up). A chain's level is the number rolled up;
    /// a cube's is looked up from the bits, so it numbers the sets the way <see cref="RollupSets"/> does.
    /// </summary>
    internal static string LevelExpr(List<string> groupings, bool cube)
    {
        if (!cube) return "(" + string.Join(" + ", groupings) + ")";
        var n = groupings.Count;
        var bits = string.Join(" + ", groupings.Select((g, i) => $"{g} * {1 << (n - 1 - i)}"));
        var cases = RollupSets.Subsets(n, true).Select((s, level) => $"WHEN {RollupSets.Mask(s, n)} THEN {level}");
        return $"(CASE {bits} {string.Join(" ", cases)} END)";
    }

    public static string ColumnExpr(Catalog cat, string source, string attr, ISqlDialect dialect)
    {
        var src = cat.Doc.Sources[source];
        var b = cat.Binding(attr, source)!;
        var alias = b.Join is not null ? src.Joins[b.Join].Alias : src.Fact.Alias;
        var raw = $"{alias}.{b.Column}";
        return cat.Doc.Attributes[attr].Bucket is { } bucket ? BucketExpr(cat, source, raw, bucket.Granularity, dialect) : raw;
    }

    /// <summary>A datetime column as the date it falls on in the request's zone, to be matched to a date table.</summary>
    private static string LocalDate(Catalog cat, string source, string raw, ISqlDialect dialect)
    {
        var (zone, stored) = (cat.Zone, cat.Doc.Sources[source].DatetimeZone);
        return TSql(dialect)
            ? $"CAST(CAST(({raw} AT TIME ZONE '{TimeZones.WindowsId(stored)}') AT TIME ZONE '{TimeZones.WindowsId(zone)}' AS datetime2) AS date)"
            : $"CAST(timezone('{zone}', timezone('{stored}', {raw})) AS DATE)";
    }

    /// <summary>
    /// A datetime column as the label of its hour, day, week (Monday), month, quarter or year in the request's zone. The column holds UTC unless the source says
    /// it holds another zone's wall-clock time; the zone names are validated IANA names, so they are safe to write into the text.
    /// </summary>
    private static string BucketExpr(Catalog cat, string source, string raw, string granularity, ISqlDialect dialect)
    {
        var (zone, stored) = (cat.Zone, cat.Doc.Sources[source].DatetimeZone);
        if (TSql(dialect))
        {
            var local = $"CAST(({raw} AT TIME ZONE '{TimeZones.WindowsId(stored)}') AT TIME ZONE '{TimeZones.WindowsId(zone)}' AS datetime2)";
            return granularity switch
            {
                "hour" => $"(CONVERT(varchar(13), {local}, 120) + ':00')",
                "day" => $"CONVERT(varchar(10), {local}, 120)",
                "week" => $"CONVERT(varchar(10), DATETRUNC(iso_week, {local}), 120)",
                "month" => $"CONVERT(varchar(7), {local}, 120)",
                "quarter" => $"(CONVERT(varchar(4), {local}, 120) + '-Q' + CAST(DATEPART(quarter, {local}) AS varchar(1)))",
                "year" => $"CONVERT(varchar(4), {local}, 120)",
                _ => throw new NotSupportedException($"granularity {granularity}"),
            };
        }
        var l = $"timezone('{zone}', timezone('{stored}', {raw}))";
        if (Postgres(dialect))
            return granularity switch
            {
                "hour" => $"(to_char({l}, 'YYYY-MM-DD HH24') || ':00')",
                "day" => $"to_char({l}, 'YYYY-MM-DD')",
                "week" => $"to_char(date_trunc('week', {l}), 'YYYY-MM-DD')",
                "month" => $"to_char({l}, 'YYYY-MM')",
                "quarter" => $"(to_char({l}, 'YYYY') || '-Q' || CAST(EXTRACT(QUARTER FROM {l}) AS VARCHAR))",
                "year" => $"to_char({l}, 'YYYY')",
                _ => throw new NotSupportedException($"granularity {granularity}"),
            };
        return granularity switch
        {
            "hour" => $"strftime({l}, '%Y-%m-%d %H:00')",
            "day" => $"strftime({l}, '%Y-%m-%d')",
            "week" => $"strftime(date_trunc('week', {l}), '%Y-%m-%d')",
            "month" => $"strftime({l}, '%Y-%m')",
            "quarter" => $"(strftime({l}, '%Y') || '-Q' || CAST(quarter({l}) AS VARCHAR))",
            "year" => $"strftime({l}, '%Y')",
            _ => throw new NotSupportedException($"granularity {granularity}"),
        };
    }

    /// <summary>
    /// A filter on a datetime column. Dates mean whole days in the request's zone, so the column is compared with the instants those days start at (the column
    /// itself is never converted, so an index can be used); values are written as the wall-clock text the source holds.
    /// </summary>
    private static string DatetimeLeafSql(Catalog cat, string source, LeafFilter f, Params p, string col)
    {
        var zone = TimeZones.Find(cat.Zone)!;
        var stored = TimeZones.Find(cat.Doc.Sources[source].DatetimeZone)!;
        string At(DateTime utc) => $"CAST({p.AddText(TimeZones.Text(TimeZones.ToWallClock(utc, stored)))} AS {(TSql(p.Dialect) ? "datetime2" : "TIMESTAMP")})";
        // A value is an instant, or a date, which is the whole of that day in the zone: from its start up to where the next day starts.
        (DateTime Start, DateTime? Next) Bound(JsonNode v)
        {
            var text = v.GetValue<string>();
            if (DateOnly.TryParseExact(text, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day))
                return (TimeZones.StartOfDayUtc(day, zone), TimeZones.StartOfDayUtc(day.AddDays(1), zone));
            return TimeZones.TryInstant(text, zone, out var utc) ? (utc, null) : throw new NotSupportedException($"{v} isn't a date or an instant");
        }
        string Is(JsonNode v)
        {
            var (start, next) = Bound(v);
            return next is null ? $"{col} = {At(start)}" : $"({col} >= {At(start)} AND {col} < {At(next.Value)})";
        }
        var vals = f.Values;
        switch (f.Operator)
        {
            case "equals": return vals.Count == 1 ? Is(vals[0]) : "(" + string.Join(" OR ", vals.Select(Is)) + ")";
            case "not_equals": return vals.Count == 1 ? $"NOT {Is(vals[0])}" : "NOT (" + string.Join(" OR ", vals.Select(Is)) + ")";
            case "gt": { var (start, next) = Bound(vals[0]); return next is null ? $"{col} > {At(start)}" : $"{col} >= {At(next.Value)}"; }
            case "gte": return $"{col} >= {At(Bound(vals[0]).Start)}";
            case "lt": return $"{col} < {At(Bound(vals[0]).Start)}";
            case "lte": { var (start, next) = Bound(vals[0]); return next is null ? $"{col} <= {At(start)}" : $"{col} < {At(next.Value)}"; }
            case "between":
            {
                var (low, _) = Bound(vals[0]);
                var (high, highNext) = Bound(vals[1]);
                return $"({col} >= {At(low)} AND {(highNext is null ? $"{col} <= {At(high)}" : $"{col} < {At(highNext.Value)}")})";
            }
            case "set": return $"{col} IS NOT NULL";
            case "not_set": return $"{col} IS NULL";
            default: throw new NotSupportedException($"operator {f.Operator} on a datetime");
        }
    }

    private static string LeafSql(Catalog cat, string source, LeafFilter f, Params p)
    {
        var col = ColumnExpr(cat, source, f.Name, p.Dialect);
        if (cat.TypeOf(f.Name) == "datetime") return DatetimeLeafSql(cat, source, f, p, col);
        var vals = f.Values;
        // A date is bound as ISO text and cast, so the comparison is on dates in every engine.
        var date = cat.TypeOf(f.Name) == "date";
        string Bind(JsonNode v) => date ? $"CAST({p.Add(v)} AS DATE)" : p.Add(v);
        switch (f.Operator)
        {
            case "equals":
            case "not_equals":
            {
                var neg = f.Operator == "not_equals";
                if (Postgres(p.Dialect) && !date)
                {
                    // a value PostgreSQL cannot store equals no row; if none is left, nothing is equal (and everything that has a value is not equal)
                    vals = vals.Where(v => !HoldsNul(v)).ToList();
                    if (vals.Count == 0) return neg ? $"{col} IS NOT NULL" : "(1 = 0)";
                }
                if (vals.Count == 1) return $"{col} {(neg ? "<>" : "=")} {Bind(vals[0])}";
                return $"{col} {(neg ? "NOT " : "")}IN ({string.Join(", ", vals.Select(Bind))})";
            }
            case "contains":
            case "not_contains":
            case "starts_with":
            {
                var neg = f.Operator == "not_contains";
                var parts = vals.Select(v =>
                {
                    var s = LikeEscape(v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString());
                    var pattern = f.Operator == "starts_with" ? $"{s}%" : $"%{s}%";
                    return $"{col} {(neg ? "NOT " : "")}LIKE {p.AddText(pattern)} ESCAPE '\\'";
                }).ToList();
                return parts.Count == 1 ? parts[0] : "(" + string.Join(neg ? " AND " : " OR ", parts) + ")";
            }
            case "gt": return $"{col} > {Bind(vals[0])}";
            case "gte": return $"{col} >= {Bind(vals[0])}";
            case "lt": return $"{col} < {Bind(vals[0])}";
            case "lte": return $"{col} <= {Bind(vals[0])}";
            case "between": return $"{col} BETWEEN {Bind(vals[0])} AND {Bind(vals[1])}";
            case "set": return $"{col} IS NOT NULL";
            case "not_set": return $"{col} IS NULL";
            default: throw new NotSupportedException($"operator {f.Operator} not implemented");
        }
    }

    private static string HavingSql(Filter f, Params p, Func<string, string> aggregate) => f switch
    {
        AndFilter a => "(" + string.Join(" AND ", a.Items.Select(x => HavingSql(x, p, aggregate))) + ")",
        OrFilter o => "(" + string.Join(" OR ", o.Items.Select(x => HavingSql(x, p, aggregate))) + ")",
        LeafFilter l => HavingLeaf(l, p, aggregate(l.Name)),
        _ => throw new NotSupportedException(),
    };

    private static string HavingLeaf(LeafFilter l, Params p, string expr)
    {
        var vals = l.Values;
        return l.Operator switch
        {
            "equals" => vals.Count == 1 ? $"{expr} = {p.Add(vals[0])}" : $"{expr} IN ({string.Join(", ", vals.Select(p.Add))})",
            "not_equals" => vals.Count == 1 ? $"{expr} <> {p.Add(vals[0])}" : $"{expr} NOT IN ({string.Join(", ", vals.Select(p.Add))})",
            "between" => $"{expr} BETWEEN {p.Add(vals[0])} AND {p.Add(vals[1])}",
            "gt" => $"{expr} > {p.Add(vals[0])}", "gte" => $"{expr} >= {p.Add(vals[0])}",
            "lt" => $"{expr} < {p.Add(vals[0])}", "lte" => $"{expr} <= {p.Add(vals[0])}",
            "set" => $"{expr} IS NOT NULL", "not_set" => $"{expr} IS NULL",
            _ => throw new NotSupportedException($"operator {l.Operator} on a measure"),
        };
    }

    private static string FilterSql(Catalog cat, string source, Filter f, Params p) => f switch
    {
        AndFilter a => "(" + string.Join(" AND ", a.Items.Select(x => FilterSql(cat, source, x, p))) + ")",
        OrFilter o => "(" + string.Join(" OR ", o.Items.Select(x => FilterSql(cat, source, x, p))) + ")",
        LeafFilter l => LeafSql(cat, source, l, p),
        _ => throw new NotSupportedException(),
    };

    /// <summary>Requested order, then every attribute ascending as a deterministic tiebreak.</summary>
    public static List<(string Column, string Direction)> OrderColumns(
        List<string> attrs, List<(string Member, string Direction)> order)
    {
        var cols = order.Select(o => (Catalog.ColName(o.Member), o.Direction.ToUpperInvariant())).ToList();
        foreach (var d in attrs)
        {
            var cn = Catalog.ColName(d);
            if (!cols.Any(c => c.Item1 == cn)) cols.Add((cn, "ASC"));
        }
        return cols;
    }

    /// <summary>
    /// The first and last day of every member of a calendar level, read from the date table itself. The level and the
    /// date dimension must be columns of one joined table (the caller checks). It takes no parameters, so one text serves both dialects.
    /// </summary>
    public static string BuildLevelRanges(Catalog cat, string source, string level, string date)
    {
        var src = cat.Doc.Sources[source];
        var lb = cat.Binding(level, source)!;
        var db = cat.Binding(date, source)!;
        var table = src.Joins[lb.Join!];
        return $"SELECT {table.Alias}.{lb.Column} AS lvl, MIN({table.Alias}.{db.Column}) AS lo, MAX({table.Alias}.{db.Column}) AS hi\n"
             + $"FROM {table.Table} {table.Alias}\nGROUP BY {table.Alias}.{lb.Column};";
    }

    /// <summary>
    /// The distinct values of a attribute read from its own table, so a member with no fact rows is included. A attribute
    /// bound to the fact table itself has no table of its own and is read from the fact. It takes no parameters.
    /// </summary>
    public static string BuildMembers(Catalog cat, string source, string attribute)
    {
        var src = cat.Doc.Sources[source];
        var b = cat.Binding(attribute, source)!;
        var table = b.Join is not null ? src.Joins[b.Join] : src.Fact;
        return $"SELECT DISTINCT {table.Alias}.{b.Column} AS {Catalog.ColName(attribute)}\nFROM {table.Table} {table.Alias};";
    }

    /// <summary>
    /// The distinct pairs of two attributes that are columns of one table, read from that table (so a member with no fact rows is
    /// included). The caller checks that both are bound in the source with the same join.
    /// </summary>
    public static string BuildPairs(Catalog cat, string source, string a, string b)
    {
        var src = cat.Doc.Sources[source];
        var (ba, bb) = (cat.Binding(a, source)!, cat.Binding(b, source)!);
        var table = src.Joins[ba.Join!];
        return $"SELECT DISTINCT {table.Alias}.{ba.Column} AS {Catalog.ColName(a)}, {table.Alias}.{bb.Column} AS {Catalog.ColName(b)}\nFROM {table.Table} {table.Alias};";
    }

    public static (string Text, List<object?> Parameters) BuildSelect(
        Catalog cat, string source, List<string> attrs, List<string> measures,
        List<Filter> filters, List<(string Member, string Direction)> order, int? limit, ISqlDialect dialect,
        List<string>? rollup = null, List<Filter>? having = null, bool cube = false)
    {
        var src = cat.Doc.Sources[source];
        var p = new Params(dialect);
        rollup = rollup is { Count: > 0 } ? rollup : null;

        var usedJoins = new List<string>();
        void Need(string attr)
        {
            var b = cat.Binding(attr, source)!;
            AddJoin(b.Join);
        }
        // A join that hangs off another dimension comes after the join it hangs off.
        void AddJoin(string? key)
        {
            if (key is null || usedJoins.Contains(key)) return;
            AddJoin(src.Joins[key].Parent);
            usedJoins.Add(key);
        }
        attrs.ForEach(Need);
        foreach (var f in filters)
            foreach (var m in f.Names().OrderBy(x => x, StringComparer.Ordinal)) Need(m);
        foreach (var measure in measures)
            foreach (var f in cat.Doc.Measures[measure].Filters)
                foreach (var m in f.Names().OrderBy(x => x, StringComparer.Ordinal)) Need(m);

        var select = attrs.Select(d => $"{ColumnExpr(cat, source, d, dialect)} AS {Catalog.ColName(d)}").ToList();
        // Native subtotals: ROLLUP over the listed attributes (outermost first), and one column that counts how many of
        // them a row has rolled up. GROUPING tells a rolled-up column from a member that is really NULL.
        if (rollup is not null)
            select.Add(LevelExpr(rollup.Select(d => $"GROUPING({ColumnExpr(cat, source, d, dialect)})").ToList(), cube) + $" AS {RollupLevelColumn}");
        // A filtered measure aggregates only the rows that pass its own filters, on top of the query's.
        string AggExpr(string m)
        {
            var def = cat.Doc.Measures[m];
            var value = $"{src.Fact.Alias}.{def.Column}";
            if (def.Filters.Count > 0)
                value = $"CASE WHEN {string.Join(" AND ", def.Filters.Select(f => FilterSql(cat, source, f, p)))} THEN {value} END";
            return string.Format(AggSql[def.Agg], value);
        }
        foreach (var m in measures) select.Add($"{AggExpr(m)} AS {Catalog.ColName(m)}");

        var lines = new List<string>
        {
            "SELECT " + string.Join(",\n       ", select),
            $"FROM {src.Fact.Table} {src.Fact.Alias}",
        };
        foreach (var j in usedJoins)
        {
            var js = src.Joins[j];
            lines.Add(js.LocalDateOf is null ? $"JOIN {js.Table} {js.Alias} ON {js.Condition}"
                : $"LEFT JOIN {js.Table} {js.Alias} ON {js.Alias}.{js.DateColumn} = {LocalDate(cat, source, js.LocalDateOf, dialect)}");
        }
        if (filters.Count > 0)
            lines.Add("WHERE " + string.Join("\n  AND ", filters.Select(f => FilterSql(cat, source, f, p))));
        if (rollup is not null)
            lines.Add("GROUP BY " + string.Join(", ", attrs.Where(d => !rollup.Contains(d)).Select(d => ColumnExpr(cat, source, d, dialect))
                .Append($"{(cube ? "CUBE" : "ROLLUP")}({string.Join(", ", rollup.Select(d => ColumnExpr(cat, source, d, dialect)))})")));
        else if (attrs.Count > 0)
            lines.Add("GROUP BY " + string.Join(", ", attrs.Select(d => ColumnExpr(cat, source, d, dialect))));

        // A filter on a measure is a condition on its aggregate.
        if (having is { Count: > 0 })
            lines.Add("HAVING " + string.Join("\n   AND ", having.Select(f => HavingSql(f, p, AggExpr))));

        var orderCols = OrderColumns(attrs, order);
        if (orderCols.Count > 0)
            lines.Add("ORDER BY " + string.Join(", ", orderCols.Select(c => $"{c.Column} {c.Direction}")));
        if (limit is not null && attrs.Count > 0)
            lines.Add(dialect.RowLimit(RowLimitStyleFor(dialect), limit.Value).Suffix.Trim());

        return (string.Join("\n", lines) + ";", p.Values);
    }
}
