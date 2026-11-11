using DbDataKit.Connections;
using Grpc.Core;

namespace DbDataKit.Serving.FlightSql.Tests;

public class FailuresTests
{
    [Theory]
    [InlineData(ConnectionFailureKind.Login, StatusCode.Unauthenticated)]
    [InlineData(ConnectionFailureKind.Network, StatusCode.Unavailable)]
    [InlineData(ConnectionFailureKind.Tls, StatusCode.Unavailable)]
    [InlineData(ConnectionFailureKind.Timeout, StatusCode.DeadlineExceeded)]
    [InlineData(ConnectionFailureKind.Permission, StatusCode.PermissionDenied)]
    [InlineData(ConnectionFailureKind.Syntax, StatusCode.InvalidArgument)]
    [InlineData(ConnectionFailureKind.Unsupported, StatusCode.InvalidArgument)]
    [InlineData(ConnectionFailureKind.Other, StatusCode.Internal)]
    public void Each_kind_of_database_failure_has_the_status_a_client_can_act_on(ConnectionFailureKind kind, StatusCode expected) =>
        Assert.Equal(expected, FlightFailures.StatusFor(kind));

    [Fact]
    public void A_classified_failure_reaches_the_client_as_its_fixed_sentence_and_never_as_the_drivers_text()
    {
        var failure = new ConnectionFailure(ConnectionFailureKind.Login, "18456", "sqlserver");

        foreach (var cause in new Exception[] { new ConnectionFailureException(failure), new ServingBackendException(failure) })
        {
            var rpc = Assert.IsType<FailureRpcException>(FlightFailures.ToRpcException(cause));
            Assert.Equal(StatusCode.Unauthenticated, rpc.StatusCode);
            Assert.Equal(failure.PublicMessage, rpc.Status.Detail);
            Assert.Same(failure, rpc.Failure);
        }
    }

    [Fact]
    public void The_serving_exceptions_become_the_status_that_says_what_happened_with_the_hosts_own_message()
    {
        Assert.Equal((StatusCode.PermissionDenied, "no"), Parts(new ServingDeniedException("no")));
        Assert.Equal((StatusCode.NotFound, "none"), Parts(new ServingNotFoundException("none")));
        Assert.Equal((StatusCode.InvalidArgument, "bad"), Parts(new ServingInvalidException("bad")));
        Assert.Equal((StatusCode.ResourceExhausted, "too many"), Parts(new ServingLimitException("too many", "rows")));

        static (StatusCode, string) Parts(Exception e) { var r = FlightFailures.ToRpcException(e); return (r.StatusCode, r.Status.Detail); }
    }

    [Fact]
    public void An_rpc_exception_passes_through_and_an_unknown_one_is_internal_naming_only_its_type()
    {
        var already = new RpcException(new Status(StatusCode.Cancelled, "stop"));
        Assert.Same(already, FlightFailures.ToRpcException(already));

        var unknown = FlightFailures.ToRpcException(new InvalidOperationException("secret table name"));
        Assert.Equal(StatusCode.Internal, unknown.StatusCode);
        Assert.DoesNotContain("secret", unknown.Status.Detail);
        Assert.Contains("InvalidOperationException", unknown.Status.Detail);
        Assert.True(FlightFailures.IsUnclassified(unknown, new InvalidOperationException()));
        Assert.False(FlightFailures.IsUnclassified(FlightFailures.ToRpcException(new ServingBackendException(new ConnectionFailure(ConnectionFailureKind.Other, "1", null))), new ServingBackendException(new ConnectionFailure(ConnectionFailureKind.Other, "1", null))));
    }
}
