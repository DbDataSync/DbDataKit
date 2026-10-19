using Apache.Arrow.Flight;
using Grpc.Core;

namespace DbDataSync.Drivers.ArrowFlightSql.Tests;

/// <summary>
/// The handshake call's request/response exchange (<see cref="FlightSqlConnection.ExchangeHandshakeAsync"/>) against a fake call, so a race in
/// the transport can be set up on purpose. CI saw a wrong password come out of the real driver as a raw gRPC <c>Cancelled</c> instead of
/// <c>Unauthenticated</c>: the server rejected the credentials and ended the call before the client had finished writing its request, the write
/// failed, and the driver never read the answer. That cannot be forced with a real server, so these tests fail the write by hand.
/// </summary>
public sealed class HandshakeExchangeTests
{
    private sealed class FakeRequestWriter(Exception? writeFailure) : IClientStreamWriter<FlightHandshakeRequest>
    {
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(FlightHandshakeRequest message) => writeFailure is null ? Task.CompletedTask : Task.FromException(writeFailure);
        public Task CompleteAsync() => Task.CompletedTask;
    }

    private sealed class EmptyResponses : IAsyncStreamReader<FlightHandshakeResponse>
    {
        public FlightHandshakeResponse Current => throw new InvalidOperationException();
        public Task<bool> MoveNext(CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private static AsyncDuplexStreamingCall<FlightHandshakeRequest, FlightHandshakeResponse> Call(Exception? writeFailure, Task<Metadata> headers) =>
        new(new FakeRequestWriter(writeFailure), new EmptyResponses(), headers, () => Status.DefaultSuccess, () => new Metadata(), () => { });

    private static Task<Metadata> Rejected(StatusCode code, string detail) =>
        Task.FromException<Metadata>(new RpcException(new Status(code, detail)));

    private static Exception TransportCancelled() =>
        new RpcException(new Status(StatusCode.Cancelled, "", new OperationCanceledException("A task was canceled.")));

    [Fact]
    public async Task A_rejection_that_beats_the_request_write_surfaces_as_the_servers_status_not_a_cancellation()
    {
        var call = Call(TransportCancelled(), Rejected(StatusCode.Unauthenticated, "bad credentials"));

        var ex = await Assert.ThrowsAsync<FlightSqlException>(
            () => FlightSqlConnection.ExchangeHandshakeAsync(call, CancellationToken.None));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task A_rejection_after_a_successful_write_surfaces_the_same_way()
    {
        var call = Call(writeFailure: null, Rejected(StatusCode.Unauthenticated, "bad credentials"));

        var ex = await Assert.ThrowsAsync<FlightSqlException>(
            () => FlightSqlConnection.ExchangeHandshakeAsync(call, CancellationToken.None));

        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }

    [Fact]
    public async Task A_failed_write_followed_by_a_good_answer_carries_on_and_returns_what_the_server_issued()
    {
        var headers = new Metadata { { "authorization", "Bearer issued-token" } };
        var call = Call(TransportCancelled(), Task.FromResult(headers));

        var issued = await FlightSqlConnection.ExchangeHandshakeAsync(call, CancellationToken.None);

        Assert.Equal("Bearer issued-token", issued);
    }

    [Fact]
    public async Task A_server_that_issues_nothing_returns_null_so_the_caller_keeps_the_basic_header()
    {
        var call = Call(writeFailure: null, Task.FromResult(new Metadata()));

        Assert.Null(await FlightSqlConnection.ExchangeHandshakeAsync(call, CancellationToken.None));
    }

    [Fact]
    public async Task The_callers_own_cancellation_still_propagates_and_is_not_swallowed_as_a_write_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var call = Call(new OperationCanceledException(cts.Token), Rejected(StatusCode.Unauthenticated, "never read"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FlightSqlConnection.ExchangeHandshakeAsync(call, cts.Token));
    }
}
