using DbDataKit.State;
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

    [Fact]
    public void A_change_request_a_trusted_app_proposes_carries_the_approval_label_in_the_stores_list()
    {
        var folder = Path.Combine(Path.GetTempPath(), "cr-via-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            var requests = new GroupChangeRequests(StateDatabase.OpenModules(Path.Combine(folder, "state.db"), [DbDataGateway.Server.State.ServingModule.Module]));
            var changes = new System.Text.Json.Nodes.JsonArray();
            var direct = requests.Propose("territories", new StoreCaller("u1", "Ada", false, ServerRole.Author), "by hand", changes);
            var through = requests.Propose("territories", new StoreCaller("u1", "Ada", false, ServerRole.Author, "sales-portal 3, approved by grace (apr_1)"), "from the app", changes);

            var listed = requests.List("territories").ToDictionary(r => r.Id);
            Assert.Equal("Ada", listed[direct.Id].ProposedByName);
            Assert.Equal("Ada, through sales-portal 3, approved by grace (apr_1)", listed[through.Id].ProposedByName);
            Assert.Equal("Ada, through sales-portal 3, approved by grace (apr_1)", (string?)listed[through.Id].ToJson()["proposed_by_name"]);
            Assert.Equal("u1", listed[through.Id].ProposedBy);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }
}
