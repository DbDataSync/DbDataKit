using System.Data;
using System.Data.Common;
using System.Globalization;
using Grpc.Core;

namespace DbDataKit.Connections.FlightSql.Tests;

/// <summary>
/// The client against a real <c>dbdataproxy server</c> in front of SQL Server and DuckDB (plan 13, A18). Skipped unless <c>DBPROXY_E2E_ADDRESS</c> names a running proxy
/// (<c>https://127.0.0.1:18815</c>); <c>DBPROXY_E2E_USER</c> / <c>DBPROXY_E2E_PASSWORD</c> are the proxy user, which may use the data sources <c>duck</c> (a writable DuckDB file)
/// and <c>mssql</c> (SQL Server, read-only). <c>DBPROXY_E2E_REQUIRED=true</c> fails the tests instead of skipping them, for the CI job that starts the proxy.
/// <c>DbDataProxy/tests/interop/run-flightsql-e2e.sh</c> starts everything and runs this class.
/// </summary>
public class ProxyEndToEndTests
{
    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;

    private static void RequireProxy()
    {
        if (Env("DBPROXY_E2E_ADDRESS") is not null) return;
        Assert.False(string.Equals(Env("DBPROXY_E2E_REQUIRED"), "true", StringComparison.OrdinalIgnoreCase), "DBPROXY_E2E_REQUIRED is true but DBPROXY_E2E_ADDRESS is not set");
        Assert.Skip("set DBPROXY_E2E_ADDRESS to a running dbdataproxy to run");
    }

    private static async Task<FlightSqlConnection> OpenAsync(
        string dataSource, string? compression = null, FlightSqlParameterMode mode = FlightSqlParameterMode.Prepared, string? password = null)
    {
        RequireProxy();
        var settings = new FlightSqlSettings(
            new Uri(Env("DBPROXY_E2E_ADDRESS")!), FlightSqlAuthMethod.Basic, Env("DBPROXY_E2E_USER") ?? "e2e", password ?? Env("DBPROXY_E2E_PASSWORD") ?? "",
            SkipCertificateValidation: true, Catalog: null, ConnectTimeout: TimeSpan.FromSeconds(30), CommandTimeout: TimeSpan.FromSeconds(60), mode,
            DataSource: dataSource, Compression: compression);
        var connection = new FlightSqlConnection(settings, Literal);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static string Literal(object? value) => value switch
    {
        null => "NULL",
        string s => "'" + s.Replace("'", "''") + "'",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => throw new NotSupportedException(value.GetType().Name),
    };

    private static async Task<List<object?[]>> QueryAsync(DbConnection connection, string sql, params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = command.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            command.Parameters.Add(p);
        }
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            rows.Add([.. row.Select(v => v is DBNull ? null : v)]);
        }
        return rows;
    }

    private static async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task DuckDb_typed_values_and_nulls_are_read_back()
    {
        await using var connection = await OpenAsync("duck");

        var rows = await QueryAsync(connection,
            "SELECT id, name, amount, d FROM (VALUES (1, 'one', 1.50::DECIMAL(10,2), DATE '2026-01-02'), (2, NULL, 2.25::DECIMAL(10,2), NULL)) AS t(id, name, amount, d) ORDER BY id");

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, Convert.ToInt32(rows[0][0], CultureInfo.InvariantCulture));
        Assert.Equal("one", rows[0][1]);
        Assert.Equal(1.50m, Convert.ToDecimal(rows[0][2], CultureInfo.InvariantCulture));
        Assert.Equal(new DateOnly(2026, 1, 2), rows[0][3] is DateTime dt ? DateOnly.FromDateTime(dt) : (DateOnly)rows[0][3]!);
        Assert.Null(rows[1][1]);
        Assert.Null(rows[1][3]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("gzip")]
    [InlineData("zstd")]
    [InlineData("lz4")]
    public async Task DuckDb_a_large_result_arrives_whole_under_each_compression(string? compression)
    {
        await using var connection = await OpenAsync("duck", compression);

        var rows = await QueryAsync(connection, "SELECT i AS id, 'row ' || i AS name FROM range(50000) t(i) ORDER BY i");

        Assert.Equal(50_000, rows.Count);
        Assert.Equal(49_999L * 50_000 / 2, rows.Sum(r => Convert.ToInt64(r[0], CultureInfo.InvariantCulture)));
        Assert.Equal("row 49999", rows[^1][1]);
    }

    [Theory]
    [InlineData(FlightSqlParameterMode.Prepared)]
    [InlineData(FlightSqlParameterMode.Inline)]
    public async Task DuckDb_a_parameterised_query_binds_the_value(FlightSqlParameterMode mode)
    {
        await using var connection = await OpenAsync("duck", mode: mode);

        var rows = await QueryAsync(connection, "SELECT COUNT(*) FROM range(100) t(i) WHERE i < @max", ("@max", 10L));

        Assert.Equal(10L, Convert.ToInt64(rows.Single()[0], CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task SqlServer_values_keep_their_types()
    {
        await using var connection = await OpenAsync("mssql");

        var rows = await QueryAsync(connection, "SELECT CAST(42 AS int) AS n, CAST(N'héllo' AS nvarchar(20)) AS s, CAST(1.5 AS decimal(10,2)) AS d, CAST(NULL AS int) AS nothing");

        var row = rows.Single();
        Assert.Equal(42, Convert.ToInt32(row[0], CultureInfo.InvariantCulture));
        Assert.Equal("héllo", row[1]);
        Assert.Equal(1.50m, Convert.ToDecimal(row[2], CultureInfo.InvariantCulture));
        Assert.Null(row[3]);
    }

    [Fact]
    public async Task The_client_itself_never_sends_a_statement_that_changes_data()
    {
        await using var connection = await OpenAsync("mssql");

        await Assert.ThrowsAsync<NotSupportedException>(() => ExecuteAsync(connection, "CREATE TABLE e2e_should_not_exist (i int)"));
    }

    [Fact]
    public async Task SqlServer_is_read_only_through_the_proxy_even_for_a_query_that_is_not_one_select()
    {
        await using var connection = await OpenAsync("mssql");

        var ex = await Assert.ThrowsAnyAsync<DbException>(() => QueryAsync(connection, "SELECT 1; SELECT 2"));

        Assert.Equal(StatusCode.PermissionDenied, Assert.IsType<FlightSqlException>(ex).StatusCode);
    }

    [Fact]
    public async Task A_wrong_password_is_refused_as_a_login_failure()
    {
        RequireProxy();

        var ex = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync("duck", password: "not-the-password"));

        var flight = Assert.IsType<FlightSqlException>(ex is AggregateException a ? a.InnerException : ex);
        Assert.Equal(StatusCode.Unauthenticated, flight.StatusCode);
        Assert.Equal(DbDataKit.Connections.ConnectionFailureKind.Login, new FlightSqlFailureMapper().Map(flight, DbDataKit.Dialect.EngineId.SqlServer)!.Kind);
    }

    [Fact]
    public async Task A_data_source_the_user_may_not_use_is_refused()
    {
        await using var connection = await OpenAsync("not-a-source");

        var ex = await Assert.ThrowsAnyAsync<DbException>(() => QueryAsync(connection, "SELECT 1"));

        Assert.IsType<FlightSqlException>(ex);
    }
}
