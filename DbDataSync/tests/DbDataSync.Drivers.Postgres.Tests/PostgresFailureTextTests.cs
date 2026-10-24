using DbDataKit.Dialect;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using Xunit;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>A real SqlClient failure is reported as a shared failure kind, not as SqlClient's text (plan 13, A10). Needs no server: nothing listens on the port.</summary>
[Collection("DriverFailures")]
public sealed class PostgresFailureTextTests : IDisposable
{
    public PostgresFailureTextTests() => DriverFailures.Configure([new(EngineId.Postgres, PostgresDriver.FailureMapper)], includeDetails: false);
    public void Dispose() => DriverFailures.Configure([], includeDetails: false);

    [Fact]
    public async Task A_refused_connection_is_a_network_failure_with_the_engines_code_and_none_of_its_text()
    {
        var config = new ConnectionConfig
        {
            Name = "closed", DriverType = DriverIds.Postgres, Host = "127.0.0.1", Port = 1, AuthMode = AuthMode.SqlAuth, UserId = "u",
            ConnectTimeoutSeconds = 2, Properties = new() { ["SSL Mode"] = "Disable" },
        };
        using var connection = new PostgresDriver().CreateConnection(config, "pw");
        var ex = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => connection.OpenAsync());

        var text = DriverFailures.Describe(ex, DriverIds.Postgres);
        Assert.StartsWith("The server could not be reached. [postgres", text);
        Assert.DoesNotContain("TCP Provider", text);
        Assert.DoesNotContain("127.0.0.1", text);
    }

}

