using System.Data.Common;

namespace DbDataKit.State.Tests;

/// <summary>Users, credentials, sessions and invites on every engine: the tables with foreign keys and bounded key columns, where DDL that got a type wrong would fail first.</summary>
public sealed class IdentityStoreTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_user_and_their_credentials_round_trip(string engine)
    {
        var users = new UserStore(_engines.Open(engine));

        Assert.False(users.Any());
        var user = users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Admin);
        users.AddCredential(user.Id, CredentialMethods.Windows, "S-1-5-21-1", secret: null, label: "work laptop");
        users.AddCredential(user.Id, CredentialMethods.Passkey, "cred-1", secret: "public-key");

        Assert.True(users.Any());
        Assert.Equal(user.Id, users.FindByCredential(CredentialMethods.Windows, "S-1-5-21-1")?.Id);
        Assert.Null(users.FindByCredential(CredentialMethods.Windows, "S-1-5-21-2"));
        Assert.Equal(2, users.CredentialsOf(user.Id).Count);
        Assert.Equal("Ada Lovelace", users.Get(user.Id)!.DisplayName);
        Assert.Equal(UserRole.Admin, users.Get(user.Id)!.Role);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_credential_subject_belongs_to_one_user(string engine)
    {
        var users = new UserStore(_engines.Open(engine));
        var a = users.CreateUser("A", null, UserRole.Viewer);
        var b = users.CreateUser("B", null, UserRole.Viewer);
        users.AddCredential(a.Id, CredentialMethods.Windows, "S-1-5-21-9");

        Assert.ThrowsAny<DbException>(() => users.AddCredential(b.Id, CredentialMethods.Windows, "S-1-5-21-9"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Role_and_enabled_changes_are_stored(string engine)
    {
        var users = new UserStore(_engines.Open(engine));
        var user = users.CreateUser("Grace", null, UserRole.Viewer);

        users.SetRole(user.Id, UserRole.Admin);
        users.SetEnabled(user.Id, false);

        var stored = users.Get(user.Id)!;
        Assert.Equal(UserRole.Admin, stored.Role);
        Assert.False(stored.Enabled);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_session_resolves_its_user_until_it_is_deleted_or_the_user_is_disabled(string engine)
    {
        var database = _engines.Open(engine);
        var users = new UserStore(database);
        var sessions = new SessionStore(database);
        var user = users.CreateUser("Linus", null, UserRole.Viewer);

        var session = sessions.Create(user.Id);
        Assert.Equal(user.Id, sessions.Resolve(session.Id, users)?.Id);
        Assert.Null(sessions.Resolve("not-a-session", users));

        sessions.Delete(session.Id);
        Assert.Null(sessions.Resolve(session.Id, users));

        var second = sessions.Create(user.Id);
        users.SetEnabled(user.Id, false);
        Assert.Null(sessions.Resolve(second.Id, users));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Deleting_all_of_a_users_sessions_signs_them_out_everywhere(string engine)
    {
        var database = _engines.Open(engine);
        var users = new UserStore(database);
        var sessions = new SessionStore(database);
        var user = users.CreateUser("Margaret", null, UserRole.Viewer);
        var one = sessions.Create(user.Id);
        var two = sessions.Create(user.Id);

        sessions.DeleteAllFor(user.Id);

        Assert.Null(sessions.Resolve(one.Id, users));
        Assert.Null(sessions.Resolve(two.Id, users));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_invite_is_found_by_its_code_and_redeemed_once(string engine)
    {
        var database = _engines.Open(engine);
        var users = new UserStore(database);
        var invites = new InviteStore(database);
        var user = users.CreateUser("Alan", null, UserRole.Admin);

        var minted = invites.Create(UserRole.Viewer, createdByUserId: user.Id);

        Assert.Equal(minted.Invite.Id, invites.Find(minted.Code)?.Id);
        Assert.Null(invites.Find("not-a-code"));
        Assert.Single(invites.ListOutstanding());
        Assert.True(invites.Redeem(minted.Invite.Id, user.Id));
        Assert.False(invites.Redeem(minted.Invite.Id, user.Id));
        Assert.Null(invites.Find(minted.Code));
        Assert.Empty(invites.ListOutstanding());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_expired_invite_is_not_found_and_the_bootstrap_invite_can_be_withdrawn(string engine)
    {
        var invites = new InviteStore(_engines.Open(engine));

        var expired = invites.Create(UserRole.Admin, lifetime: TimeSpan.FromSeconds(-1));
        Assert.Null(invites.Find(expired.Code));

        var bootstrap = invites.Create(UserRole.Admin);
        Assert.NotNull(invites.Find(bootstrap.Code));
        invites.DeleteBootstrapInvites();
        Assert.Null(invites.Find(bootstrap.Code));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_code_itself_is_never_stored(string engine)
    {
        var database = _engines.Open(engine);
        var minted = new InviteStore(database).Create(UserRole.Admin);

        using var connection = database.OpenConnection();
        using var command = database.Command(connection, "SELECT CodeHash FROM Invites;");
        var stored = (string)command.ExecuteScalar()!;

        Assert.NotEqual(minted.Code, stored);
        Assert.DoesNotContain(minted.Code, stored);
    }
}
