using DbDataKit.Connections;
using DbDataProxy.Data;
using DbDataKit.Serving.FlightSql;
using DbDataProxy.Server.Serving;
using DbDataProxy.Tests.Support;
using Grpc.Core;

namespace DbDataProxy.Tests.Flight;

/// <summary>
/// Clients get a failure kind, the engine's error number or SQLSTATE and a fixed sentence, never the database driver's text
/// (which can name tables, columns, servers and file paths). The driver text goes to the local log.
/// </summary>
public class FailureModelTests(ProxyFixture proxy) : IClassFixture<ProxyFixture>
{
    private async Task<ProxyClient> LoginAsync()
    {
        var client = proxy.Connect();
        await client.AuthenticateAsync("alice", ProxyFixture.AlicePassword);
        return client;
    }

    [Fact]
    public async Task A_syntax_error_reports_the_kind_and_class_but_not_the_engines_words()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELEKT 1 FROM prices", "duck"));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.Equal("The statement was not accepted by the server. [duckdb Parser-Error]", ex.Status.Detail);
        Assert.DoesNotContain("SELEKT", ex.Status.Detail);
        Assert.DoesNotContain("syntax error", ex.Status.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_missing_table_does_not_echo_its_name_or_the_catalog_suggestions()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("SELECT * FROM confidential_salaries_2026", "duck"));

        Assert.Equal(StatusCode.InvalidArgument, ex.StatusCode);
        Assert.DoesNotContain("confidential_salaries_2026", ex.Status.Detail);
        Assert.DoesNotContain("prices", ex.Status.Detail); // DuckDB's "did you mean" lists real table names
        Assert.Contains("[duckdb Catalog-Error]", ex.Status.Detail);
    }

    [Fact]
    public async Task A_refused_write_is_a_permission_failure_with_a_code()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.QueryAsync("INSERT INTO prices VALUES (9, 9, 'x')", "duck_off"));

        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        Assert.StartsWith("The login does not have permission for this operation.", ex.Status.Detail);
        Assert.DoesNotContain("prices", ex.Status.Detail);
    }

    [Fact]
    public async Task Prepared_statement_and_update_paths_give_the_same_text_free_errors()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(() => client.RawUpdateAsync("UPDATE no_such_table_zz SET a = 1", "duck"));
        Assert.DoesNotContain("no_such_table_zz", ex.Status.Detail);
        Assert.Contains("[duckdb Catalog-Error]", ex.Status.Detail);
    }

    // what a client sees: Proxy's data layer says the failure in the serving layer's words, the Flight module in gRPC's
    private static RpcException Wire(Exception ex) => FlightFailures.ToRpcException(ProxyFailures.ToServing(ex));

    [Theory]
    [InlineData(ConnectionFailureKind.Login, StatusCode.Unauthenticated)]
    [InlineData(ConnectionFailureKind.Network, StatusCode.Unavailable)]
    [InlineData(ConnectionFailureKind.Tls, StatusCode.Unavailable)]
    [InlineData(ConnectionFailureKind.Timeout, StatusCode.DeadlineExceeded)]
    [InlineData(ConnectionFailureKind.Permission, StatusCode.PermissionDenied)]
    [InlineData(ConnectionFailureKind.Syntax, StatusCode.InvalidArgument)]
    [InlineData(ConnectionFailureKind.Unsupported, StatusCode.InvalidArgument)]
    [InlineData(ConnectionFailureKind.Other, StatusCode.Internal)]
    public void Failure_kinds_map_to_statuses(ConnectionFailureKind kind, StatusCode expected)
    {
        Assert.Equal(expected, FlightFailures.StatusFor(kind));
        var rpc = Wire(new ConnectionFailureException(new ConnectionFailure(kind, "1234", DbDataKit.Dialect.EngineId.SqlServer)));
        Assert.Equal(expected, rpc.StatusCode);
        Assert.Equal(ConnectionFailure.TextFor(kind) + " [sqlserver 1234]", rpc.Status.Detail);
    }

    [Fact]
    public void Unknown_exceptions_never_surface_their_message()
    {
        var rpc = Wire(new InvalidOperationException("Server=db7;Password=hunter2 exploded"));
        Assert.Equal(StatusCode.Internal, rpc.StatusCode);
        Assert.DoesNotContain("hunter2", rpc.Status.Detail);
        Assert.DoesNotContain("db7", rpc.Status.Detail);
        Assert.Contains("InvalidOperationException", rpc.Status.Detail);
    }

    [Fact]
    public void Messages_the_proxy_wrote_itself_are_passed_on()
    {
        var rpc = Wire(new ParameterMismatchException("The statement has 2 '?' markers but 1 parameters were supplied."));
        Assert.Equal(StatusCode.InvalidArgument, rpc.StatusCode);
        Assert.Contains("2 '?' markers", rpc.Status.Detail);
        Assert.Equal(StatusCode.InvalidArgument, Wire(new UnsupportedValueException("Column 'x' needs a CAST.")).StatusCode);
    }
}
