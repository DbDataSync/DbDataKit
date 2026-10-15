using DbData.Dialect;
using DbData.Dialect.Conformance;
using Xunit;

namespace DbData.Dialect.Conformance.Tests;

// Offline: one subclass per built-in dialect.
public sealed class AnsiConformance : DialectConformanceTests { protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("ansi"); }
public sealed class SqlServerConformance : DialectConformanceTests { protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("sqlserver"); }
public sealed class PostgresConformance : DialectConformanceTests { protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("postgres"); }
public sealed class MySqlConformance : DialectConformanceTests { protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("mysql"); }
public sealed class OracleConformance : DialectConformanceTests { protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("oracle"); }
public sealed class DuckDbConformance : DialectConformanceTests { protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("duckdb"); }
public sealed class FabricConformance : DialectConformanceTests { protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("fabric"); }

public sealed class OdbcGenericConformance : DialectConformanceTests
{
    protected override ISqlDialect CreateDialect() => OdbcDialect.FromFacts(new OdbcFacts("Generic"));
}

public sealed class OdbcBracketConformance : DialectConformanceTests
{
    protected override ISqlDialect CreateDialect() => OdbcDialect.FromFacts(new OdbcFacts("Brackets", IdentifierQuoteChar: "[", SupportsTop: true));
}

// Live: skipped unless the env var holds a connection string. No driver ships in shared/, so Connect is a stub that
// product repos (or a docker fixture) replace by deriving and overriding it.
public abstract class StubLive : LiveDialectConformanceTests
{
    protected override Task<ILiveEngine> Connect(string connectionString)
        => throw new NotSupportedException($"{EnvironmentVariable} is set but no driver is wired: derive from this class and implement Connect.");
}

public sealed class SqlServerLive : StubLive { protected override string EnvironmentVariable => "DBDATA_CONFORMANCE_SQLSERVER"; protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("sqlserver"); }
public sealed class PostgresLive : StubLive { protected override string EnvironmentVariable => "DBDATA_CONFORMANCE_POSTGRES"; protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("postgres"); }
public sealed class MySqlLive : StubLive { protected override string EnvironmentVariable => "DBDATA_CONFORMANCE_MYSQL"; protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("mysql"); }
public sealed class OracleLive : StubLive
{
    protected override string EnvironmentVariable => "DBDATA_CONFORMANCE_ORACLE";
    protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("oracle");
    protected override string SelectExpression(string expression) => $"SELECT {expression} FROM dual";
}
public sealed class DuckDbLive : StubLive { protected override string EnvironmentVariable => "DBDATA_CONFORMANCE_DUCKDB"; protected override ISqlDialect CreateDialect() => DialectCatalog.Builtin.Get("duckdb"); }
