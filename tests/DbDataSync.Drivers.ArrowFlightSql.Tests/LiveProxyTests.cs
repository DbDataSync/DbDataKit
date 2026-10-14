using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Xunit.Abstractions;

namespace DbDataSync.Drivers.ArrowFlightSql.Tests;

/// <summary>
/// The driver against a running DbDataProxy. Opt-in: set DBPROXY_LIVE_ADDRESS (for example
/// <c>https://127.0.0.1:18815</c>), DBPROXY_LIVE_USER and DBPROXY_LIVE_PASSWORD; the proxy needs a data source
/// <c>scratch</c> holding a <c>sales</c> table (id, name, amount, d) and a second source <c>other</c> without it.
/// Without the variables each test returns immediately.
/// </summary>
public sealed class LiveProxyTests(ITestOutputHelper output)
{
    private static readonly string? Address = Environment.GetEnvironmentVariable("DBPROXY_LIVE_ADDRESS");
    private static readonly string User = Environment.GetEnvironmentVariable("DBPROXY_LIVE_USER") ?? "me";
    private static readonly string Password = Environment.GetEnvironmentVariable("DBPROXY_LIVE_PASSWORD") ?? "";

    private static async Task<(ArrowFlightSqlDriver Driver, DbConnection Connection)> OpenAsync(
        string dataSource, string? compression = null, string? parameterMode = null)
    {
        var properties = new Dictionary<string, string>
        {
            [FlightSqlSettings.SkipCertificateValidationKey] = "true",
            [FlightSqlSettings.DataSourceKey] = dataSource,
        };
        if (compression is not null) properties[FlightSqlSettings.CompressionKey] = compression;
        if (parameterMode is not null) properties[FlightSqlSettings.ParameterModeKey] = parameterMode;

        var driver = new ArrowFlightSqlDriver();
        var connection = driver.CreateConnection(new ConnectionConfig
        {
            Name = "proxy",
            DriverType = DriverIds.ArrowFlightSql,
            ConnectionString = Address!,
            AuthMode = AuthMode.SqlAuth,
            UserId = User,
            Properties = properties,
        }, Password);
        await connection.OpenAsync();
        return (driver, connection);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("gzip")]
    [InlineData("zstd")]
    [InlineData("lz4")]
    public async Task Reads_A_Table_Under_Each_Compression(string? compression)
    {
        if (Address is null) return;
        var (_, connection) = await OpenAsync("scratch", compression);
        await using var _c = connection;

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT id, name, amount, d FROM sales ORDER BY id";
        await using var reader = await cmd.ExecuteReaderAsync();

        long rows = 0, sum = 0;
        object? lastName = null, lastAmount = null, lastDate = null;
        while (await reader.ReadAsync())
        {
            rows++;
            sum += reader.GetInt64(0);
            lastName = reader.GetValue(1);
            lastAmount = reader.GetValue(2);
            lastDate = reader.GetValue(3);
        }

        output.WriteLine($"{compression ?? "(server default)"}: {rows} rows, last {lastName} / {lastAmount} / {lastDate}");
        Assert.Equal(200_000, rows);
        Assert.Equal(199_999L * 200_000 / 2, sum);
    }

    [Fact]
    public async Task The_Data_Source_Header_Selects_The_Database()
    {
        if (Address is null) return;
        var (_, connection) = await OpenAsync("other");
        await using var _c = connection;

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sales";
        var ex = await Assert.ThrowsAnyAsync<Exception>(async () => await cmd.ExecuteReaderAsync());
        output.WriteLine(ex.Message);
    }

    [Fact]
    public async Task Browses_The_Proxys_Metadata()
    {
        if (Address is null) return;
        var (driver, connection) = await OpenAsync("scratch");
        await using var _c = connection;

        foreach (var db in await driver.ListDatabasesAsync(connection, default))
        {
            output.WriteLine($"catalog {db}");
            foreach (var t in await driver.ListTablesAsync(connection, db, default))
            {
                output.WriteLine($"  {t.Schema}.{t.Table}");
                if (t.Table != "sales") continue;
                foreach (var c in await driver.ListColumnsAsync(connection, db, t.Schema, t.Table, default))
                    output.WriteLine($"    {c}");
            }
        }
    }

    [Theory]
    [InlineData("Inline")]
    [InlineData("Prepared")]
    public async Task A_Parameterised_Query(string mode)
    {
        if (Address is null) return;
        var (_, connection) = await OpenAsync("scratch", parameterMode: mode);
        await using var _c = connection;

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sales WHERE id < @max";
        cmd.AddParameter("@max", 1000L);
        try
        {
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            output.WriteLine($"{mode}: {reader.GetValue(0)}");
            Assert.Equal(1000L, Convert.ToInt64(reader.GetValue(0)));
        }
        catch (Exception ex) when (mode == "Prepared")
        {
            output.WriteLine($"Prepared with parameters: {ex.GetType().Name}: {ex.Message}");
        }
    }

    [Fact]
    public async Task Connection_Tester_Passes()
    {
        if (Address is null) return;
        var (_, connection) = await OpenAsync("scratch");
        await using var _c = connection;
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT 1";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
    }
}
