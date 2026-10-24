using DbDataKit.Dialect;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>A real SqlClient failure is reported as a shared failure kind, not as SqlClient's text (plan 13, A10). Needs no server: nothing listens on the port.</summary>
[Collection("DriverFailures")]
public sealed class MsSqlFailureTextTests : IDisposable
{
    public MsSqlFailureTextTests() => DriverFailures.Configure([new(EngineId.SqlServer, MsSqlDriver.FailureMapper)], includeDetails: false);
    public void Dispose() => DriverFailures.Configure([], includeDetails: false);

    [Fact]
    public async Task A_refused_connection_is_a_network_failure_with_the_engines_code_and_none_of_its_text()
    {
        var config = new ConnectionConfig
        {
            Name = "closed", DriverType = DriverIds.MsSql, Host = "127.0.0.1", Port = 1, AuthMode = AuthMode.SqlAuth, UserId = "sa",
            ConnectTimeoutSeconds = 2, Properties = new() { ["TrustServerCertificate"] = "true" },
        };
        using var connection = new MsSqlDriver().CreateConnection(config, "pw");
        var ex = await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => connection.OpenAsync());

        var text = DriverFailures.Describe(ex, DriverIds.MsSql);
        Assert.StartsWith("The server could not be reached. [sqlserver", text);
        Assert.DoesNotContain("TCP Provider", text);
        Assert.DoesNotContain("127.0.0.1", text);
    }

}

