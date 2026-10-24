using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Dialect;
using DbDataSync.Core.Sql;
using Xunit;

namespace DbDataSync.Core.Tests;

/// <summary>A driver's failure reaches an operator as the shared classifier's message, never as the driver's own text (plan 13, A10).</summary>
[Collection("DriverFailures")]
public sealed class DriverFailuresTests : IDisposable
{
    private sealed class FakeDbException(string message, string? sqlState = null) : DbException(message)
    {
        public override string? SqlState { get; } = sqlState;
    }

    public DriverFailuresTests() => DriverFailures.Configure([], includeDetails: false);
    public void Dispose() { DriverFailures.Configure([], includeDetails: false); DriverFailures.SetLog(null); }

    [Fact]
    public void A_driver_failure_is_the_classified_public_message_with_its_code()
    {
        var text = DriverFailures.Describe(new FakeDbException("password authentication failed for user \"alice\" at 10.1.2.3", "28P01"), "Postgres");
        Assert.Equal("The login was rejected. [postgres 28P01]", text);
    }

    [Fact]
    public void A_wrapped_driver_failure_is_classified_from_the_inner_exception()
    {
        var text = DriverFailures.Describe(new InvalidOperationException("outer", new FakeDbException("host 10.1.2.3 refused", "08006")), "Postgres");
        Assert.DoesNotContain("10.1.2.3", text);
        Assert.StartsWith("The server could not be reached.", text);
    }

    [Fact]
    public void Syncs_own_messages_pass_through_unchanged()
    {
        Assert.Equal("The mapping 'orders' has no key.", DriverFailures.Describe(new InvalidOperationException("The mapping 'orders' has no key.")));
    }

    [Fact]
    public void The_drivers_text_is_logged_redacted_and_shown_only_with_details_on()
    {
        var logged = new List<string>();
        DriverFailures.SetLog(logged.Add);
        DriverFailures.Register("s3cret-pw");
        var ex = new FakeDbException("login failed, password s3cret-pw, server db1", "28000");

        Assert.DoesNotContain("db1", DriverFailures.Describe(ex, "Postgres"));
        Assert.Single(logged);
        Assert.Contains("db1", logged[0]);
        Assert.DoesNotContain("s3cret-pw", logged[0]);

        DriverFailures.Configure([], includeDetails: true);
        var detailed = DriverFailures.Describe(ex, "Postgres");
        Assert.Contains("db1", detailed);
        Assert.DoesNotContain("s3cret-pw", detailed);
    }

    [Fact]
    public void The_stored_detail_of_a_driver_failure_is_withheld_unless_details_are_on()
    {
        var ex = new FakeDbException("boom at db1");
        Assert.Null(DriverFailures.Detail(ex));
        Assert.NotNull(DriverFailures.Detail(new InvalidOperationException("ours")));
        DriverFailures.Configure([], includeDetails: true);
        Assert.Contains("db1", DriverFailures.Detail(ex));
    }

    [Fact]
    public void A_mapper_whose_package_is_missing_is_skipped()
    {
        DriverFailures.Configure([new KeyValuePair<EngineId, IFailureMapper>(EngineId.SqlServer, new MissingPackageMapper())], includeDetails: false);
        var text = DriverFailures.Describe(new FakeDbException("x", "42P01"), "MsSql");
        Assert.StartsWith("The statement was not accepted", text);
    }

    private sealed class MissingPackageMapper : IFailureMapper
    {
        public ConnectionFailure? Map(Exception exception, EngineId engine) => throw new FileNotFoundException("Microsoft.Data.SqlClient");
    }
}
