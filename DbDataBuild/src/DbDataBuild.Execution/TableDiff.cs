using System.Globalization;
using System.Text;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>A table or view to compare: where it is and the columns the catalog reports.</summary>
public sealed record DiffTable(string SchemaName, string Name, IReadOnlyList<ColumnShape> Columns)
{
    public string Qualified => $"{SchemaName}.{Name}";
}

/// <summary>A column present on both sides and comparable: the key columns are among them.</summary>
public sealed record DiffColumn(string Name, ColumnShape Left, ColumnShape Right);

/// <param name="Skipped">Columns on both sides that are not compared, and why (a type with no equality, or types of different kinds).</param>
public sealed record DiffPlan(string Target, DiffTable Left, DiffTable Right, IReadOnlyList<string> Key, IReadOnlyList<DiffColumn> Compared,
    IReadOnlyList<(string Column, string Left, string Right, string Reason)> Skipped, IReadOnlyList<string> OnlyLeft, IReadOnlyList<string> OnlyRight);

public sealed record ColumnStats(string Column, long LeftNonNull, long RightNonNull, string? LeftMin, string? LeftMax, string? RightMin, string? RightMax);

/// <summary>A row present on one side only, or on both with different values; <c>Values</c> are the compared columns (empty unless values were asked for).</summary>
public sealed record DiffSample(IReadOnlyDictionary<string, string?> Key, IReadOnlyDictionary<string, string?> Values);
public sealed record DifferingSample(IReadOnlyDictionary<string, string?> Key, IReadOnlyDictionary<string, (string? Left, string? Right)> Columns);

/// <summary>
/// A comparison across connections that stopped at the buckets: so many rows differ that listing them would take more memory than the command allows (<see cref="CrossDiffer"/>). The row counts of
/// <see cref="DiffOutcome"/> are then not known; what is known is how many buckets of the key differ and in which columns.
/// </summary>
public sealed record PartialDiff(int BucketsDiffering, int Buckets, long RowsInDifferingBuckets, IReadOnlyList<string> ColumnsDiffering);

public sealed record DiffOutcome(
    long LeftRows, long RightRows, long LeftDuplicateKeys, long RightDuplicateKeys,
    long OnlyLeft, long OnlyRight, long Matched, long Differing, IReadOnlyDictionary<string, long> DifferingByColumn,
    IReadOnlyList<ColumnStats> Stats, bool RowsCompared,
    IReadOnlyList<DiffSample> OnlyLeftSamples, IReadOnlyList<DiffSample> OnlyRightSamples, IReadOnlyList<DifferingSample> DifferingSamples)
{
    public PartialDiff? Partial { get; init; }

    public bool Identical => RowsCompared && Partial == null && OnlyLeft == 0 && OnlyRight == 0 && Differing == 0 && LeftRows == RightRows;
}

/// <summary>
/// Compares the data of two tables of one target with read-only queries (DESIGN.md 9.10): the comparison is a full outer join on the key inside the engine, so counts come back and rows do not, unless the caller asks
/// for values. NULL equals NULL for a column's value; a key is compared with `=`, so a row with a NULL key is a row on one side only.
/// </summary>
public static class TableDiffer
{
    private static readonly HashSet<string> NoEquality = ["text", "ntext", "image", "xml", "geography", "geometry", "hierarchyid", "sql_variant", "json", "tsvector", "point", "box", "polygon"];

    private static readonly Dictionary<string, string> Families = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tinyint"] = "number", ["smallint"] = "number", ["int"] = "number", ["integer"] = "number", ["bigint"] = "number", ["decimal"] = "number", ["numeric"] = "number", ["float"] = "number",
        ["real"] = "number", ["double precision"] = "number", ["money"] = "number", ["smallmoney"] = "number",
        ["char"] = "text", ["varchar"] = "text", ["nchar"] = "text", ["nvarchar"] = "text", ["character"] = "text", ["character varying"] = "text", ["bpchar"] = "text",
        ["date"] = "time", ["datetime"] = "time", ["datetime2"] = "time", ["smalldatetime"] = "time", ["datetimeoffset"] = "time", ["time"] = "time", ["timestamp without time zone"] = "time",
        ["timestamp with time zone"] = "time", ["time without time zone"] = "time",
        ["bit"] = "bool", ["boolean"] = "bool", ["uniqueidentifier"] = "uuid", ["uuid"] = "uuid", ["binary"] = "bytes", ["varbinary"] = "bytes", ["bytea"] = "bytes",
    };

    /// <summary>Pairs the columns of the two tables. Returns null and the reason when a key column is missing on a side or cannot be compared.</summary>
    public static (DiffPlan? Plan, string? Problem) Plan(string target, DiffTable left, DiffTable right, IReadOnlyList<string> key, IReadOnlyCollection<string>? only = null, IReadOnlyCollection<string>? except = null)
    {
        var rightByName = right.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var leftByName = left.Columns.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var compared = new List<DiffColumn>();
        var skipped = new List<(string, string, string, string)>();
        foreach (var l in left.Columns)
        {
            if (!rightByName.TryGetValue(l.Name, out var r)) continue;
            if (only is { Count: > 0 } && !only.Contains(l.Name, StringComparer.OrdinalIgnoreCase) && !key.Contains(l.Name, StringComparer.OrdinalIgnoreCase)) continue;
            if (except != null && except.Contains(l.Name, StringComparer.OrdinalIgnoreCase) && !key.Contains(l.Name, StringComparer.OrdinalIgnoreCase)) continue;
            var reason = Incomparable(target, l, r);
            if (reason != null) skipped.Add((l.Name, Describe(l), Describe(r), reason));
            else compared.Add(new DiffColumn(l.Name, l, r));
        }
        var actualKey = new List<string>();
        foreach (var k in key)
        {
            if (!leftByName.TryGetValue(k, out var lk)) return (null, $"the key column `{k}` is not a column of {left.Qualified}.");
            if (!rightByName.ContainsKey(k)) return (null, $"the key column `{k}` is not a column of {right.Qualified}.");
            if (!compared.Any(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))) return (null, $"the key column `{k}` cannot be compared between the two tables ({skipped.FirstOrDefault(s => string.Equals(s.Item1, k, StringComparison.OrdinalIgnoreCase)).Item4}).");
            actualKey.Add(lk.Name);
        }
        var onlyLeft = left.Columns.Where(c => !rightByName.ContainsKey(c.Name)).Select(c => c.Name).ToList();
        var onlyRight = right.Columns.Where(c => !leftByName.ContainsKey(c.Name)).Select(c => c.Name).ToList();
        return (new DiffPlan(target, left, right, actualKey, compared, skipped, onlyLeft, onlyRight), null);
    }

    private static string Describe(ColumnShape c) =>
        c.Type + (c.Precision != null ? $"({c.Precision}{(c.Scale != null ? $",{c.Scale}" : "")})" : c.Length != null ? $"({(c.Length == -1 ? "max" : c.Length.ToString())})" : "");

    /// <summary>`text` has no equality on SQL Server (the old large-object type) and is an ordinary string on PostgreSQL.</summary>
    private static bool HasNoEquality(string engine, string type) => NoEquality.Contains(type) && !(engine == "postgres" && string.Equals(type, "text", StringComparison.OrdinalIgnoreCase));

    private static string? Incomparable(string engine, ColumnShape l, ColumnShape r)
    {
        if (HasNoEquality(engine, l.Type) || HasNoEquality(engine, r.Type)) return $"{(HasNoEquality(engine, l.Type) ? l.Type : r.Type)} has no equality";
        var fl = Families.GetValueOrDefault(l.Type);
        var fr = Families.GetValueOrDefault(r.Type);
        if (fl != null && fl == fr) return null;
        if (string.Equals(l.Type, r.Type, StringComparison.OrdinalIgnoreCase)) return null;
        return $"the types are of different kinds ({l.Type} and {r.Type})";
    }

    // ---- the queries ----

    private static string Q(string target, string id) => TrackingDdl.For(target).Quote(id);
    private static string Table(string target, DiffTable t) => $"{Q(target, t.SchemaName)}.{Q(target, t.Name)}";

    /// <summary>A side as a derived table of the columns that are compared, plus a marker that says the row exists (a NULL key column must not look like an absent row).</summary>
    private static string Side(DiffPlan p, DiffTable t, bool left) =>
        $"(SELECT {string.Join(", ", p.Compared.Select(c => Q(p.Target, left ? c.Left.Name : c.Right.Name) + " AS " + Q(p.Target, c.Name)))}, 1 AS ddb_present FROM {Table(p.Target, t)})";

    private static string KeyJoin(DiffPlan p) => string.Join(" AND ", p.Key.Select(k => $"a.{Q(p.Target, k)} = b.{Q(p.Target, k)}"));

    /// <summary>A value on the two sides differs; NULL equals NULL. Text columns of different collations are compared in the left column's.</summary>
    internal static string Differs(DiffPlan p, DiffColumn c)
    {
        var a = $"a.{Q(p.Target, c.Name)}";
        var b = $"b.{Q(p.Target, c.Name)}";
        if (Families.GetValueOrDefault(c.Left.Type) == "text" && c.Left.Collation != null && c.Right.Collation != null && c.Left.Collation != c.Right.Collation && System.Text.RegularExpressions.Regex.IsMatch(c.Left.Collation, "^[A-Za-z0-9_.\\-]+$"))
            b += p.Target == "postgres" ? $" COLLATE {Q(p.Target, c.Left.Collation)}" : $" COLLATE {c.Left.Collation}";
        return $"({a} <> {b} OR ({a} IS NULL AND {b} IS NOT NULL) OR ({a} IS NOT NULL AND {b} IS NULL))";
    }

    private static string Limit(string target, int n, string select, string tail) => target == "postgres" ? $"{select} {tail} LIMIT {n}" : $"SELECT TOP ({n}){select["SELECT".Length..]} {tail}";

    internal static string CountSql(DiffPlan p, DiffTable t, bool left, bool values)
    {
        var cols = p.Compared.Select(c => left ? c.Left.Name : c.Right.Name).ToList();
        var parts = new List<string> { "COUNT(*) AS ddb_rows" };
        for (var i = 0; i < cols.Count; i++)
        {
            parts.Add($"COUNT({Q(p.Target, cols[i])}) AS n{i}");
            if (values && Families.GetValueOrDefault((left ? p.Compared[i].Left : p.Compared[i].Right).Type) is "number" or "text" or "time" or "uuid")
                parts.Add($"MIN({Q(p.Target, cols[i])}) AS lo{i}, MAX({Q(p.Target, cols[i])}) AS hi{i}");
        }
        return $"SELECT {string.Join(", ", parts)} FROM {Table(p.Target, t)}";
    }

    internal static string DuplicateKeySql(DiffPlan p, DiffTable t, bool left)
    {
        var keys = string.Join(", ", p.Key.Select(k => Q(p.Target, left ? p.Compared.First(c => c.Name == k).Left.Name : p.Compared.First(c => c.Name == k).Right.Name)));
        return $"SELECT COUNT(*) FROM (SELECT {keys} FROM {Table(p.Target, t)} GROUP BY {keys} HAVING COUNT(*) > 1) d";
    }

    internal static string SummarySql(DiffPlan p)
    {
        var both = "a.ddb_present IS NOT NULL AND b.ddb_present IS NOT NULL";
        var others = p.Compared.Where(c => !p.Key.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var any = others.Count == 0 ? "1 = 0" : string.Join(" OR ", others.Select(c => Differs(p, c)));
        var parts = new List<string>
        {
            "SUM(CASE WHEN a.ddb_present IS NOT NULL AND b.ddb_present IS NULL THEN 1 ELSE 0 END) AS only_left",
            "SUM(CASE WHEN a.ddb_present IS NULL AND b.ddb_present IS NOT NULL THEN 1 ELSE 0 END) AS only_right",
            $"SUM(CASE WHEN {both} THEN 1 ELSE 0 END) AS matched",
            $"SUM(CASE WHEN {both} AND ({any}) THEN 1 ELSE 0 END) AS differing",
        };
        parts.AddRange(others.Select((c, i) => $"SUM(CASE WHEN {both} AND {Differs(p, c)} THEN 1 ELSE 0 END) AS d{i}"));
        return $"SELECT {string.Join(", ", parts)} FROM {Side(p, p.Left, true)} a FULL OUTER JOIN {Side(p, p.Right, false)} b ON {KeyJoin(p)}";
    }

    internal static string OnlySql(DiffPlan p, bool left, int limit)
    {
        var (side, other, alias, otherAlias) = left ? (Side(p, p.Left, true), Side(p, p.Right, false), "a", "b") : (Side(p, p.Right, false), Side(p, p.Left, true), "b", "a");
        var select = $"SELECT {string.Join(", ", p.Compared.Select(c => $"{alias}.{Q(p.Target, c.Name)}"))}";
        var keyJoin = KeyJoin(p);
        return Limit(p.Target, limit, select,
            $"FROM {(left ? side : other)} a {(left ? "LEFT" : "RIGHT")} JOIN {(left ? other : side)} b ON {keyJoin} WHERE {otherAlias}.ddb_present IS NULL ORDER BY {string.Join(", ", p.Key.Select(k => $"{alias}.{Q(p.Target, k)}"))}");
    }

    internal static string DifferingSql(DiffPlan p, int limit)
    {
        var others = p.Compared.Where(c => !p.Key.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var select = $"SELECT {string.Join(", ", p.Key.Select(k => $"a.{Q(p.Target, k)}").Concat(others.SelectMany(c => new[] { $"a.{Q(p.Target, c.Name)}", $"b.{Q(p.Target, c.Name)}" })))}";
        var any = string.Join(" OR ", others.Select(c => Differs(p, c)));
        return Limit(p.Target, limit, select, $"FROM {Side(p, p.Left, true)} a JOIN {Side(p, p.Right, false)} b ON {KeyJoin(p)} WHERE {any} ORDER BY {string.Join(", ", p.Key.Select(k => $"a.{Q(p.Target, k)}"))}");
    }

    // ---- running them ----

    /// <param name="showValues">Also read the smallest and largest value of each column and up to <paramref name="limit"/> sample rows of each kind of difference. Without it only counts come back.</param>
    public static async Task<DiffOutcome> RunAsync(ReadSession read, DiffPlan p, bool showValues, int limit, CancellationToken ct = default)
    {
        async Task<IReadOnlyList<object?>> One(string sql) => (await read.QueryAsync(sql, null, ct)).Single();
        static long L(object? v) => v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);

        var leftRow = await One(CountSql(p, p.Left, true, showValues));
        var rightRow = await One(CountSql(p, p.Right, false, showValues));
        var stats = new List<ColumnStats>();
        int li = 1, ri = 1;
        foreach (var c in p.Compared)
        {
            var lNon = L(leftRow[li++]); var rNon = L(rightRow[ri++]);
            string? lo1 = null, hi1 = null, lo2 = null, hi2 = null;
            if (showValues && Families.GetValueOrDefault(c.Left.Type) is "number" or "text" or "time" or "uuid")
            {
                lo1 = Cell(leftRow[li++]); hi1 = Cell(leftRow[li++]);
            }
            if (showValues && Families.GetValueOrDefault(c.Right.Type) is "number" or "text" or "time" or "uuid")
            {
                lo2 = Cell(rightRow[ri++]); hi2 = Cell(rightRow[ri++]);
            }
            stats.Add(new ColumnStats(c.Name, lNon, rNon, lo1, hi1, lo2, hi2));
        }

        var dupLeft = L((await One(DuplicateKeySql(p, p.Left, true)))[0]);
        var dupRight = L((await One(DuplicateKeySql(p, p.Right, false)))[0]);
        var leftRows = L(leftRow[0]); var rightRows = L(rightRow[0]);
        if (dupLeft > 0 || dupRight > 0)
            return new DiffOutcome(leftRows, rightRows, dupLeft, dupRight, 0, 0, 0, 0, new Dictionary<string, long>(), stats, false, [], [], []);

        var summary = await One(SummarySql(p));
        var others = p.Compared.Where(c => !p.Key.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList();
        var byColumn = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < others.Count; i++) byColumn[others[i].Name] = L(summary[4 + i]);
        var onlyLeft = L(summary[0]); var onlyRight = L(summary[1]); var matched = L(summary[2]); var differing = L(summary[3]);

        var leftSamples = new List<DiffSample>(); var rightSamples = new List<DiffSample>(); var diffSamples = new List<DifferingSample>();
        if (showValues && limit > 0)
        {
            DiffSample ToSample(IReadOnlyList<object?> row)
            {
                var all = p.Compared.Select((c, i) => (c.Name, Value: Cell(row[i]))).ToList();
                return new DiffSample(all.Where(x => p.Key.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToDictionary(x => x.Name, x => x.Value), all.Where(x => !p.Key.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToDictionary(x => x.Name, x => x.Value));
            }
            if (onlyLeft > 0) leftSamples.AddRange((await read.QueryAsync(OnlySql(p, true, limit), null, ct)).Select(ToSample));
            if (onlyRight > 0) rightSamples.AddRange((await read.QueryAsync(OnlySql(p, false, limit), null, ct)).Select(ToSample));
            if (differing > 0)
                foreach (var row in await read.QueryAsync(DifferingSql(p, limit), null, ct))
                {
                    var key = p.Key.Select((k, i) => (k, Cell(row[i]))).ToDictionary(x => x.k, x => x.Item2);
                    var cols = new Dictionary<string, (string?, string?)>();
                    for (var i = 0; i < others.Count; i++)
                    {
                        var (a, b) = (Cell(row[p.Key.Count + 2 * i]), Cell(row[p.Key.Count + 2 * i + 1]));
                        if (a != b) cols[others[i].Name] = (a, b);
                    }
                    diffSamples.Add(new DifferingSample(key, cols));
                }
        }
        return new DiffOutcome(leftRows, rightRows, dupLeft, dupRight, onlyLeft, onlyRight, matched, differing, byColumn, stats, true, leftSamples, rightSamples, diffSamples);
    }

    private static string? Cell(object? v) => v switch
    {
        null => null,
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss.FFFFFF", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        byte[] bytes => "0x" + Convert.ToHexString(bytes),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture),
    };
}
