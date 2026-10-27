using System.Data.Common;
using System.Text;
using System.Text.RegularExpressions;
using DbDataKit.Semantic.Abstractions;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.Semantic.Tests;

/// <summary>
/// Runs the T-SQL the engine emits (and agent-written T-SQL) on a real SQL Server, for the fixture job. Sources that are not
/// T-SQL, such as the SSAS model, go to the fallback (the DuckDB stand-in). Test code: J03 replaces it with the executor over
/// <c>DbDataKit.Connections</c>.
/// </summary>
public sealed class SqlServerFixtureExecutor(string connectionString, ISourceExecutor fallback) : ISourceExecutor
{
    public bool RunsNativeText => true;

    public QueryRows Execute(CompiledQuery query, int maxRows)
    {
        if (query.Native.Dialect != "tsql") return fallback.Execute(query, maxRows);
        using var con = new SqlConnection(connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = query.ExecText ?? query.Native.Text;
        // Emitted text uses @p1..@pN in the same order as the positional DuckDB parameters.
        for (int i = 0; i < query.DuckDbParameters.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i + 1}", query.DuckDbParameters[i] ?? DBNull.Value);
        return DuckDbRows.Read(cmd, maxRows);
    }

    public QueryRows ExecuteNativeText(string source, NativeQuery native, int maxRows)
    {
        if (native.Dialect != "tsql")
            throw new NotSupportedException($"{native.Dialect} execution isn't available on this executor.");
        using var con = new SqlConnection(connectionString);
        con.Open();
        using var cmd = con.CreateCommand();
        cmd.CommandText = native.Text;
        return DuckDbRows.Read(cmd, maxRows);
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
        return DuckDbRows.Read(cmd, maxRows);
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
