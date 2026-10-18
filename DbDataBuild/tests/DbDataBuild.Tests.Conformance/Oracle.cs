using DbDataBuild.Tests.Conformance;
using DuckDB.NET.Data;

namespace DbDataBuild.Tests.Conformance;

public enum OracleKind { FullReplace, DeleteInsertByKey, MergeByKey, DeleteInsertByRange, WatermarkAppend }

/// <param name="Lookback">For WatermarkAppend: null appends only rows newer than the watermark; a span replaces the window from the watermark.</param>
public sealed record OracleSpec(OracleKind Kind, TimeSpan? Lookback = null, DateTime? Start = null, DateTime? End = null);

/// <summary>
/// The DuckDB reference implementation of each strategy (DESIGN.md 6.6): what the target should hold after a load. Written in plain DuckDB
/// SQL over copies of the source and target, independently of the T-SQL and PostgreSQL templates, so the two can be compared.
/// </summary>
public sealed class Oracle : IDisposable
{
    private readonly DuckDBConnection connection = new("DataSource=:memory:");

    public Oracle(IEnumerable<Row> source, IEnumerable<Row> target)
    {
        connection.Open();
        Run("CREATE TABLE src (event_id BIGINT, event_ts TIMESTAMP, amount DECIMAL(14, 2), label VARCHAR(20))");
        Run("CREATE TABLE tgt (event_id BIGINT, event_ts TIMESTAMP, amount DECIMAL(14, 2), label VARCHAR(20))");
        Run(Dataset.Insert("src", source));
        Run(Dataset.Insert("tgt", target));
    }

    public void AddSource(IEnumerable<Row> rows) => Run(Dataset.Insert("src", rows));

    /// <summary>What the watermark resolver should return now: MAX(event_ts) in the target, less the lookback.</summary>
    public DateTime? Watermark(TimeSpan? lookback)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT max(event_ts) FROM tgt";
        var v = cmd.ExecuteScalar();
        return v is DateTime dt ? dt - (lookback ?? TimeSpan.Zero) : null;
    }

    public void Apply(OracleSpec spec)
    {
        switch (spec.Kind)
        {
            case OracleKind.FullReplace:
                Run("DELETE FROM tgt; INSERT INTO tgt SELECT * FROM src;");
                break;
            case OracleKind.DeleteInsertByKey:
                Run("DELETE FROM tgt WHERE event_id IN (SELECT event_id FROM src); INSERT INTO tgt SELECT * FROM src;");
                break;
            case OracleKind.MergeByKey:
                Run("UPDATE tgt SET event_ts = s.event_ts, amount = s.amount, label = s.label FROM src s WHERE tgt.event_id = s.event_id;" +
                    "INSERT INTO tgt SELECT * FROM src s WHERE s.event_id NOT IN (SELECT event_id FROM tgt);");
                break;
            case OracleKind.DeleteInsertByRange:
            {
                var (a, b) = (Ts(spec.Start!.Value), Ts(spec.End!.Value));
                Run($"DELETE FROM tgt WHERE event_ts >= {a} AND event_ts < {b}; INSERT INTO tgt SELECT * FROM src WHERE event_ts >= {a} AND event_ts < {b};");
                break;
            }
            case OracleKind.WatermarkAppend:
            {
                var wm = Watermark(spec.Lookback) ?? throw new InvalidOperationException("The oracle needs a non-empty target to take a watermark from.");
                if (spec.Lookback == null) Run($"INSERT INTO tgt SELECT * FROM src WHERE event_ts > {Ts(wm)};");
                else Run($"DELETE FROM tgt WHERE event_ts >= {Ts(wm)}; INSERT INTO tgt SELECT * FROM src WHERE event_ts >= {Ts(wm)};");
                break;
            }
        }
    }

    public List<string> Target()
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT event_id, event_ts, amount, label FROM tgt";
        using var rd = cmd.ExecuteReader();
        var rows = new List<string>();
        while (rd.Read())
            rows.Add(string.Join("|", Enumerable.Range(0, rd.FieldCount).Select(i => Normalize.Value(rd.IsDBNull(i) ? null : rd.GetValue(i), rd.GetDataTypeName(i)))));
        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static string Ts(DateTime t) => $"TIMESTAMP '{t:yyyy-MM-dd HH:mm:ss}'";

    private void Run(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return;
        using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public void Dispose() => connection.Dispose();
}
