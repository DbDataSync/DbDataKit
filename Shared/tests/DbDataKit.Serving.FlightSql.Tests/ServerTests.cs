using System.Data;
using System.Data.Common;
using DbDataKit.Connections.FlightSql;
using DbDataKit.Serving.Tests;
using Apache.Arrow.Flight;
using Grpc.Core;

namespace DbDataKit.Serving.FlightSql.Tests;

/// <summary>The Flight SQL module end to end: a real client over HTTP/2 against the module on the fake host's services (two users, a public table both see and a secret one only the admin sees, a row limit for the reader).</summary>
public class ServerTests
{
    private static async Task<FlightSqlConnection> OpenAsync(ModuleHost host, string user = "admin", string? password = null, string? dataSource = "main", string? compression = null, FlightSqlAuthMethod auth = FlightSqlAuthMethod.Basic)
    {
        var settings = new FlightSqlSettings(host.Address, auth, user, password ?? $"{user}-pw", SkipCertificateValidation: false, Catalog: null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), FlightSqlParameterMode.Prepared, DataSource: dataSource, Compression: compression);
        var connection = new FlightSqlConnection(settings, v => v?.ToString() ?? "NULL");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<List<object?[]>> QueryAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            var row = new object?[reader.FieldCount];
            reader.GetValues(row!);
            rows.Add(row);
        }
        return rows;
    }

    [Fact]
    public async Task A_client_logs_in_with_basic_credentials_and_reads_a_table_in_batches()
    {
        await using var host = await ModuleHost.StartAsync();
        await using var connection = await OpenAsync(host);

        var rows = await QueryAsync(connection, "public.items");

        Assert.Equal([1, 2, 3, 4, 5], rows.Select(r => (int)r[0]!));
        Assert.Equal(["a", "b", "c", "d", "e"], rows.Select(r => (string)r[1]!));
    }

    [Fact]
    public async Task A_wrong_password_or_an_unknown_user_is_unauthenticated()
    {
        await using var host = await ModuleHost.StartAsync();

        foreach (var (user, password) in new[] { ("admin", "wrong"), ("nobody", "x") })
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => OpenAsync(host, user, password));
            Assert.Equal(StatusCode.Unauthenticated, Status(error));
        }
    }

    private static StatusCode Status(Exception e) => e switch
    {
        RpcException rpc => rpc.StatusCode,
        FlightSqlException wrapped => wrapped.StatusCode,
        { InnerException: { } inner } => Status(inner),
        _ => StatusCode.Unknown,
    };

    [Fact]
    public async Task A_call_without_credentials_or_with_a_token_nobody_issued_is_unauthenticated()
    {
        await using var host = await ModuleHost.StartAsync();
        using var channel = Grpc.Net.Client.GrpcChannel.ForAddress(host.Address);
        var flight = new Apache.Arrow.Flight.Client.FlightClient(channel);

        var none = await Assert.ThrowsAsync<RpcException>(async () => { await foreach (var _ in flight.ListFlights(new FlightCriteria()).ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken)) { } });
        Assert.Equal(StatusCode.Unauthenticated, none.StatusCode);

        var forged = new Grpc.Core.Metadata { { "authorization", "Bearer not-a-token" } };
        var bad = await Assert.ThrowsAsync<RpcException>(async () => { using var call = flight.ListFlights(new FlightCriteria(), forged); await foreach (var _ in call.ResponseStream.ReadAllAsync(TestContext.Current.CancellationToken)) { } });
        Assert.Equal(StatusCode.Unauthenticated, bad.StatusCode);
    }

    [Fact]
    public async Task What_the_core_refuses_reaches_the_client_as_the_status_that_says_so()
    {
        await using var host = await ModuleHost.StartAsync();
        await using var reader = await OpenAsync(host, "reader");

        Assert.Equal(StatusCode.PermissionDenied, Status(await Assert.ThrowsAnyAsync<Exception>(() => QueryAsync(reader, "secret.salaries"))));
        Assert.Equal(StatusCode.NotFound, Status(await Assert.ThrowsAnyAsync<Exception>(() => QueryAsync(reader, "public.nothing"))));
        Assert.Equal(StatusCode.InvalidArgument, Status(await Assert.ThrowsAnyAsync<Exception>(() => QueryAsync(reader, " "))));
    }

    [Fact]
    public async Task A_limit_the_core_enforces_ends_the_stream_with_an_error_after_the_rows_the_caller_may_have()
    {
        await using var host = await ModuleHost.StartAsync();
        await using var reader = await OpenAsync(host, "reader");

        var error = await Assert.ThrowsAnyAsync<Exception>(() => QueryAsync(reader, "public.items"));

        Assert.Equal(StatusCode.ResourceExhausted, Status(error));
    }

    [Fact]
    public async Task An_update_goes_to_the_core_and_is_refused_by_a_host_that_takes_none()
    {
        await using var readOnly = await ModuleHost.StartAsync();
        using var reading = await RawClient.LoginAsync(readOnly.Address, "admin", "admin-pw");
        Assert.Equal(StatusCode.PermissionDenied, Status(await Assert.ThrowsAnyAsync<Exception>(() => reading.UpdateAsync("update items"))));

        await using var writable = await ModuleHost.StartAsync(new HostServices(new FakeServingHost(withWrites: true)));
        using var writing = await RawClient.LoginAsync(writable.Address, "admin", "admin-pw");
        Assert.Equal(1, await writing.UpdateAsync("update items"));
    }

    [Fact]
    public async Task A_drained_module_answers_unavailable_so_the_client_goes_to_another_instance()
    {
        await using var host = await ModuleHost.StartAsync();
        await using var connection = await OpenAsync(host);
        await host.Module.DrainAsync();

        Assert.Equal(ServingHealthState.Degraded, host.Module.Health().State);
        Assert.Equal(StatusCode.Unavailable, Status(await Assert.ThrowsAnyAsync<Exception>(() => QueryAsync(connection, "public.items"))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("zstd")]
    [InlineData("lz4")]
    [InlineData("gzip")]
    public async Task A_client_may_ask_for_a_compression_and_the_rows_are_the_same(string? compression)
    {
        await using var host = await ModuleHost.StartAsync();
        await using var connection = await OpenAsync(host, compression: compression);

        Assert.Equal(5, (await QueryAsync(connection, "public.items")).Count);
    }
}
