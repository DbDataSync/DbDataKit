using System.Data.Common;
using System.Data.Odbc;

namespace DbDataProxy.Data;

/// <summary>An ADO.NET provider the proxy can open connections with.</summary>
public interface IDbProvider
{
    /// <summary>The name used in configuration ("odbc", "sqlserver", "duckdb").</summary>
    string Name { get; }

    DbConnection CreateConnection(string connectionString);

    /// <summary>Returns the connection string with the caller's credentials injected (pass-through authentication).</summary>
    string ApplyCredentials(string connectionString, string user, string password);
}

public sealed class OdbcProvider : IDbProvider
{
    public string Name => "odbc";

    public DbConnection CreateConnection(string connectionString) => new OdbcConnection(connectionString);

    public string ApplyCredentials(string connectionString, string user, string password)
    {
        // OdbcConnectionStringBuilder brace-quotes values that contain special characters.
        var builder = new OdbcConnectionStringBuilder(connectionString);
        foreach (var key in new[] { "Trusted_Connection", "Integrated Security" })
            builder.Remove(key);
        builder["UID"] = user;
        builder["PWD"] = password;
        return builder.ConnectionString;
    }
}
