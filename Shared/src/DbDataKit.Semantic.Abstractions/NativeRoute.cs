namespace DbDataKit.Semantic.Abstractions;

/// <summary>The engines a semantic source query can run on: the text the engine planned for each is different.</summary>
public enum SourceEngine { SqlServer, Postgres, DuckDb }

/// <summary>The text to run for a source query, the values its markers stand for, and whether the markers are named (<c>@p1</c>) or positional.</summary>
public sealed record RoutedText(string Text, IReadOnlyList<object?> Parameters, bool Named);

/// <summary>
/// Which of a compiled query's texts an engine runs, so every executor (the connections' and a serving host's) chooses the same: SQL Server runs the T-SQL of the query, PostgreSQL its own text whatever kind the catalog gives the source
/// (its tables are expected on the connection), DuckDB the twin.
/// </summary>
public static class NativeRoute
{
    /// <summary>The text for <paramref name="engine"/>, or null with <paramref name="why"/> when the query has none for it.</summary>
    public static RoutedText? For(SourceEngine engine, CompiledQuery query, out string? why)
    {
        why = null;
        switch (engine)
        {
            case SourceEngine.SqlServer:
                if (query.Native.Dialect == "tsql") return new(query.ExecText ?? query.Native.Text, query.DuckDbParameters, Named: true);
                why = $"cannot run {query.Native.Dialect} on source '{query.Source}' (SQL Server)";
                return null;
            case SourceEngine.Postgres:
                if (query.Postgres is not null) return new(query.Postgres.Text, query.Postgres.Parameters, Named: false);
                why = $"cannot run {query.Native.Dialect} on source '{query.Source}' (PostgreSQL)";
                return null;
            default:
                return new(query.DuckDbSql, query.DuckDbParameters.Select(DuckDbValue).ToList(), Named: false);
        }
    }

    /// <summary>
    /// DuckDB.NET sends a decimal parameter as text, which DuckDB then won't compare with a number. A whole number goes as a long and
    /// the rest as a double, so the same parameters stay exact for SQL Server.
    /// </summary>
    private static object? DuckDbValue(object? v) =>
        v is decimal d ? (d == Math.Truncate(d) && Math.Abs(d) < long.MaxValue ? (long)d : (double)d) : v;
}
