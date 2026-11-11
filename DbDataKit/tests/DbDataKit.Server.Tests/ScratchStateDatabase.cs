using System.Data.Common;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.Server.Tests;

/// <summary>
/// A scratch database on the shared SQL Server or PostgreSQL (<c>DBDATAKIT_TEST_SQLSERVER</c>, <c>DBDATAKIT_TEST_POSTGRES</c>) for a test of a product's state settings. <see cref="Connection"/> is a connection string whose
/// password is a reference to the variable <see cref="PasswordVariable"/>, which <see cref="Environment"/> answers: the settings file written from it holds no password.
/// </summary>
internal sealed class ScratchStateDatabase : IDisposable
{
    public string PasswordVariable { get; } = "C07_STATE_PW_" + Guid.NewGuid().ToString("N")[..8];

    private readonly string _admin;
    private readonly string _name = $"c07_{Guid.NewGuid():N}";
    private readonly string _password;

    private ScratchStateDatabase(string engine, string admin)
    {
        // the same variable is set for the process, so a host built in the test (which reads the real environment) resolves the reference too
        Engine = engine;
        _admin = admin;
        Run(engine == "mssql" ? $"CREATE DATABASE [{_name}];" : $"CREATE DATABASE {_name};");
        if (engine == "mssql")
        {
            var builder = new SqlConnectionStringBuilder(admin) { InitialCatalog = _name };
            _password = builder.Password;
            builder.Password = "env:" + PasswordVariable;
            System.Environment.SetEnvironmentVariable(PasswordVariable, _password);
            Connection = builder.ConnectionString;
        }
        else
        {
            var builder = new NpgsqlConnectionStringBuilder(admin) { Database = _name };
            _password = builder.Password!;
            builder.Password = "env:" + PasswordVariable;
            System.Environment.SetEnvironmentVariable(PasswordVariable, _password);
            Connection = builder.ConnectionString;
        }
    }

    /// <summary><c>mssql</c> or <c>postgres</c>.</summary>
    public string Engine { get; }

    public string Connection { get; }

    /// <summary>Reads the one variable the connection refers to; everything else is unset.</summary>
    public string? Environment(string name) => name == PasswordVariable ? _password : null;

    /// <summary>The database on <paramref name="engine"/>'s server; the test is skipped when that engine is not set up.</summary>
    public static ScratchStateDatabase For(string engine)
    {
        var admin = System.Environment.GetEnvironmentVariable(engine == "mssql" ? "DBDATAKIT_TEST_SQLSERVER" : "DBDATAKIT_TEST_POSTGRES");
        if (admin is null)
        {
            Assert.False(string.Equals(System.Environment.GetEnvironmentVariable(engine == "mssql" ? "DBDATAKIT_TEST_REQUIRE_SQLSERVER" : "DBDATAKIT_TEST_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase), $"the engine is required (DBDATAKIT_TEST_REQUIRE_*) but DBDATAKIT_TEST_{(engine == "mssql" ? "SQLSERVER" : "POSTGRES")} is not set");
            Assert.Skip($"set DBDATAKIT_TEST_{(engine == "mssql" ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        }
        return new ScratchStateDatabase(engine, admin);
    }

    /// <summary>Whether the database has a table of this name, in any schema: what proves the state was written there.</summary>
    public long Tables(string like)
    {
        using System.Data.Common.DbConnection connection = Engine == "mssql" ? new SqlConnection(new SqlConnectionStringBuilder(_admin) { InitialCatalog = _name }.ConnectionString) : new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_admin) { Database = _name }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = Engine == "mssql" ? $"SELECT COUNT(*) FROM sys.tables WHERE name LIKE '{like}'" : $"SELECT COUNT(*) FROM information_schema.tables WHERE table_name LIKE '{like}'";
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private void Run(string sql)
    {
        using System.Data.Common.DbConnection connection = Engine == "mssql" ? new SqlConnection(_admin) : new NpgsqlConnection(_admin);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        System.Environment.SetEnvironmentVariable(PasswordVariable, null);
        SqlConnection.ClearAllPools();
        NpgsqlConnection.ClearAllPools();
        try { Run(Engine == "mssql" ? $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];" : $"DROP DATABASE IF EXISTS {_name} WITH (FORCE);"); }
        catch (Exception ex) when (ex is DbException) { }
    }
}
