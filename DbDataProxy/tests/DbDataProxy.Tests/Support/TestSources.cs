using DbDataKit.Connections;
using DbDataProxy.Data;

namespace DbDataProxy.Tests.Support;

/// <summary>Builds connection sources over the shared providers from a plain connection string, the way the server does for a configured data source.</summary>
public static class TestSources
{
    public static SourceConnection Duck(string connectionString, bool readOnly = false) =>
        SourceConnection.Create(new DbDataKit.Connections.DuckDb.DuckDbProvider(), "test", connectionString, readOnly);

    public static SourceConnection SqlClient(string connectionString) =>
        SourceConnection.Create(new DbDataKit.Connections.SqlServer.SqlServerProvider(), "test", connectionString);

    public static SourceConnection Postgres(string connectionString, bool readOnly = false) =>
        SourceConnection.Create(new DbDataKit.Connections.Postgres.PostgresProvider(), "test", connectionString, readOnly);

    public static SourceConnection Odbc(string connectionString) =>
        SourceConnection.Create(new DbDataKit.Connections.Odbc.OdbcProvider(), "test", connectionString);

    public static ProviderRegistry Registry() => new(
    [
        new DbDataKit.Connections.Odbc.OdbcProvider(),
        new DbDataKit.Connections.SqlServer.SqlServerProvider(),
        new DbDataKit.Connections.DuckDb.DuckDbProvider(),
        new DbDataKit.Connections.Postgres.PostgresProvider(),
        new DbDataKit.Connections.FlightSql.FlightSqlProvider(),
    ]);
}
