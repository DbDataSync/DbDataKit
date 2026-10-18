using System.Globalization;
using System.Text.RegularExpressions;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>
/// Compares a table on one connection with a table on another, which may be another engine (DESIGN.md 9.10.1). The two engines cannot join, so each one computes, inside the engine, a 64-bit digest of
/// every value from a canonical text form that is the same on both engines (<see cref="Canonical"/>), and the comparison is on digests: first per bucket (the first two hex digits of the key's digest: a
/// row count and, for each column, the sum of the digests), then, only for the buckets that differ, per row. No value leaves an engine unless the caller asks for values, and then only the sample rows.
/// </summary>
public static class CrossDiffer
{
    private const int BucketDrillLimit = 160;       // when more buckets than this differ, the rows of every bucket are fetched in one pass

    /// <summary>The most rows (of the larger side) whose digests are fetched to be compared one by one: about 200 MB. More than this and the result stops at the buckets (<see cref="PartialDiff"/>).</summary>
    public static long MaxDrillRows { get; internal set; } = 1_000_000;

    /// <summary>The kind of a column for the canonical form, with the decimal scale the two sides agree on (<c>Scale</c>), or why the column cannot be compared.</summary>
    internal sealed record Kinded(string Kind, int Scale, int IntegerDigits, bool Padded, string? Reason);

    private static readonly Dictionary<string, (string Kind, int Digits, int Scale)> Fixed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["tinyint"] = ("number", 3, 0), ["smallint"] = ("number", 5, 0), ["int"] = ("number", 10, 0), ["integer"] = ("number", 10, 0), ["bigint"] = ("number", 19, 0),
        ["money"] = ("number", 15, 4), ["smallmoney"] = ("number", 6, 4),
    };

    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ["char"] = "text", ["varchar"] = "text", ["nchar"] = "text", ["nvarchar"] = "text", ["character"] = "text", ["character varying"] = "text", ["bpchar"] = "text", ["text"] = "text",
        ["date"] = "date",
        ["datetime"] = "timestamp", ["datetime2"] = "timestamp", ["smalldatetime"] = "timestamp", ["timestamp without time zone"] = "timestamp",
        ["datetimeoffset"] = "instant", ["timestamp with time zone"] = "instant",
        ["time"] = "time", ["time without time zone"] = "time",
        ["bit"] = "bool", ["boolean"] = "bool", ["uniqueidentifier"] = "uuid", ["uuid"] = "uuid",
        ["binary"] = "bytes", ["varbinary"] = "bytes", ["bytea"] = "bytes",
    };

    private static readonly HashSet<string> Floating = new(StringComparer.OrdinalIgnoreCase) { "float", "real", "double precision" };
    private static readonly HashSet<string> Padded = new(StringComparer.OrdinalIgnoreCase) { "char", "nchar", "character", "bpchar" };

    internal static Kinded KindOf(string engine, ColumnShape c)
    {
        var type = c.Type;
        if (Floating.Contains(type)) return new("", 0, 0, false, "floating point has no text form that is the same on every engine");
        if (Fixed.TryGetValue(type, out var f)) return new(f.Kind, f.Scale, f.Digits - f.Scale, false, null);
        if (string.Equals(type, "decimal", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "numeric", StringComparison.OrdinalIgnoreCase))
            return c.Precision is { } p && c.Scale is { } s ? new("number", s, p - s, false, null) : new("", 0, 0, false, "a number without a declared precision and scale has no fixed text form");
        if (string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) && engine != "postgres") return new("", 0, 0, false, "text has no equality");
        if (Kinds.TryGetValue(type, out var kind)) return new(kind, 0, 0, Padded.Contains(type), null);
        return new("", 0, 0, false, $"{type} has no canonical text form");
    }

    /// <summary>Pairs the columns. Like <see cref="TableDiffer.Plan"/>, but a column is comparable when both sides map to the same kind of value and a decimal of either side fits the common scale.</summary>
    public static (DiffPlan? Plan, string? Problem) Plan(string leftEngine, string rightEngine, DiffTable left, DiffTable right, IReadOnlyList<string> key,
        IReadOnlyCollection<string>? only = null, IReadOnlyCollection<string>? except = null)
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
            var reason = Incomparable(leftEngine, rightEngine, l, r);
            if (reason != null) skipped.Add((l.Name, Describe(l), Describe(r), reason)); else compared.Add(new DiffColumn(l.Name, l, r));
        }
        var actualKey = new List<string>();
        foreach (var k in key)
        {
            if (!leftByName.TryGetValue(k, out var lk)) return (null, $"the key column `{k}` is not a column of {left.Qualified}.");
            if (!rightByName.ContainsKey(k)) return (null, $"the key column `{k}` is not a column of {right.Qualified}.");
            if (!compared.Any(c => string.Equals(c.Name, k, StringComparison.OrdinalIgnoreCase))) return (null, $"the key column `{k}` cannot be compared between the two tables ({skipped.FirstOrDefault(s => string.Equals(s.Item1, k, StringComparison.OrdinalIgnoreCase)).Item4}).");
            actualKey.Add(lk.Name);
        }
        return (new DiffPlan(leftEngine, left, right, actualKey, compared, skipped,
            left.Columns.Where(c => !rightByName.ContainsKey(c.Name)).Select(c => c.Name).ToList(), right.Columns.Where(c => !leftByName.ContainsKey(c.Name)).Select(c => c.Name).ToList()), null);
    }

    private static string? Incomparable(string leftEngine, string rightEngine, ColumnShape l, ColumnShape r)
    {
        var a = KindOf(leftEngine, l); var b = KindOf(rightEngine, r);
        if (a.Reason != null) return a.Reason;
        if (b.Reason != null) return b.Reason;
        if (a.Kind != b.Kind) return $"the types are of different kinds ({l.Type} and {r.Type})";
        if (a.Kind == "number" && Math.Max(a.IntegerDigits, b.IntegerDigits) + Math.Max(a.Scale, b.Scale) > 38) return "the two decimal types need more than 38 digits together";
        return null;
    }

    private static string Describe(ColumnShape c) =>
        c.Type + (c.Precision != null ? $"({c.Precision}{(c.Scale != null ? $",{c.Scale}" : "")})" : c.Length != null ? $"({(c.Length == -1 ? "max" : c.Length.ToString())})" : "");

    /// <summary>True when the two columns hold the same kind of value at the same scale: the native names may differ (`int` and `integer`), and that is no difference.</summary>
    public static bool SameType(DiffPlan p, string rightEngine, DiffColumn c)
    {
        var a = KindOf(p.Target, c.Left); var b = KindOf(rightEngine, c.Right);
        return a.Kind == b.Kind && a.Scale == b.Scale && (a.Kind != "text" || LengthOf(c.Left) == LengthOf(c.Right));
    }

    private static int? LengthOf(ColumnShape c) => c.Length is -1 or null ? null : c.Length;

    // ---- the canonical form ----

    private static string Q(string engine, string id) => TrackingDdl.For(engine).Quote(id);
    private static string Table(string engine, DiffTable t) => $"{Q(engine, t.SchemaName)}.{Q(engine, t.Name)}";

    private const string Utf8Collation = "Latin1_General_100_CI_AS_SC_UTF8";

    /// <summary>
    /// The value as text that is the same on every engine (not for NULL: a NULL stays NULL). Numbers are cast to the decimal scale the two sides share; text is exact (case and trailing spaces count; a
    /// fixed-length `char` is trimmed, since its padding is part of the type); timestamps have six digits of fraction (a seventh is rounded away); a timestamp with a zone is converted to UTC;
    /// uuids and binary values are lower-case hex.
    /// </summary>
    internal static string Canonical(string engine, string column, Kinded k, int scale)
    {
        var pg = engine == "postgres";
        return k.Kind switch
        {
            "number" => pg ? $"CAST(CAST({column} AS NUMERIC(38,{scale})) AS TEXT)" : $"CAST(CAST({column} AS DECIMAL(38,{scale})) AS VARCHAR(60))",
            "text" => pg ? $"CAST({(k.Padded ? $"RTRIM({column})" : column)} AS TEXT)" : $"CAST(CAST({(k.Padded ? $"RTRIM({column})" : column)} AS NVARCHAR(MAX)) COLLATE {Utf8Collation} AS VARCHAR(MAX))",
            "date" => pg ? $"to_char({column}, 'YYYY-MM-DD')" : $"CONVERT(VARCHAR(10), {column}, 23)",
            "timestamp" => pg ? $"to_char({column}, 'YYYY-MM-DD\"T\"HH24:MI:SS.US')" : Micro($"CAST({column} AS DATETIME2(6))"),
            "instant" => pg ? $"to_char({column} AT TIME ZONE 'UTC', 'YYYY-MM-DD\"T\"HH24:MI:SS.US')" : Micro($"CAST(SWITCHOFFSET({column}, '+00:00') AS DATETIME2(6))"),
            "time" => pg ? $"to_char({column}, 'HH24:MI:SS.US')" : $"CONVERT(VARCHAR(16), CAST({column} AS TIME(6)))",
            "bool" => pg ? $"CASE WHEN {column} THEN '1' ELSE '0' END" : $"CAST({column} AS VARCHAR(1))",
            "uuid" => pg ? $"LOWER(CAST({column} AS TEXT))" : $"LOWER(CAST({column} AS VARCHAR(36)))",
            _ => pg ? $"encode({column}, 'hex')" : $"LOWER(CONVERT(VARCHAR(MAX), {column}, 2))",
        };
    }

    /// <summary>A datetime2 as `yyyy-mm-ddThh:mm:ss.ffffff`: style 126 leaves the fraction out when it is zero, so it is written by hand.</summary>
    private static string Micro(string datetime2) =>
        $"CONVERT(VARCHAR(19), {datetime2}, 126) + '.' + RIGHT('000000' + CAST(DATEPART(MICROSECOND, {datetime2}) AS VARCHAR(6)), 6)";

    /// <summary>The value as it is hashed: a marker for NULL (so NULL differs from the empty string) and the canonical text otherwise.</summary>
    private static string Hashed(string engine, string column, Kinded k, int scale) =>
        engine == "postgres" ? $"CASE WHEN {column} IS NULL THEN 'N' ELSE 'V' || {Canonical(engine, column, k, scale)} END" : $"CASE WHEN {column} IS NULL THEN 'N' ELSE 'V' + {Canonical(engine, column, k, scale)} END";

    private static string Digest(string engine, string text) =>
        engine == "postgres" ? $"left(encode(sha256(convert_to({text}, 'UTF8')), 'hex'), 16)" : $"LOWER(CONVERT(VARCHAR(16), SUBSTRING(HASHBYTES('SHA2_256', {text}), 1, 8), 2))";

    /// <summary>The low 32 bits of a digest as a number, to be summed per bucket.</summary>
    private static string Low32(string engine, string digest) =>
        engine == "postgres" ? $"CAST(CAST('x' || substr({digest}, 1, 8) AS BIT(32)) AS BIGINT)" : $"CONVERT(BIGINT, CONVERT(VARBINARY(4), '0x' + SUBSTRING({digest}, 1, 8), 1))";

    private sealed record Side(string Engine, DiffTable Table, IReadOnlyList<string> Columns, IReadOnlyList<Kinded> Kinds, IReadOnlyList<int> Scales, IReadOnlyList<int> KeyIndexes);

    private static Side SideOf(DiffPlan p, string engine, bool left)
    {
        var t = left ? p.Left : p.Right;
        // the left side's engine is the plan's, the right side's the one given
        var own = p.Compared.Select(c => KindOf(left ? p.Target : engine, left ? c.Left : c.Right)).ToList();
        return new Side(left ? p.Target : engine, t, p.Compared.Select(c => left ? c.Left.Name : c.Right.Name).ToList(), own,
            p.Compared.Select(c => Math.Max(KindOf(p.Target, c.Left).Scale, KindOf(engine, c.Right).Scale)).ToList(),
            p.Key.Select(k => IndexOf(p, k)).ToList());
    }

    private static string KeyDigest(Side s)
    {
        var sep = s.Engine == "postgres" ? " || chr(31) || " : " + CHAR(31) + ";
        var parts = s.KeyIndexes.Select(i => $"({Hashed(s.Engine, Q(s.Engine, s.Columns[i]), s.Kinds[i], s.Scales[i])})");
        return Digest(s.Engine, string.Join(sep, parts));
    }

    /// <summary>The table as the key's digest and the digest of each compared column.</summary>
    private static string Digests(Side s, bool values = false)
    {
        var cols = new List<string> { $"{KeyDigest(s)} AS k" };
        for (var i = 0; i < s.Columns.Count; i++)
        {
            var q = Q(s.Engine, s.Columns[i]);
            cols.Add(values ? $"{Canonical(s.Engine, q, s.Kinds[i], s.Scales[i])} AS c{i}" : $"{Digest(s.Engine, Hashed(s.Engine, q, s.Kinds[i], s.Scales[i]))} AS c{i}");
        }
        return $"(SELECT {string.Join(", ", cols)} FROM {Table(s.Engine, s.Table)}) x";
    }

    internal static string BucketSql(DiffPlan p, string engine, bool left)
    {
        var s = SideOf(p, engine, left);
        var sums = string.Join(", ", Enumerable.Range(0, s.Columns.Count).Select(i => $"SUM({Low32(s.Engine, $"c{i}")}) AS s{i}"));
        return $"SELECT LEFT(k, 2) AS b, COUNT(*) AS n{(sums.Length > 0 ? ", " + sums : "")} FROM {Digests(s)} GROUP BY LEFT(k, 2)";
    }

    internal static string RowsSql(DiffPlan p, string engine, bool left, IReadOnlyCollection<string>? buckets)
    {
        var s = SideOf(p, engine, left);
        var where = buckets == null ? "" : $" WHERE LEFT(k, 2) IN ({string.Join(", ", buckets.Select(b => $"'{b}'"))})";
        return $"SELECT k, {string.Join(", ", Enumerable.Range(0, s.Columns.Count).Select(i => $"c{i}"))} FROM {Digests(s)}{where}";
    }

    internal static string ValuesSql(DiffPlan p, string engine, bool left, IReadOnlyCollection<string> keys)
    {
        var s = SideOf(p, engine, left);
        return $"SELECT k, {string.Join(", ", Enumerable.Range(0, s.Columns.Count).Select(i => $"c{i}"))} FROM {Digests(s, values: true)} WHERE k IN ({string.Join(", ", keys.Select(k => $"'{k}'"))})";
    }

    private static string DuplicateSql(DiffPlan p, string engine, bool left)
    {
        var s = SideOf(p, engine, left);
        var keys = string.Join(", ", s.KeyIndexes.Select(i => Q(s.Engine, s.Columns[i])));
        return $"SELECT COUNT(*) FROM (SELECT {keys} FROM {Table(s.Engine, s.Table)} GROUP BY {keys} HAVING COUNT(*) > 1) d";
    }

    // ---- running them ----

    private static readonly Regex HexKey = new("^[0-9a-f]{16}$", RegexOptions.Compiled);

    /// <param name="left">The session on the left table's connection (of engine <see cref="DiffPlan.Target"/>).</param>
    public static async Task<DiffOutcome> RunAsync(ReadSession left, ReadSession right, DiffPlan p, bool showValues, int limit, CancellationToken ct = default)
    {
        static long L(object? v) => v == null ? 0 : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        var rightEngine = right.Engine;
        async Task<long> Scalar(ReadSession s, string sql) => L((await s.QueryAsync(sql, null, ct)).Single()[0]);

        // the two sides are different servers: each step asks both at once
        var (leftRows, rightRows) = await Both(Scalar(left, $"SELECT COUNT(*) FROM {Table(p.Target, p.Left)}"), Scalar(right, $"SELECT COUNT(*) FROM {Table(rightEngine, p.Right)}"));
        var (dupLeft, dupRight) = await Both(Scalar(left, DuplicateSql(p, rightEngine, true)), Scalar(right, DuplicateSql(p, rightEngine, false)));
        if (dupLeft > 0 || dupRight > 0)
            return new DiffOutcome(leftRows, rightRows, dupLeft, dupRight, 0, 0, 0, 0, new Dictionary<string, long>(), [], false, [], [], []);

        // level one: a row count and a sum of digests per column for each bucket of keys
        var n = p.Compared.Count;
        async Task<Dictionary<string, long[]>> Buckets(ReadSession s, bool isLeft)
        {
            var map = new Dictionary<string, long[]>(StringComparer.Ordinal);
            foreach (var row in await s.QueryAsync(BucketSql(p, rightEngine, isLeft), null, ct))
                map[Convert.ToString(row[0], CultureInfo.InvariantCulture)!] = row.Skip(1).Select(L).ToArray();
            return map;
        }
        var (lb, rb) = await Both(Buckets(left, true), Buckets(right, false));
        var differing = lb.Keys.Union(rb.Keys).Where(b => !(lb.TryGetValue(b, out var x) && rb.TryGetValue(b, out var y) && x.SequenceEqual(y))).Order(StringComparer.Ordinal).ToList();

        // so many differences that fetching every row would take more memory than a comparison should: the buckets say which columns differ, and a narrower comparison (--columns) can go further
        var rowsToFetch = differing.Sum(b => Math.Max(lb.TryGetValue(b, out var x) ? x[0] : 0, rb.TryGetValue(b, out var y) ? y[0] : 0));
        if (rowsToFetch > MaxDrillRows)
        {
            var columns = Enumerable.Range(0, n).Where(i => differing.Any(b => (lb.TryGetValue(b, out var x) ? x[i + 1] : 0) != (rb.TryGetValue(b, out var y) ? y[i + 1] : 0))).Select(i => p.Compared[i].Name).ToList();
            var allBuckets = lb.Keys.Union(rb.Keys).Count();
            return new DiffOutcome(leftRows, rightRows, 0, 0, 0, 0, 0, 0, new Dictionary<string, long>(), [], true, [], [], []) { Partial = new PartialDiff(differing.Count, allBuckets, rowsToFetch, columns) };
        }

        // level two: the digests of each row of the buckets that differ
        var which = differing.Count == 0 ? null : differing.Count > BucketDrillLimit ? null : differing;
        async Task<Dictionary<string, string?[]>> Rows(ReadSession s, bool isLeft)
        {
            var map = new Dictionary<string, string?[]>(StringComparer.Ordinal);
            if (differing.Count == 0) return map;
            foreach (var row in await s.QueryAsync(RowsSql(p, rightEngine, isLeft, which), null, ct))
                map[Convert.ToString(row[0], CultureInfo.InvariantCulture)!] = row.Skip(1).Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)).ToArray();
            return map;
        }
        var (lr, rr) = await Both(Rows(left, true), Rows(right, false));

        var onlyLeftKeys = lr.Keys.Where(k => !rr.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var onlyRightKeys = rr.Keys.Where(k => !lr.ContainsKey(k)).Order(StringComparer.Ordinal).ToList();
        var byColumn = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var differingKeys = new List<string>();
        foreach (var k in lr.Keys.Where(rr.ContainsKey).Order(StringComparer.Ordinal))
        {
            var a = lr[k]; var b = rr[k];
            var any = false;
            for (var i = 0; i < n; i++)
            {
                if (a[i] == b[i]) continue;
                any = true;
                if (!p.Key.Contains(p.Compared[i].Name, StringComparer.OrdinalIgnoreCase)) byColumn[p.Compared[i].Name] = byColumn.GetValueOrDefault(p.Compared[i].Name) + 1;
            }
            if (any) differingKeys.Add(k);
        }
        // a key column cannot differ on a matched key, so a row counts as differing only through the other columns
        differingKeys = differingKeys.Where(k => Enumerable.Range(0, n).Any(i => lr[k][i] != rr[k][i] && !p.Key.Contains(p.Compared[i].Name, StringComparer.OrdinalIgnoreCase))).ToList();
        var matched = leftRows - onlyLeftKeys.Count;

        var leftSamples = new List<DiffSample>(); var rightSamples = new List<DiffSample>(); var diffSamples = new List<DifferingSample>();
        if (showValues && limit > 0)
        {
            var leftWant = onlyLeftKeys.Take(limit).Concat(differingKeys.Take(limit)).Distinct().ToList();
            var rightWant = onlyRightKeys.Take(limit).Concat(differingKeys.Take(limit)).Distinct().ToList();
            async Task<Dictionary<string, string?[]>> Values(ReadSession s, bool isLeft, List<string> keys)
            {
                var map = new Dictionary<string, string?[]>(StringComparer.Ordinal);
                if (keys.Count == 0 || keys.Any(k => !HexKey.IsMatch(k))) return map;
                foreach (var row in await s.QueryAsync(ValuesSql(p, rightEngine, isLeft, keys), null, ct))
                    map[Convert.ToString(row[0], CultureInfo.InvariantCulture)!] = row.Skip(1).Select(v => Cell(v)).ToArray();
                return map;
            }
            var (lv, rv) = await Both(Values(left, true, leftWant), Values(right, false, rightWant));
            DiffSample Sample(string?[] row)
            {
                var all = p.Compared.Select((c, i) => (c.Name, Value: row[i])).ToList();
                return new DiffSample(all.Where(x => p.Key.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToDictionary(x => x.Name, x => x.Value), all.Where(x => !p.Key.Contains(x.Name, StringComparer.OrdinalIgnoreCase)).ToDictionary(x => x.Name, x => x.Value));
            }
            foreach (var k in onlyLeftKeys.Take(limit)) if (lv.TryGetValue(k, out var row)) leftSamples.Add(Sample(row));
            foreach (var k in onlyRightKeys.Take(limit)) if (rv.TryGetValue(k, out var row)) rightSamples.Add(Sample(row));
            foreach (var k in differingKeys.Take(limit))
            {
                if (!lv.TryGetValue(k, out var a) || !rv.TryGetValue(k, out var b)) continue;
                var key = p.Key.ToDictionary(x => x, string? (x) => a[IndexOf(p, x)]);
                var cols = new Dictionary<string, (string?, string?)>();
                for (var i = 0; i < n; i++) if (a[i] != b[i] && !p.Key.Contains(p.Compared[i].Name, StringComparer.OrdinalIgnoreCase)) cols[p.Compared[i].Name] = (a[i], b[i]);
                diffSamples.Add(new DifferingSample(key, cols));
            }
        }
        return new DiffOutcome(leftRows, rightRows, 0, 0, onlyLeftKeys.Count, onlyRightKeys.Count, matched, differingKeys.Count, byColumn, [], true, leftSamples, rightSamples, diffSamples);
    }

    private static async Task<(T, U)> Both<T, U>(Task<T> a, Task<U> b) { await Task.WhenAll(a, b); return (a.Result, b.Result); }

    private static int IndexOf(DiffPlan p, string name) => p.Compared.Select((c, i) => (c, i)).First(x => string.Equals(x.c.Name, name, StringComparison.OrdinalIgnoreCase)).i;

    private static string? Cell(object? v) => v == null ? null : Convert.ToString(v, CultureInfo.InvariantCulture);
}
