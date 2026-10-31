using Npgsql;

namespace DbDataProxy.Tests.Support;

/// <summary>
/// Where the PostgreSQL tests connect: the shared test engine (<c>testenv/testenv.sh up postgres</c>, then <c>eval "$(testenv/testenv.sh env dbdataproxy)"</c>)
/// as an Npgsql connection string in <c>DBPROXY_TEST_POSTGRES</c>. Without it the tests skip; with <c>DBPROXY_REQUIRE_POSTGRES=true</c> (what the map sets
/// when the engine was started) an unreachable server fails them, so CI cannot go green without really running them.
/// </summary>
public static class PostgresEnv
{
    private static readonly string? Raw = Environment.GetEnvironmentVariable("DBPROXY_TEST_POSTGRES");

    public static bool Required =>
        string.Equals(Environment.GetEnvironmentVariable("DBPROXY_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase);

    /// <summary>The connection string as the administrator of a data source writes it: the engine's TLS is a throwaway self-signed certificate, so the string says <c>SSL Mode=Require</c> (encrypted, not verified).</summary>
    public static string ConnectionString(int timeoutSeconds = 15) => $"{Raw};SSL Mode=Require;Timeout={timeoutSeconds}";

    public static NpgsqlConnectionStringBuilder Parts => new(Raw);

    /// <summary>The same server without a login, for a data source that uses the caller's own database credentials.</summary>
    public static string WithoutLogin() =>
        new NpgsqlConnectionStringBuilder(Raw) { Username = null, Password = null }.ConnectionString + ";SSL Mode=Require";

    /// <summary>Skips (or, when required, fails) unless the server answers.</summary>
    public static async Task EnsureReachableAsync()
    {
        if (string.IsNullOrEmpty(Raw))
        {
            if (Required) throw new InvalidOperationException("PostgreSQL is required (DBPROXY_REQUIRE_POSTGRES) but DBPROXY_TEST_POSTGRES is not set.");
            Assert.Skip("DBPROXY_TEST_POSTGRES is not set (testenv/testenv.sh up postgres, then eval \"$(testenv/testenv.sh env dbdataproxy)\").");
        }
        try
        {
            await using var probe = new NpgsqlConnection(ConnectionString());
            await probe.OpenAsync();
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            if (Required) throw new InvalidOperationException($"PostgreSQL is required but not reachable: {ex.Message}", ex);
            Assert.Skip($"PostgreSQL is not reachable: {ex.Message}");
        }
    }

    /// <summary>Runs setup statements as the test administrator, outside the proxy.</summary>
    public static async Task ExecuteAsync(params string[] statements)
    {
        await using var connection = new NpgsqlConnection(ConnectionString());
        await connection.OpenAsync();
        foreach (var sql in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }
    }
}
