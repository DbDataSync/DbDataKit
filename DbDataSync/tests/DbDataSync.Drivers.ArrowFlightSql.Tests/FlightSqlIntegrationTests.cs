using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.ArrowFlightSql.Tests;

/// <summary>
/// The driver against a real gRPC peer (<see cref="FakeFlightSqlServer"/>): the handshake, both
/// authentication methods, streaming across record batches, prepared statements, metadata, TLS.
/// </summary>
public sealed class FlightSqlIntegrationTests
{
    // A failed test result carries the shared classifier's message (plan 13, A10), so the Flight mapper has to be registered.
    public FlightSqlIntegrationTests() => DriverFailures.Configure(
        [new(DriverFailures.EngineFor(DriverIds.ArrowFlightSql), new FlightSqlFailureMapper())], includeDetails: false);

    private static ConnectionConfig Config(
        TestServerHost server, AuthMode auth = AuthMode.None, string? user = null,
        Dictionary<string, string>? properties = null, string driver = DriverIds.ArrowFlightSql) => new()
    {
        Name = "flight",
        DriverType = driver,
        ConnectionString = server.Address.ToString(),
        AuthMode = auth,
        UserId = user,
        Properties = properties ?? [],
    };

    private static async Task<DbConnection> OpenAsync(
        ArrowFlightSqlDriver driver, ConnectionConfig config, string? credential = null)
    {
        var connection = driver.CreateConnection(config, credential);
        await connection.OpenAsync();
        return connection;
    }

    private static Dictionary<string, string> Plain => new() { [FlightSqlSettings.UseTlsKey] = "false" };

    private static Dictionary<string, string> PlainWith(string key, string value) =>
        new() { [FlightSqlSettings.UseTlsKey] = "false", [key] = value };

    // ---- Reading ----

    [Fact]
    public async Task Reads_Every_Row_Across_Record_Batches_With_Typed_Values()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT * FROM orders";
        await using var reader = await cmd.ExecuteReaderAsync();

        // The schema is known before the first Read — callers ask for names first.
        Assert.Equal(["id", "customer", "amount", "placed", "shipped", "flag"],
            Enumerable.Range(0, reader.FieldCount).Select(reader.GetName));
        Assert.True(reader.HasRows);

        var ids = new List<long>();
        var customers = new List<string?>();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetInt64(0));
            customers.Add(reader.IsDBNull(1) ? null : reader.GetString(1));
        }

        Assert.Equal([1L, 2, 3, 4, 5], ids);
        Assert.Equal(["customer-1", "customer-2", null, "customer-4", "customer-5"], customers);
    }

    [Fact]
    public async Task Maps_Arrow_Types_To_Clr_Values()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT * FROM orders";
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());

        Assert.Equal(1L, reader.GetValue(0));
        Assert.Equal(10.25m, reader.GetValue(2));
        // No time zone on the column, so no offset on the value.
        Assert.Equal(new DateTime(2026, 1, 2, 12, 30, 0), reader.GetValue(3));
        Assert.IsType<DateTime>(reader.GetValue(3));
        Assert.Equal(new DateTime(2026, 2, 2), reader.GetValue(4));
        Assert.Equal(false, reader.GetValue(5));
        Assert.Equal("bigint", reader.GetDataTypeName(0));
        Assert.Equal("decimal(18,2)", reader.GetDataTypeName(2));
        Assert.Equal("timestamp", reader.GetDataTypeName(3));
    }

    [Fact]
    public async Task Schema_Comes_From_The_First_Batch_When_The_Server_Omits_It_From_FlightInfo()
    {
        await using var server = await TestServerHost.StartAsync(s => s.OmitInfoSchema = true);
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT * FROM orders";
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.Equal(6, reader.FieldCount);
        Assert.Equal("id", reader.GetName(0));
        Assert.True(await reader.ReadAsync());
    }

    [Fact]
    public async Task Batch_Reload_Reader_Reads_A_Table_Through_The_Driver()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver(ArrowFlightSqlDriver.Variants.Single(v => v.DriverType == DriverIds.ArrowFlightSqlPostgres));
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain, driver: driver.DriverType));

        var reader = driver.Readers.Single();
        var result = await reader.ReadChangesAsync(
            connection, new SourceTableRef { ConnectionName = "flight", Database = "main", Schema = "sales", Table = "orders" },
            previousWatermark: null, ReadIntent.InitialLoad, [], "orders", [], [],
            new Dictionary<string, IReadOnlyList<CachedColumn>>(), new Dictionary<string, string>(), CancellationToken.None);

        var rows = new List<ChangeRow>();
        await foreach (var row in result.Rows)
            rows.Add(row);

        Assert.Equal(5, rows.Count);
        Assert.All(rows, r => Assert.Equal(ChangeOperation.Insert, r.Operation));
        Assert.Equal(1L, rows[0]["id"]);
        Assert.Null(rows[2]["customer"]);

        // The statement went out in the Postgres variant's quoting.
        Assert.Contains("\"sales\".\"orders\"", server.State.Executed.Single());
    }

    // ---- Authentication ----

    [Fact]
    public async Task Open_Server_Needs_No_Credentials()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        var test = await driver.TestAsync(connection, CancellationToken.None);

        Assert.True(test.Succeeded, test.Error);
        Assert.All(server.State.AuthorizationSeen, h => Assert.Null(h));
        Assert.Equal(0, server.State.HandshakeCount);
    }

    [Fact]
    public async Task Basic_Auth_Handshakes_Then_Uses_The_Issued_Bearer_Token()
    {
        await using var server = await TestServerHost.StartAsync(s => s.Auth = ServerAuth.BasicHandshake);
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(
            driver, Config(server, AuthMode.SqlAuth, "svc", Plain), credential: "s3cret");

        var test = await driver.TestAsync(connection, CancellationToken.None);

        Assert.True(test.Succeeded, test.Error);
        Assert.Equal(1, server.State.HandshakeCount);

        // After the handshake only the issued token is sent — the password is not repeated on every call.
        var afterHandshake = server.State.AuthorizationSeen.Skip(1).ToList();
        Assert.NotEmpty(afterHandshake);
        Assert.All(afterHandshake, h => Assert.Equal($"Bearer {server.State.IssuedToken}", h));
    }

    [Fact]
    public async Task Basic_Auth_Falls_Back_To_Sending_Basic_When_The_Server_Issues_No_Token()
    {
        await using var server = await TestServerHost.StartAsync(s => s.Auth = ServerAuth.BasicEveryCall);
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(
            driver, Config(server, AuthMode.SqlAuth, "svc", Plain), credential: "s3cret");

        var test = await driver.TestAsync(connection, CancellationToken.None);

        Assert.True(test.Succeeded, test.Error);
        Assert.All(server.State.AuthorizationSeen, h => Assert.StartsWith("Basic ", h));
    }

    [Fact]
    public async Task Basic_Auth_With_A_Wrong_Password_Fails_The_Open_And_Never_Reveals_The_Password()
    {
        await using var server = await TestServerHost.StartAsync(s => s.Auth = ServerAuth.BasicHandshake);
        var driver = new ArrowFlightSqlDriver();

        var ex = await Assert.ThrowsAsync<FlightSqlException>(() =>
            OpenAsync(driver, Config(server, AuthMode.SqlAuth, "svc", Plain), credential: "not-the-password"));

        Assert.Equal(Grpc.Core.StatusCode.Unauthenticated, ex.StatusCode);
        Assert.DoesNotContain("not-the-password", ex.ToString());
    }

    [Fact]
    public async Task Bearer_Auth_Sends_The_Token_With_No_Handshake()
    {
        await using var server = await TestServerHost.StartAsync(s => s.Auth = ServerAuth.Bearer);
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(
            driver, Config(server, AuthMode.SqlAuth, null, PlainWith(FlightSqlSettings.AuthMethodKey, "Bearer")),
            credential: "static-token-123");

        var test = await driver.TestAsync(connection, CancellationToken.None);

        Assert.True(test.Succeeded, test.Error);
        Assert.Equal(0, server.State.HandshakeCount);
        Assert.All(server.State.AuthorizationSeen, h => Assert.Equal("Bearer static-token-123", h));
    }

    [Fact]
    public async Task Bearer_Auth_With_A_Wrong_Token_Is_Reported_By_The_Connection_Test()
    {
        await using var server = await TestServerHost.StartAsync(s => s.Auth = ServerAuth.Bearer);
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(
            driver, Config(server, AuthMode.SqlAuth, null, PlainWith(FlightSqlSettings.AuthMethodKey, "Bearer")),
            credential: "wrong-token");

        var test = await driver.TestAsync(connection, CancellationToken.None);

        // A failed test result, not an exception: a wrong credential is the failure an operator expects.
        Assert.False(test.Succeeded);
        Assert.StartsWith("The login was rejected.", test.Error);
        Assert.Contains("Unauthenticated", test.Error);
        Assert.DoesNotContain("wrong-token", test.Error);
    }

    [Fact]
    public async Task No_Credentials_Against_A_Protected_Server_Is_Reported_By_The_Connection_Test()
    {
        await using var server = await TestServerHost.StartAsync(s => s.Auth = ServerAuth.Bearer);
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        var test = await driver.TestAsync(connection, CancellationToken.None);

        Assert.False(test.Succeeded);
        Assert.StartsWith("The login was rejected.", test.Error);
    }

    // ---- Parameters ----

    [Fact]
    public async Task Prepared_Parameters_Are_Bound_Positionally_And_Never_Spliced_Into_The_Statement()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE id > @min AND customer <> 'a @min' AND id < @max AND id <> @min";
        cmd.AddParameter("@min", 1L);
        cmd.AddParameter("@max", 99L);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync()) { }

        // Positional markers, one per use (so @min twice), and the quoted '@min' left alone.
        Assert.Equal("SELECT * FROM orders WHERE id > ? AND customer <> 'a @min' AND id < ? AND id <> ?", server.State.Executed.Single());

        var parameters = server.State.ExecutedParameters.Single()!;
        Assert.Equal(3, parameters.ColumnCount);
        Assert.Equal(1, parameters.Length);
        Assert.Equal(1L, ((Apache.Arrow.Int64Array)parameters.Column(0)).GetValue(0));
        Assert.Equal(99L, ((Apache.Arrow.Int64Array)parameters.Column(1)).GetValue(0));
        Assert.Equal(1L, ((Apache.Arrow.Int64Array)parameters.Column(2)).GetValue(0));
    }

    [Fact]
    public async Task Inline_Mode_Renders_Literals_Through_The_Dialect_And_Escapes_Quotes()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(
            driver, Config(server, properties: PlainWith(FlightSqlSettings.ParameterModeKey, "Inline")));

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT * FROM orders WHERE customer = @name";
        cmd.AddParameter("@name", "o'brien");
        await using var reader = await cmd.ExecuteReaderAsync();

        Assert.Equal("SELECT * FROM orders WHERE customer = 'o''brien'", server.State.Executed.Single());
        Assert.Null(server.State.ExecutedParameters.Single());
    }

    [Fact]
    public async Task A_Server_That_Cannot_Bind_Parameters_Falls_Back_To_Inline_Literals()
    {
        await using var server = await TestServerHost.StartAsync(s => s.RefuseParameters = true);
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        for (var i = 0; i < 2; i++)
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.CommandText = "SELECT * FROM orders WHERE customer = @name";
            cmd.AddParameter("@name", "o'brien");
            await using var reader = await cmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
        }

        // The first command's bind was refused and it fell back; the second went straight to literals.
        Assert.Equal(
            ["SELECT * FROM orders WHERE customer = 'o''brien'", "SELECT * FROM orders WHERE customer = 'o''brien'"],
            server.State.Executed);
    }

    [Fact]
    public async Task A_Command_That_Changes_Data_Is_Refused()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "DELETE FROM orders";

        await Assert.ThrowsAsync<NotSupportedException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Empty(server.State.Executed);
    }

    // ---- Metadata ----

    [Fact]
    public async Task Browses_Catalogs_Tables_And_Columns_With_Keys()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        Assert.Equal(["main", "other"], await driver.ListDatabasesAsync(connection, default));

        var tables = await driver.ListTablesAsync(connection, "main", default);
        Assert.Equal(
            [new TableMetadata("sales", "orders"), new TableMetadata("sales", "order_view")],
            tables); // the SYSTEM TABLE is left out

        var columns = await driver.ListColumnsAsync(connection, "main", "sales", "orders", default);
        Assert.Equal(6, columns.Count);
        Assert.Equal(new ColumnMetadata("id", "bigint", false, true, false), columns[0]);
        Assert.Equal(new ColumnMetadata("customer", "varchar", true, false, false), columns[1]);
        Assert.Equal("decimal(18,2)", columns[2].NativeType);
        Assert.Equal("timestamp", columns[3].NativeType);
        Assert.Equal("date", columns[4].NativeType);
        Assert.Equal("boolean", columns[5].NativeType);
    }

    [Fact]
    public async Task An_Unfiltered_Browse_Leaves_The_Filters_Unset_Rather_Than_Sending_Empty_Ones()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        await driver.ListTablesAsync(connection, "", default);

        // An empty catalog means "tables that have none" and an empty pattern matches nothing; unset is
        // the protocol's "no filter".
        Assert.Equal(
            "GetTables catalog=<unset> schema=<unset> table=<unset> types=[] includeSchema=False",
            server.State.MetadataRequests.Single());
    }

    [Fact]
    public async Task A_Server_Without_Metadata_Browses_As_Empty_Not_As_An_Error()
    {
        await using var server = await TestServerHost.StartAsync(s => s.NoMetadata = true);
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        Assert.Empty(await driver.ListDatabasesAsync(connection, default));
        Assert.Empty(await driver.ListTablesAsync(connection, "main", default));
        Assert.Empty(await driver.ListColumnsAsync(connection, "main", "sales", "orders", default));
    }

    // ---- TLS ----

    [Fact]
    public async Task A_Self_Signed_Certificate_Is_Rejected_By_Default()
    {
        await using var server = await TestServerHost.StartAsync(tls: true);
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server));

        var test = await driver.TestAsync(connection, CancellationToken.None);

        Assert.False(test.Succeeded);
        Assert.StartsWith("A secure connection could not be established.", test.Error);
    }

    [Fact]
    public async Task Skipping_Certificate_Validation_Accepts_A_Self_Signed_Certificate()
    {
        await using var server = await TestServerHost.StartAsync(s => s.Auth = ServerAuth.Bearer, tls: true);
        var driver = new ArrowFlightSqlDriver();
        var properties = new Dictionary<string, string>
        {
            [FlightSqlSettings.SkipCertificateValidationKey] = "true",
            [FlightSqlSettings.AuthMethodKey] = "Bearer",
        };
        await using var connection = await OpenAsync(
            driver, Config(server, AuthMode.SqlAuth, null, properties), credential: "static-token-123");

        var test = await driver.TestAsync(connection, CancellationToken.None);

        Assert.True(test.Succeeded, test.Error);
    }

    // ---- Connection ----

    [Fact]
    public async Task The_Connection_String_Is_The_Address_Only()
    {
        await using var server = await TestServerHost.StartAsync(s => s.Auth = ServerAuth.Bearer);
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(
            driver, Config(server, AuthMode.SqlAuth, null, PlainWith(FlightSqlSettings.AuthMethodKey, "Bearer")),
            credential: "static-token-123");

        Assert.DoesNotContain("static-token-123", connection.ConnectionString);
        Assert.DoesNotContain("static-token-123", connection.DataSource);
        Assert.Equal(System.Data.ConnectionState.Open, connection.State);
    }

    [Fact]
    public async Task Command_Timeout_Is_Stamped_From_The_Connection_Config()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver();
        var config = Config(server, properties: Plain);
        config.CommandTimeoutSeconds = 7;
        await using var connection = await OpenAsync(driver, config);

        using var cmd = connection.CreateTimedCommand();

        Assert.Equal(7, cmd.CommandTimeout);
    }

    [Fact]
    public async Task Server_Version_Never_Throws()
    {
        await using var server = await TestServerHost.StartAsync();
        var driver = new ArrowFlightSqlDriver();
        await using var connection = await OpenAsync(driver, Config(server, properties: Plain));

        Assert.False(string.IsNullOrEmpty(connection.ServerVersion));
    }
}
