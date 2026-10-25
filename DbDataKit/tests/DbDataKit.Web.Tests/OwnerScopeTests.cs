using DbDataKit.Web.Trust;
using Xunit;

namespace DbDataKit.Web.Tests;

/// <summary>The owner rules of a publishing server client (plan 13, W12): it creates objects, changes and deletes only its own, and never the policy of the server or what someone else made.</summary>
public class OwnerScopeTests
{
    private const ServerRole Everything = ServerRole.Viewer | ServerRole.Operator | ServerRole.Author | ServerRole.Approver | ServerRole.Admin | ServerRole.Publisher;

    private static ValidatedToken Token(ServerRole roles, string client = "build-1", ClientKind kind = ClientKind.Server, ServerRole ceiling = Everything) =>
        new(new PairedClient(client, "Build", new byte[32], ceiling, kind), "user-7", "Ada", false, roles, new Dictionary<string, ServerRole>(),
            "token-0001", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5));

    private static ValidatedToken Publisher(string client = "build-1") => Token(ServerRole.Publisher, client, ceiling: ServerRole.Publisher);

    [Fact]
    public void A_publisher_creates_and_the_object_is_stamped_with_its_client()
    {
        var token = Publisher();

        Assert.True(OwnerScope.Authorize(token, ObjectAction.Create).Allowed);
        Assert.Equal("build-1", OwnerScope.Stamp(token));
    }

    [Theory]
    [InlineData(ObjectAction.Change)]
    [InlineData(ObjectAction.Delete)]
    public void A_publisher_changes_and_deletes_only_what_it_made(ObjectAction action)
    {
        Assert.True(OwnerScope.Authorize(Publisher(), action, new ManagedObject("build-1")).Allowed);

        var other = OwnerScope.Authorize(Publisher(), action, new ManagedObject("build-2"));
        Assert.False(other.Allowed);
        Assert.Contains("another client", other.Reason);

        var byHand = OwnerScope.Authorize(Publisher(), action, new ManagedObject(null));
        Assert.False(byHand.Allowed);
        Assert.False(OwnerScope.Authorize(Publisher(), action, new ManagedObject("")).Allowed);
        Assert.Contains("not made by a publisher", byHand.Reason);
    }

    [Fact]
    public void The_servers_policy_is_never_a_publishers_even_if_it_carries_the_clients_id()
    {
        var decision = OwnerScope.Authorize(Publisher(), ObjectAction.Change, new ManagedObject("build-1", IsServerPolicy: true));

        Assert.False(decision.Allowed);
        Assert.Contains("policy", decision.Reason);
    }

    [Fact]
    public void Changing_without_naming_the_object_is_refused()
    {
        Assert.False(OwnerScope.Authorize(Publisher(), ObjectAction.Change).Allowed);
        Assert.False(OwnerScope.Authorize(Publisher(), ObjectAction.Delete, null).Allowed);
    }

    [Theory]
    [InlineData(ServerRole.None)]
    [InlineData(ServerRole.Viewer)]
    [InlineData(ServerRole.Operator | ServerRole.Author | ServerRole.Approver)]
    public void Without_the_publisher_or_admin_role_nothing_is_allowed(ServerRole held)
    {
        var token = Token(held);

        Assert.False(OwnerScope.Authorize(token, ObjectAction.Create).Allowed);
        Assert.False(OwnerScope.Authorize(token, ObjectAction.Change, new ManagedObject("build-1")).Allowed);
    }

    [Fact]
    public void The_consoles_ceiling_limits_what_a_token_may_claim()
    {
        // a token claiming publisher under a ceiling without it holds nothing
        var capped = Token(ServerRole.Publisher, ceiling: ServerRole.Viewer);

        Assert.False(OwnerScope.Authorize(capped, ObjectAction.Create).Allowed);
    }

    [Fact]
    public void An_admin_is_not_limited_by_owner()
    {
        var admin = Token(ServerRole.Admin, "console-1", ClientKind.Console);

        Assert.True(OwnerScope.Authorize(admin, ObjectAction.Change, new ManagedObject(null)).Allowed);
        Assert.True(OwnerScope.Authorize(admin, ObjectAction.Delete, new ManagedObject("build-2")).Allowed);
        Assert.True(OwnerScope.Authorize(admin, ObjectAction.Change, new ManagedObject("x", IsServerPolicy: true)).Allowed);
    }

    [Theory]
    [InlineData(ClientKind.Console, ServerRole.Admin, true)]
    [InlineData(ClientKind.Cli, ServerRole.Admin, true)]
    [InlineData(ClientKind.Console, ServerRole.Approver, false)]
    [InlineData(ClientKind.Server, ServerRole.Admin, false)]
    [InlineData(ClientKind.Server, ServerRole.Publisher, false)]
    public void Adopting_an_object_is_a_persons_act_with_the_admin_role(ClientKind kind, ServerRole held, bool allowed)
    {
        Assert.Equal(allowed, OwnerScope.AuthorizeAdopt(Token(held, "client-1", kind)).Allowed);
    }

    [Fact]
    public void The_publisher_role_is_a_role_of_its_own()
    {
        Assert.Equal(ServerRole.Publisher, ServerRoles.Close(ServerRole.Publisher));
        Assert.False(ServerRoles.Allows(ServerRole.Publisher, ServerRole.Viewer));
        Assert.False(ServerRoles.Allows(ServerRole.Admin | ServerRole.Approver, ServerRole.Publisher));
        Assert.True(ServerRoles.TryParse(["publisher"], out var parsed));
        Assert.Equal(ServerRole.Publisher, parsed);
        Assert.Equal(["publisher"], ServerRoles.Names(ServerRole.Publisher));
    }
}
