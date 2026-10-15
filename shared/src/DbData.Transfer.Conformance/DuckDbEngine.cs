using DbData.Dialect;
using DuckDB.NET.Data;

namespace DbData.Transfer.Conformance;

/// <summary>The in-process reference engine: DuckDB in memory. Each <c>OpenAsync</c> is a new empty database.</summary>
public static class DuckDbEngine
{
    public static ConformanceEngine Create() => new("duckdb", DialectCatalog.For(EngineId.DuckDb), null, async ct =>
    {
        var c = new DuckDBConnection("DataSource=:memory:");
        await c.OpenAsync(ct);
        return c;
    });
}
