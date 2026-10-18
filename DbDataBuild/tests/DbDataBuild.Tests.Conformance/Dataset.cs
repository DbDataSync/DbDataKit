using System.Text;

namespace DbDataBuild.Tests.Conformance;

public sealed record Row(long Id, DateTime Ts, decimal? Amount, string? Label);

/// <summary>Synthetic, deterministic data with the edge cases the strategies must handle (DESIGN.md 15.2): boundary timestamps, equal timestamps, NULLs, non-ASCII text.</summary>
public static class Dataset
{
    private static DateTime T(string s) => DateTime.Parse(s, System.Globalization.CultureInfo.InvariantCulture);

    // the range used by delete_insert_by_range: [start, end)
    public static readonly DateTime RangeStart = T("2024-01-03 00:00:00");
    public static readonly DateTime RangeEnd = T("2024-01-05 00:00:00");

    public static readonly Row[] Source =
    [
        new(1, T("2024-01-01 00:00:00"), 10.00m, "a"),
        new(2, T("2024-01-02 12:00:00"), 20.50m, "b"),
        new(3, T("2024-01-03 00:00:00"), null, "c"),            // exactly the range start: included
        new(4, T("2024-01-04 08:30:15"), 40.00m, null),
        new(5, T("2024-01-04 23:59:59"), 50.00m, "é"),
        new(6, T("2024-01-05 00:00:00"), 60.00m, "f"),          // exactly the range end: excluded
        new(7, T("2024-01-06 00:00:00"), 70.00m, "g"),
        new(8, T("2024-01-06 00:00:00"), 80.00m, "h"),          // same timestamp as 7
        new(9, T("2024-01-07 10:00:00"), 90.25m, "i"),
        new(10, T("2024-01-08 00:00:00"), 100.00m, "j"),
    ];

    /// <summary>What the target holds before the first load: stale rows, an orphan in the range, an old row, and one on the range end.</summary>
    public static readonly Row[] Target0 =
    [
        new(2, T("2024-01-02 12:00:00"), 999.00m, "stale"),
        new(4, T("2024-01-04 08:30:15"), 999.00m, "stale"),
        new(99, T("2024-01-04 12:00:00"), 1.00m, "orphan"),     // in the range, not in the source
        new(98, T("2023-12-31 00:00:00"), 2.00m, "old"),
        new(6, T("2024-01-05 00:00:00"), 6.00m, "keepme"),      // on the range end
    ];

    /// <summary>Rows that arrive after the first load: one inside the watermark's lookback window, one long before it.</summary>
    public static readonly Row[] Late =
    [
        new(11, T("2024-01-07 05:00:00"), 11.00m, "late-in-window"),
        new(12, T("2024-01-01 06:00:00"), 12.00m, "late-old"),
    ];

    /// <summary>A row the target's CHECK constraint refuses, to make a script fail after it has started changing data.</summary>
    public static readonly Row Poison = new(666, T("2024-01-09 00:00:00"), 2_000_000m, "poison");

    public const string Body = "SELECT e.event_id, e.event_ts, e.amount, e.label FROM staging.events e";
    public const string Columns = "  - {name: event_id, type: BIGINT, nullable: false}\n  - {name: event_ts, type: TIMESTAMP, nullable: false}\n  - {name: amount, type: \"DECIMAL(14, 2)\"}\n  - {name: label, type: \"VARCHAR(20)\"}\n";

    public static async Task CreateAsync(Engine e, bool withCheck)
    {
        foreach (var schema in new[] { "staging", "marts" })
            await e.ExecAsync($"CREATE SCHEMA {e.QuoteIdent(schema)}");
        await CreateTable(e, "staging", "events", false);
        await CreateTable(e, "marts", "fct_events", withCheck);
    }

    private static Task CreateTable(Engine e, string schema, string table, bool check) =>
        e.ExecAsync($"CREATE TABLE {e.QuoteIdent(schema)}.{e.QuoteIdent(table)} (event_id {e.ColumnType("BIGINT")} NOT NULL, event_ts {e.ColumnType("TIMESTAMP")} NOT NULL, " +
                    $"amount {e.ColumnType("DECIMAL(14, 2)")} NULL, label {e.ColumnType("VARCHAR(20)")} NULL{(check ? ", CONSTRAINT chk_amount CHECK (amount IS NULL OR amount < 1000000)" : "")})");

    public static async Task InsertAsync(Engine e, string schema, string table, IEnumerable<Row> rows)
    {
        foreach (var r in rows)
            await e.RunScriptAsync($"INSERT INTO {e.QuoteIdent(schema)}.{e.QuoteIdent(table)} (event_id, event_ts, amount, label) VALUES (@id, @ts, @amount, @label)",
                new Dictionary<string, object?> { ["id"] = r.Id, ["ts"] = r.Ts, ["amount"] = new Typed(System.Data.DbType.Decimal, r.Amount), ["label"] = new Typed(System.Data.DbType.String, r.Label) });
    }

    public static Task<List<string>> TargetRowsAsync(Engine e) =>
        e.RowsAsync($"SELECT event_id, event_ts, amount, label FROM {e.QuoteIdent("marts")}.{e.QuoteIdent("fct_events")}");

    public static string SqlLiteral(Row r) =>
        $"({r.Id}, TIMESTAMP '{r.Ts:yyyy-MM-dd HH:mm:ss}', {(r.Amount is { } a ? a.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) : "NULL")}, {(r.Label is { } l ? "'" + l.Replace("'", "''") + "'" : "NULL")})";

    public static string Insert(string table, IEnumerable<Row> rows)
    {
        var sb = new StringBuilder();
        foreach (var r in rows) sb.Append($"INSERT INTO {table} VALUES {SqlLiteral(r)};\n");
        return sb.ToString();
    }
}
