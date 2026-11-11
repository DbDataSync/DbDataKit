using System.Data.Common;
using System.Net.Sockets;
using System.Security.Authentication;
using DbDataKit.Dialect;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Abstractions.Tests;

public class FailureTests
{
    private sealed class FakeDbException(string message, string? state) : DbException(message)
    {
        public override string? SqlState => state;
    }

    private sealed class NumberMapper : IFailureMapper
    {
        public ConnectionFailure? Map(Exception exception, EngineId engine) =>
            exception is FakeDbException { Message: var m } && m.StartsWith("N18456", StringComparison.Ordinal)
                ? new ConnectionFailure(ConnectionFailureKind.Login, "18456", engine) : null;
    }

    [Theory]
    [InlineData("28P01", ConnectionFailureKind.Login)]
    [InlineData("28000", ConnectionFailureKind.Login)]
    [InlineData("08001", ConnectionFailureKind.Network)]
    [InlineData("08S01", ConnectionFailureKind.Network)]
    [InlineData("57014", ConnectionFailureKind.Timeout)]
    [InlineData("HYT00", ConnectionFailureKind.Timeout)]
    [InlineData("42501", ConnectionFailureKind.Permission)]
    [InlineData("42601", ConnectionFailureKind.Syntax)]
    [InlineData("42000", ConnectionFailureKind.Syntax)]
    [InlineData("0A000", ConnectionFailureKind.Unsupported)]
    [InlineData("IM002", ConnectionFailureKind.Unsupported)]
    public void Sql_state_classes_map_to_kinds(string state, ConnectionFailureKind kind)
    {
        var f = new FailureClassifier().Classify(new FakeDbException("whatever", state), EngineId.Postgres);
        Assert.Equal(kind, f.Kind);
        Assert.Equal(state, f.Code);
        Assert.Equal(EngineId.Postgres, f.Engine);
    }

    [Fact]
    public void Public_text_never_contains_raw_driver_text()
    {
        const string raw = "Login failed for user 'sa'. Password was hunter2 on server prod-db-01";
        var f = new FailureClassifier().Classify(new FakeDbException(raw, "28000"), EngineId.SqlServer);
        Assert.DoesNotContain("hunter2", f.PublicMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("prod-db-01", f.PublicMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("sa'", f.ToString(), StringComparison.Ordinal);
        Assert.Null(f.Raw);
        var ex = new FailureClassifier().ToException(new FakeDbException(raw, "28000"), EngineId.SqlServer);
        Assert.DoesNotContain("hunter2", ex.Message, StringComparison.Ordinal);
        Assert.Null(ex.InnerException);
    }

    [Fact]
    public void Raw_text_only_with_the_explicit_flag_and_redacted()
    {
        var redactor = new Redactor();
        redactor.Register("hunter2");
        var f = new FailureClassifier(redactor: redactor, includeRaw: true).Classify(new FakeDbException("bad hunter2", "28000"), EngineId.Postgres);
        Assert.NotNull(f.Raw);
        Assert.DoesNotContain("hunter2", f.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", f.PublicMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Engine_mappers_win_and_inner_exceptions_are_searched()
    {
        var c = new FailureClassifier([new NumberMapper()]);
        var wrapped = new InvalidOperationException("outer", new FakeDbException("N18456 x", "42000"));
        var f = c.Classify(wrapped, EngineId.SqlServer);
        Assert.Equal(ConnectionFailureKind.Login, f.Kind);
        Assert.Equal("18456", f.Code);
    }

    [Fact]
    public void Exception_types_classify_when_there_is_no_state()
    {
        var c = new FailureClassifier();
        Assert.Equal(ConnectionFailureKind.Timeout, c.Classify(new TimeoutException(), EngineId.Oracle).Kind);
        Assert.Equal(ConnectionFailureKind.Network, c.Classify(new SocketException(10061), EngineId.Oracle).Kind);
        Assert.Equal(ConnectionFailureKind.Tls, c.Classify(new AuthenticationException("cert"), EngineId.Oracle).Kind);
        Assert.Equal(ConnectionFailureKind.Network, c.Classify(new InvalidOperationException("x", new IOException("io")), EngineId.Oracle).Kind);
        var other = c.Classify(new InvalidCastException("secret text"), EngineId.Oracle);
        Assert.Equal(ConnectionFailureKind.Other, other.Kind);
        Assert.Equal("InvalidCastException", other.Code);
        Assert.DoesNotContain("secret text", other.PublicMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Aggregate_exceptions_are_unwrapped()
    {
        var f = new FailureClassifier().Classify(new AggregateException(new FakeDbException("x", "28000")), EngineId.Postgres);
        Assert.Equal(ConnectionFailureKind.Login, f.Kind);
    }

    [Fact]
    public void Read_only_session_applies_its_statements_in_order()
    {
        var s = new ReadOnlySession("x", ReadOnlyEnforcement.SessionDefault, ["a", "b"], new Dictionary<string, string> { ["k"] = "v" }, "n");
        Assert.Equal(["a", "b"], s.Statements);
        Assert.Equal("v", s.ConnectionStringKeys["k"]);
        Assert.Equal(ReadOnlyEnforcement.None, ReadOnlySession.NoneAvailable("e", "n").Enforcement);
    }
}
