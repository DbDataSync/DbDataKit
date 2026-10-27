using DbDataKit.Semantic.Abstractions;
using DuckDB.NET.Data;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// Conformance stand-in: runs the DuckDB equivalent of each compiled query against the seed data. The engines
/// run <c>CompiledQuery.Native</c> instead, through the executors over <c>DbDataKit.Connections</c>.
/// </summary>
public sealed class DuckDbStandInExecutor : IColumnarSourceExecutor, IDisposable
{
    private readonly DuckDBConnection _con;

    public DuckDbStandInExecutor(string seedSql)
    {
        _con = new DuckDBConnection("DataSource=:memory:");
        _con.Open();
        using var cmd = _con.CreateCommand();
        cmd.CommandText = seedSql;
        cmd.ExecuteNonQuery();
    }

    public bool RunsNativeText => false;

    public QueryRows Execute(CompiledQuery query, int maxRows)
    {
        using var cmd = Command(query);
        return QueryRowReader.Read(cmd, maxRows);
    }

    public ColumnarOutcome ExecuteColumnar(CompiledQuery query, int maxRows)
    {
        using var cmd = Command(query);
        return QueryRowReader.ReadColumnar(cmd, maxRows);
    }

    private DuckDBCommand Command(CompiledQuery query)
    {
        var cmd = _con.CreateCommand();
        cmd.CommandText = query.DuckDbSql;
        foreach (var p in query.DuckDbParameters)
            cmd.Parameters.Add(new DuckDBParameter(DuckDbValue(p) ?? DBNull.Value));
        return cmd;
    }

    /// <summary>
    /// DuckDB.NET sends a decimal parameter as text, which DuckDB then won't compare with a number. A whole number goes as a long and
    /// the rest as a double, here, so the same parameters stay exact for SQL Server.
    /// </summary>
    private static object? DuckDbValue(object? v) =>
        v is decimal d ? (d == Math.Truncate(d) && Math.Abs(d) < long.MaxValue ? (long)d : (double)d) : v;

    public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows) =>
        throw new NotSupportedException(
            "The DuckDB stand-in can't run agent-written T-SQL or DAX. Use a SQL Server test container for level 0 fixtures.");

    public void Dispose() => _con.Dispose();
}
