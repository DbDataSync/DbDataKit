using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
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

/// <summary>
/// Runs the DuckDB-dialect twin of each query on a real PostgreSQL, until the engine has a PostgreSQL emitter (J05). The twin is
/// plain SQL for the most part; the positional <c>?</c> marks become <c>$n</c>. Anything that is not the twin's dialect goes to the fallback.
/// </summary>
public sealed class PostgresTwinExecutor(string connectionString, ISourceExecutor fallback) : ISourceExecutor
{
    public bool RunsNativeText => false;

    public QueryRows Execute(CompiledQuery query, int maxRows)
    {
        if (query.Native.Dialect == "dax") return fallback.Execute(query, maxRows);
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = Positional(query.DuckDbSql);
        foreach (var p in query.DuckDbParameters) cmd.Parameters.Add(new NpgsqlParameter { Value = p ?? DBNull.Value });
        return QueryRowReader.Read(cmd, maxRows);
    }

    public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows) =>
        throw new NotSupportedException("The PostgreSQL twin executor can't run agent-written text.");

    /// <summary>Replaces each <c>?</c> outside a quoted string or identifier with <c>$1</c>, <c>$2</c>, ...</summary>
    public static string Positional(string sql)
    {
        var sb = new StringBuilder();
        int n = 0;
        char quote = '\0';
        foreach (var c in sql)
        {
            if (quote != '\0') { if (c == quote) quote = '\0'; sb.Append(c); }
            else if (c is '\'' or '"') { quote = c; sb.Append(c); }
            else if (c == '?') sb.Append('$').Append(++n);
            else sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Loads the DuckDB-dialect seed script as it is: it is plain SQL.</summary>
    public static void LoadSeed(string connectionString, string seedSql)
    {
        using var con = new NpgsqlConnection(connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = seedSql;
        cmd.ExecuteNonQuery();
    }
}
