using System.Data.Common;
using DbDataProxy.Data;
using DuckDB.NET.Data;
using Microsoft.Data.SqlClient;

namespace DbDataProxy.Server.Data;

public sealed class SqlServerProvider : IDbProvider
{
    public string Name => "sqlserver";

    public ParameterStyle ParameterStyle => ParameterStyle.NamedAtSign;

    public DbDataKit.Dialect.ISqlDialect Dialect => ProviderDialects.SqlServer;

    public DbConnection CreateConnection(string connectionString) => new SqlConnection(connectionString);

    public string ApplyCredentials(string connectionString, string user, string password)
    {
        var builder = new SqlConnectionStringBuilder(connectionString)
        {
            IntegratedSecurity = false,
            UserID = user,
            Password = password,
        };
        return builder.ConnectionString;
    }
}

public sealed class DuckDbProvider : IDbProvider
{
    public string Name => "duckdb";

    public DbDataKit.Dialect.ISqlDialect Dialect => ProviderDialects.DuckDb;

    public DbConnection CreateConnection(string connectionString) => new DuckDBConnection(connectionString);

    public string ApplyCredentials(string connectionString, string user, string password) =>
        throw new NotSupportedException("DuckDB has no user authentication, so it cannot be a pass-through data source.");
}
