using Microsoft.Data.SqlClient;

namespace DbDataGateway.Tests.Support;

/// <summary>
/// Where the SQL Server tests connect. Defaults to a local SQL Express with Windows authentication. CI points them at a
/// Docker container with DBDATAKIT_TEST_MSSQL_SERVER (host or host,port), DBDATAKIT_TEST_MSSQL_USER and _PASSWORD
/// (SQL authentication). With DBDATAKIT_TEST_REQUIRE_SQLSERVER=true an unreachable server fails the tests instead of skipping
/// them, so CI cannot go green without really running them.
/// </summary>
public static class SqlServerEnv
{
    public static readonly string Server = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_MSSQL_SERVER") ?? @".\SQLEXPRESS";
    public static readonly string? User = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_MSSQL_USER");
    public static readonly string? Password = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_MSSQL_PASSWORD");

    public static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable("DBDATAKIT_TEST_REQUIRE_SQLSERVER"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool UsesSqlAuthentication => !string.IsNullOrEmpty(User);

    public static string SqlClient(int timeoutSeconds = 3) =>
        $"Server={Server};{(UsesSqlAuthentication ? $"User Id={User};Password={Password}" : "Integrated Security=True")};" +
        $"TrustServerCertificate=True;Encrypt=False;Connect Timeout={timeoutSeconds}";

    /// <summary>Connection string without any login, for data sources that use the caller's own database credentials.</summary>
    public static string SqlClientWithoutLogin() => $"Server={Server};TrustServerCertificate=True;Encrypt=False";

    /// <summary>The same server for an ODBC driver, e.g. "ODBC Driver 17 for SQL Server".</summary>
    public static string Odbc(string driver) =>
        $"Driver={{{driver}}};Server={Server};{(UsesSqlAuthentication ? $"UID={User};PWD={Password}" : "Trusted_Connection=Yes")};TrustServerCertificate=Yes";

    /// <summary>Skips (or, when required, fails) unless the server answers.</summary>
    public static async Task EnsureReachableAsync()
    {
        try
        {
            await using var probe = new SqlConnection(SqlClient());
            await probe.OpenAsync();
        }
        catch (Exception ex) when (ex is SqlException or InvalidOperationException)
        {
            if (Required) throw new InvalidOperationException($"SQL Server '{Server}' is required but not reachable: {ex.Message}", ex);
            Assert.Skip($"SQL Server '{Server}' is not reachable: {ex.Message}");
        }
    }
}
