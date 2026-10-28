using DbDataKit.Connections;
using DbDataKit.Connections.DuckDb;
using DbDataKit.Connections.Postgres;
using DbDataKit.Connections.SqlServer;
using DbDataKit.Secrets;
using DbDataKit.Semantic.Connections;
using Npgsql;

namespace DbDataKit.Semantic.Tests;

/// <summary>The shared connections the fixtures run through: a read context on a SQL Server database (from the testenv variables) and on a DuckDB file.</summary>
public static class ConnectionContexts
{
    /// <summary>A read-only SQL Server connection to <paramref name="database"/> on the server <c>DBDATA_TEST_SQLSERVER_SERVER</c> (host,port), as the testenv user.</summary>
    public static SemanticSource SqlServer(string database)
    {
        var server = Environment.GetEnvironmentVariable("DBDATA_TEST_SQLSERVER_SERVER")!.Split(',');
        var definition = new ConnectionDefinition
        {
            Name = "semantic_test_mssql", Engine = "sqlserver", Access = AccessMode.Readonly,
            Address = new AddressSpec { Host = server[0], Port = server.Length > 1 ? int.Parse(server[1]) : null, Database = database },
            ReadLogin = new LoginSpec { Mode = AuthMode.Sql, User = Environment.GetEnvironmentVariable("DBDATA_TEST_SQLSERVER_USER") },
            // the shared test engine has a self-signed certificate and the suite's other tests connect without encryption
            Tls = new TlsSpec { Encrypt = false, TrustServerCertificate = true },
        };
        var login = new ResolvedLogin(LoginRole.Read, AuthMode.Sql, "env:DBDATA_TEST_SQLSERVER_PASSWORD")
        {
            UserName = definition.ReadLogin.User,
            Password = SecretValue.From(Environment.GetEnvironmentVariable("DBDATA_TEST_SQLSERVER_PASSWORD")!),
        };
        var provider = new SqlServerProvider();
        return new SemanticSource(provider, new ConnectionContext { Definition = definition, Login = login, Role = LoginRole.Read });
    }

    /// <summary>A read-only DuckDB connection to the database file (opened READ_ONLY, in the sandbox).</summary>
    public static SemanticSource DuckDb(string file)
    {
        var definition = new ConnectionDefinition
        {
            Name = "semantic_test_duckdb", Engine = "duckdb", Access = AccessMode.Readonly, Address = new AddressSpec { Database = file },
        };
        return new SemanticSource(new DuckDbProvider(), new ConnectionContext { Definition = definition, Role = LoginRole.Read });
    }

    /// <summary>A read-only PostgreSQL connection to the scratch database named by <paramref name="connectionString"/> (the Npgsql string of <c>DBDATA_TEST_POSTGRES</c> with that database).</summary>
    public static SemanticSource Postgres(string connectionString)
    {
        var b = new NpgsqlConnectionStringBuilder(connectionString);
        var definition = new ConnectionDefinition
        {
            Name = "semantic_test_postgres", Engine = "postgres", Access = AccessMode.Readonly,
            Address = new AddressSpec { Host = b.Host, Port = b.Port, Database = b.Database },
            ReadLogin = new LoginSpec { Mode = AuthMode.Sql, User = b.Username },
            // the shared test engine has no certificate
            Tls = new TlsSpec { Encrypt = false },
        };
        var login = new ResolvedLogin(LoginRole.Read, AuthMode.Sql, "env:DBDATA_TEST_POSTGRES")
        {
            UserName = b.Username,
            Password = b.Password is { Length: > 0 } password ? SecretValue.From(password) : null,
        };
        return new SemanticSource(new PostgresProvider(), new ConnectionContext { Definition = definition, Login = login, Role = LoginRole.Read });
    }
}
