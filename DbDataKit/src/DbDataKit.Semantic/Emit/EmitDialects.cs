using DbDataKit.Dialect;

namespace DbDataKit.Semantic.Emit;

/// <summary>The shared dialects the SQL emitter writes: SQL Server's T-SQL for the source, and the DuckDB twin of the same query.</summary>
public static class EmitDialects
{
    /// <summary>T-SQL, the text sent to SQL Server (<c>NativeQuery.Dialect</c> is <c>tsql</c>).</summary>
    public static ISqlDialect TSql => DialectCatalog.Builtin.Get(EngineId.SqlServer);

    /// <summary>The DuckDB twin that runs on DuckDB and the conformance seed data (<c>duckdb</c>).</summary>
    public static ISqlDialect DuckDb => DialectCatalog.Builtin.Get(EngineId.DuckDb);
}
