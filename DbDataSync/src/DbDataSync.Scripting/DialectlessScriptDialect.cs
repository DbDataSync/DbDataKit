using DbDataKit.Dialect;
using DbDataKit.Sync.Core.Sql;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Scripting.Abstractions;

namespace DbDataSync.Scripting;

/// <summary>Stands in for a driver that names no dialect — ODBC and JDBC reaching an arbitrary engine.
/// Every member throws, so a script that needs one is told plainly rather than silently given SQL
/// Server's.</summary>
public sealed class DialectlessScriptDialect : IScriptDialect
{
    public static DialectlessScriptDialect Instance { get; } = new();

    public string EngineName => "Unknown";

    public string QuoteIdentifier(string identifier) => throw Unsupported();
    public string ParameterReference(string name) => throw Unsupported();
    public CanonicalType ToCanonicalType(string nativeType) => throw Unsupported();
    public RenderedColumnType RenderColumnType(CanonicalType type) => throw Unsupported();

    private static InvalidOperationException Unsupported() =>
        new("This connection's driver does not name a SQL dialect, so a script cannot generate SQL for it.");
}
