using System.Globalization;
using System.Runtime.CompilerServices;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Verification;

/// <summary>A table as the verifier walks it: the columns as the snapshot's <see cref="StateTableDefinitions"/> read them from the migrations, and the key's columns in key order.</summary>
internal sealed record VerifiedTable(string Name, IReadOnlyList<SnapshotColumn> Columns, IReadOnlyList<SnapshotColumn> Key);

/// <summary>
/// The tables of a module for verification, and the order a table's rows are read in. What a column means and how a value is written are not decided here: the column types come from
/// <see cref="StateTableDefinitions"/> and the neutral values and the row hash from <see cref="SnapshotValues"/>, the same code the exporter uses, so the digest of a database equals the
/// <c>rows_digest</c> of a snapshot of it. This class only holds what a snapshot does not need: the order the two sides are walked together in.
/// </summary>
internal static class TableKeys
{
    private static readonly ConditionalWeakTable<StateNamespace, IReadOnlyDictionary<string, VerifiedTable>> Cache = [];

    public static IReadOnlyDictionary<string, VerifiedTable> For(StateNamespace ns) => Cache.GetValue(ns, Build);

    private static Dictionary<string, VerifiedTable> Build(StateNamespace ns) =>
        StateTableDefinitions.ReadFor(ns).ToDictionary(
            t => t.Name,
            t => new VerifiedTable(t.Name, t.Columns, [.. t.PrimaryKey.Select(k => t.Columns.First(c => c.Name == k))]),
            StringComparer.Ordinal);

    /// <summary>The key's neutral values (a <see cref="long"/> for an int, a <see cref="string"/> for everything else), compared by <see cref="Compare"/>.</summary>
    public static IReadOnlyList<object> KeyOf(IReadOnlyList<SnapshotColumn> key, object?[] rawKey) =>
        [.. key.Select((c, i) => c.Type == SnapshotColumnTypes.Int
            ? Convert.ToInt64(rawKey[i], CultureInfo.InvariantCulture)
            : (object)(SnapshotValues.Normalise(c.Type, rawKey[i]) as string ?? throw new InvalidOperationException($"A key column ({c.Name}) held a value that cannot be ordered.")))];

    public static int Compare(IReadOnlyList<object> a, IReadOnlyList<object> b)
    {
        for (var i = 0; i < a.Count; i++)
        {
            var c = a[i] is long x ? x.CompareTo((long)b[i]) : string.CompareOrdinal((string)a[i], (string)b[i]);
            if (c != 0) return c;
        }

        return 0;
    }

    public static string KeyText(IReadOnlyList<object> key) => string.Join("/", key.Select(k => Convert.ToString(k, CultureInfo.InvariantCulture)));

    /// <summary>The SQL expression a key column is ordered and paged by, the same on every engine: a uuid as lower-case text, text in the engine's binary collation, the rest as it is.</summary>
    public static string OrderExpression(StateDialect dialect, SnapshotColumn column) => column.Type switch
    {
        SnapshotColumnTypes.Uuid => Collate(dialect, dialect.UuidAsText(column.Name)),
        SnapshotColumnTypes.Text => Collate(dialect, column.Name),
        _ => column.Name,
    };

    private static string Collate(StateDialect dialect, string expression) => dialect.Engine switch
    {
        StateEngineIds.Postgres => expression + " COLLATE \"C\"",
        StateEngineIds.MsSql => expression + " COLLATE Latin1_General_100_BIN2",
        _ => expression,
    };

    /// <summary>The SQL expression for the byte length of a blob column, or null for an engine that is not built in.</summary>
    public static string? BlobLength(StateDialect dialect, string column) => dialect.Engine switch
    {
        StateEngineIds.Sqlite => $"length({column})",
        StateEngineIds.Postgres => $"octet_length({column})",
        StateEngineIds.MsSql => $"DATALENGTH({column})",
        _ => null,
    };
}
