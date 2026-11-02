using System.Text.Json.Nodes;
using DbDataKit.DataModel;
using DbDataKit.Web.Trust;
using DbDataStore.Server;

namespace DbDataStore.Tests;

/// <summary>Which writes wait for approval, and the requests a server keeps (plan 19, Z07). The write and approve routes are in <see cref="StoreDataTests"/>.</summary>
public sealed class StoreChangeRequestTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dds-requests-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    private static ValidatedToken Token(string subject) =>
        new(new PairedClient("c", "console", [], ServerRole.Admin), subject, subject, false, ServerRole.Author, new Dictionary<string, ServerRole>(), "t", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static DataModel Model(ApprovalLevel approval = ApprovalLevel.None, AgentWrites agents = AgentWrites.Request) => new("m", null, "c", approval, [], InstanceMode.None, agents);

    [Fact]
    public void An_agents_write_waits_unless_the_model_lets_agents_write_and_a_persons_waits_only_when_the_model_asks()
    {
        Assert.True(StoreChangeRequests.Needs(Model(), Token("agent:a")));
        Assert.False(StoreChangeRequests.Needs(Model(agents: AgentWrites.Direct), Token("agent:a")));
        Assert.False(StoreChangeRequests.Needs(Model(), Token("ada")));
        Assert.True(StoreChangeRequests.Needs(Model(ApprovalLevel.Steward), Token("ada")));
        Assert.True(StoreChangeRequests.Needs(Model(ApprovalLevel.Owner, AgentWrites.Direct), Token("ada")));
        Assert.False(StoreChangeRequests.Needs(Model(ApprovalLevel.Owner, AgentWrites.Direct), Token("agent:a")));
    }

    [Fact]
    public void An_owners_approval_needs_an_administrator_and_a_stewards_an_author()
    {
        Assert.Equal(ServerRole.Admin, StoreChangeRequests.Approver(Model(ApprovalLevel.Owner)));
        Assert.Equal(ServerRole.Author, StoreChangeRequests.Approver(Model(ApprovalLevel.Steward)));
        Assert.Equal(ServerRole.Author, StoreChangeRequests.Approver(Model()));
    }

    [Fact]
    public void A_request_is_kept_as_proposed_listed_by_state_and_decided_once_recorded()
    {
        var changes = (JsonArray)JsonNode.Parse("""[{"action":"insert","entity":"t","values":{"a":1}}]""")!;
        var proposed = StoreChangeRequests.Propose(directory, "m", "default", Token("agent:a"), "add a", changes, DateTimeOffset.UnixEpoch);
        Assert.True(proposed.ByAgent);
        Assert.True(StoreChangeRequests.IsValidId(proposed.Id));

        var found = StoreChangeRequests.Find(directory, "m", "default", proposed.Id)!;
        Assert.Equal("add a", found.Description);
        Assert.Equal(changes.ToJsonString(), found.Changes.ToJsonString());
        Assert.Single(StoreChangeRequests.List(directory, "m", "default", ChangeRequest.Pending));

        StoreChangeRequests.Decide(directory, found, ChangeRequest.Approved, Token("ada"), "ok", DateTimeOffset.UnixEpoch);
        Assert.Empty(StoreChangeRequests.List(directory, "m", "default", ChangeRequest.Pending));
        var decided = StoreChangeRequests.Find(directory, "m", "default", proposed.Id)!;
        Assert.Equal(("approved", "ada", "ok"), (decided.State, decided.DecidedBy, decided.Note));
        Assert.Empty(StoreChangeRequests.List(directory, "m", "other"));
        Assert.Null(StoreChangeRequests.Find(directory, "m", "default", "../../etc"));
    }
}
