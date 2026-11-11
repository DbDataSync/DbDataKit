using DbDataKit.Connections;
using DbDataKit.Dialect;
using Grpc.Core;

namespace DbDataKit.Connections.FlightSql.Tests;

public class FlightSqlSettingsTests
{
    [Theory]
    [InlineData("grpc://h:1", true, "http://h:1/")]
    [InlineData("grpc+tcp://h:1", true, "http://h:1/")]
    [InlineData("grpc+tls://h:1", false, "https://h:1/")]
    [InlineData("http://h:1", true, "http://h:1/")]
    [InlineData("https://h:1", false, "https://h:1/")]
    [InlineData("h:1", true, "https://h:1/")]
    [InlineData("h:1", false, "http://h:1/")]
    public void An_address_takes_its_scheme_from_the_text_or_from_the_tls_setting(string text, bool tls, string expected) =>
        Assert.Equal(expected, FlightSqlSettings.ResolveAddress(text, null, null, tls).ToString());

    [Fact]
    public void Host_and_port_make_an_address_and_a_connection_string_wins()
    {
        Assert.Equal("https://flight.example:32010/", FlightSqlSettings.ResolveAddress(null, "flight.example", 32010, true).ToString());
        Assert.Equal("http://other:1/", FlightSqlSettings.ResolveAddress("grpc://other:1", "flight.example", 32010, true).ToString());
    }

    [Fact]
    public void Neither_a_host_nor_an_address_is_refused_and_so_is_a_scheme_flight_does_not_use()
    {
        Assert.Throws<InvalidOperationException>(() => FlightSqlSettings.ResolveAddress(null, null, null, true));
        Assert.Contains("ftp://", Assert.Throws<InvalidOperationException>(() => FlightSqlSettings.ResolveAddress("ftp://h", null, null, true)).Message);
    }

    [Fact]
    public void Seconds_of_zero_or_less_mean_no_limit()
    {
        Assert.Equal(Timeout.InfiniteTimeSpan, FlightSqlSettings.Seconds(0));
        Assert.Equal(TimeSpan.FromSeconds(30), FlightSqlSettings.Seconds(30));
    }
}

public class FlightSqlFailureMapperTests
{
    [Theory]
    [InlineData(StatusCode.Unauthenticated, ConnectionFailureKind.Login)]
    [InlineData(StatusCode.PermissionDenied, ConnectionFailureKind.Permission)]
    [InlineData(StatusCode.Unavailable, ConnectionFailureKind.Network)]
    [InlineData(StatusCode.DeadlineExceeded, ConnectionFailureKind.Timeout)]
    [InlineData(StatusCode.Unimplemented, ConnectionFailureKind.Unsupported)]
    [InlineData(StatusCode.InvalidArgument, ConnectionFailureKind.Syntax)]
    [InlineData(StatusCode.Internal, ConnectionFailureKind.Other)]
    public void A_grpc_status_maps_to_a_failure_kind_and_the_public_code_is_its_name(StatusCode status, ConnectionFailureKind kind)
    {
        var failure = new FlightSqlFailureMapper().Map(new FlightSqlException("server said something private", status), EngineId.Postgres);

        Assert.NotNull(failure);
        Assert.Equal(kind, failure!.Kind);
        Assert.Equal(status.ToString(), failure.Code);
    }

    [Fact]
    public void A_tls_failure_is_a_tls_failure_whatever_the_status()
    {
        var failure = new FlightSqlFailureMapper().Map(new FlightSqlException("handshake", StatusCode.Unavailable, isTls: true), EngineId.Postgres);
        Assert.Equal(ConnectionFailureKind.Tls, failure!.Kind);
    }

    [Fact]
    public void Other_exceptions_are_not_its_to_map() =>
        Assert.Null(new FlightSqlFailureMapper().Map(new InvalidOperationException("x"), EngineId.Postgres));
}
