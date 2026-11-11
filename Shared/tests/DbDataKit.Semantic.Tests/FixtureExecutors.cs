using System.Data.Common;
using DbDataKit.Semantic.Abstractions;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// Adds agent-written T-SQL (the level 0 fixtures) to an executor that does not run native text: the queries the engine plans go to
/// <paramref name="inner"/> (the connections executor), the agent's text runs straight on SQL Server. Test code only; a host puts its
/// statement classifier in front of native text instead.
/// </summary>
public sealed class NativeTextTestExecutor(ISourceExecutor inner, string sqlServerConnectionString) : ISourceExecutor
{
    public bool RunsNativeText => true;

    public QueryRows Execute(CompiledQuery query, int maxRows) => inner.Execute(query, maxRows);

    public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows)
    {
        if (native.Dialect != "tsql")
            throw new NotSupportedException($"{native.Dialect} execution isn't available on this executor.");
        using var con = new SqlConnection(sqlServerConnectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = native.Text;
        return QueryRowReader.Read(cmd, maxRows);
    }

    /// <summary>Loads a T-SQL seed script: DROP statements first, then everything else (SQL Server compiles a whole batch before running any of it).</summary>
    public static void LoadSeed(string connectionString, string seedTsql)
    {
        using var con = new SqlConnection(connectionString);
        con.Open();
        var lines = seedTsql.Split('\n');
        foreach (var batch in new[]
        {
            string.Join('\n', lines.Where(l => l.TrimStart().StartsWith("DROP TABLE", StringComparison.OrdinalIgnoreCase))),
            string.Join('\n', lines.Where(l => !l.TrimStart().StartsWith("DROP TABLE", StringComparison.OrdinalIgnoreCase))),
        })
        {
            if (string.IsNullOrWhiteSpace(batch)) continue;
            using var cmd = con.CreateCommand();
            cmd.CommandText = batch;
            cmd.ExecuteNonQuery();
        }
    }
}

/// <summary>Seeds the scratch PostgreSQL database. The conformance seed script is plain SQL that PostgreSQL runs as it is.</summary>
public static class PostgresSeed
{
    public static void Load(string connectionString, string seedSql)
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = seedSql;
        cmd.ExecuteNonQuery();
    }
}
