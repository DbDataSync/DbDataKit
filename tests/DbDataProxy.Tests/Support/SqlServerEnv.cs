using Microsoft.Data.SqlClient;

namespace DbDataProxy.Tests.Support;

/// <summary>
/// Where the SQL Server tests connect. Defaults to a local SQL Express with Windows authentication. CI points them at a
/// Docker container with DBPROXY_TEST_SQLSERVER (host or host,port), DBPROXY_TEST_SQLSERVER_USER and _PASSWORD
/// (SQL authentication). With DBPROXY_REQUIRE_SQLSERVER=true an unreachable server fails the tests instead of skipping
/// them, so CI cannot go green without really running them.
/// </summary>
public static class SqlServerEnv
{
    public static readonly string Server = Environment.GetEnvironmentVariable("DBPROXY_TEST_SQLSERVER") ?? @".\SQLEXPRESS";
    public static readonly string? User = Environment.GetEnvironmentVariable("DBPROXY_TEST_SQLSERVER_USER");
    public static readonly string? Password = Environment.GetEnvironmentVariable("DBPROXY_TEST_SQLSERVER_PASSWORD");

    public static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable("DBPROXY_REQUIRE_SQLSERVER"), "true", StringComparison.OrdinalIgnoreCase);

    public static bool UsesSqlAuthentication => !string.IsNullOrEmpty(User);

    public static string SqlClient(int timeoutSeconds = 3) =>
        $"Server={Server};{(UsesSqlAuthentication ? $"User Id={User};Password={Password}" : "Integrated Security=True")};" +
        $"TrustServerCertificate=True;Encrypt=False;Connect Timeout={timeoutSeconds}";

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
