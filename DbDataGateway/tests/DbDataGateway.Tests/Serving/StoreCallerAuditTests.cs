using DbDataKit.Web.Trust;
using DbDataGateway.Server.Serving;

namespace DbDataGateway.Tests.Serving;

/// <summary>Plan 26, AP15 and AP16: the Store's audit row of a write made through a trusted app names the person, the app version and the approver.</summary>
public sealed class StoreCallerAuditTests
{
    [Fact]
    public void A_call_through_a_trusted_app_is_named_with_the_app_its_version_and_the_approval()
    {
        var direct = new StoreCaller("u1", "Ada", false, ServerRole.Author);
        var through = direct with { Via = "sales-portal 3, approved by grace (apr_1)" };

        Assert.Equal("Ada", direct.AuditName);
        Assert.Equal("Ada, through sales-portal 3, approved by grace (apr_1)", through.AuditName);
        Assert.Equal("u1, through x", new StoreCaller("u1", null, false, ServerRole.Author, "x").AuditName);
    }
}
