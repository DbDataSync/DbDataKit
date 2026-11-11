namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 13a U16: one account per person across every way of signing in. The key is the address (a Windows account's UPN), matched case-insensitively; a SAM name is never a key; an address nobody vouched for never links; an address one
/// account holds is refused to another. On every engine the stores run on, so the unique index on the key (a filtered index on SQL Server) is exercised where the shared engines are up.
/// </summary>
public sealed class ModuleAccountLinkingTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine, TimeProvider? time = null) => _engines.Provision(engine).Open([IdentityModule.Module, NotificationsModule.Module], time: time);


    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_local_user_a_windows_account_and_a_second_windows_sign_in_are_one_user_whatever_the_case(string engine)
    {
        var users = new IdentityUserStore(Open(engine));
        var linker = new AccountLinker(users);

        // the administrator invited ada@example.com; she enrolled a passkey
        var local = users.CreateUser("Ada Lovelace", "ada@example.com", UserRole.Viewer, emailVerified: true);
        users.AddCredential(local.Id, CredentialMethods.Passkey, "cred-1", "public-key");

        // the domain says her UPN is ADA@EXAMPLE.COM: the same person
        var windows = linker.SignInWindows("S-1-5-21-1", "CORP\\ada", "ADA@EXAMPLE.COM", UserRole.Admin);
        Assert.Equal(local.Id, windows.Id);
        Assert.Equal(UserRole.Admin, windows.Role);                 // the group is the authority at a Windows sign-in, as it always was
        Assert.Equal(local.Id, users.FindByCredential(CredentialMethods.Passkey, "cred-1")!.Id);
        Assert.Equal(UserRole.Admin, users.FindByCredential(CredentialMethods.Passkey, "cred-1")!.Role);   // and so the passkey is the same roles
        Assert.Equal(2, users.CredentialsOf(local.Id).Count);

        // later, the same SID again, by its SID alone
        Assert.Equal(local.Id, linker.SignInWindows("S-1-5-21-1", "CORP\\ada", "ada@example.com", UserRole.Admin).Id);
        Assert.Single(users.List());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_new_windows_account_makes_a_user_keyed_on_its_upn_that_an_invitation_for_the_same_address_is_refused(string engine)
    {
        var database = Open(engine);
        var users = new IdentityUserStore(database);
        var windows = new AccountLinker(users).SignInWindows("S-1-5-21-2", "CORP\\grace", "Grace@Corp.Example", UserRole.Viewer);

        Assert.Equal("Grace@Corp.Example", windows.Email);
        Assert.True(windows.EmailVerified);

        // an administrator inviting the same address (any case) as a new user is told which account has it
        var refused = Assert.Throws<AccountConflictException>(() => new IdentityInviteStore(database).Create(UserRole.Viewer, email: "grace@corp.example"));
        Assert.Contains("already belongs to", refused.Message);
        Assert.Equal(windows.Id, refused.OtherUserId);
        Assert.Empty(new IdentityInviteStore(database).ListOutstanding());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_windows_account_with_only_a_sam_name_is_not_matched_by_name_and_is_refused_as_a_new_user(string engine)
    {
        var users = new IdentityUserStore(Open(engine));
        var linker = new AccountLinker(users);
        var local = users.CreateUser("Ada", "ada@corp", UserRole.Viewer, emailVerified: true);

        // CORP\ada is not guessed to be ada@corp: nothing is linked and nothing is made
        var refused = Assert.Throws<AccountConflictException>(() => linker.SignInWindows("S-1-5-21-3", "CORP\\ada", upn: null, UserRole.Admin));
        Assert.Contains("no user principal name", refused.Message);
        Assert.Throws<AccountConflictException>(() => linker.SignInWindows("S-1-5-21-3", "CORP\\ada", upn: "CORP\\ada", UserRole.Admin));
        Assert.Single(users.List());
        Assert.Null(users.FindByCredential(CredentialMethods.Windows, "S-1-5-21-3"));

        // an account that already signs in by its SID keeps working when the domain gives no UPN this time
        var known = linker.SignInWindows("S-1-5-21-4", "CORP\\bob", "bob@corp", UserRole.Viewer);
        Assert.Equal(known.Id, linker.SignInWindows("S-1-5-21-4", "CORP\\bob", upn: null, UserRole.Viewer).Id);
        Assert.NotEqual(local.Id, known.Id);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_sign_in_that_would_take_an_address_another_account_holds_is_refused_with_a_message(string engine)
    {
        var users = new IdentityUserStore(Open(engine));
        var linker = new AccountLinker(users);
        var ada = users.CreateUser("Ada", "ada@example.com", UserRole.Viewer, emailVerified: true);
        var bob = linker.SignInWindows("S-1-5-21-5", "CORP\\bob", "bob@example.com", UserRole.Viewer);

        // Bob's domain account is now said to be ada@example.com: that is Ada's, and Bob is not her
        var refused = Assert.Throws<AccountConflictException>(() => linker.SignInWindows("S-1-5-21-5", "CORP\\bob", "Ada@Example.com", UserRole.Viewer));
        Assert.Contains("already used by another account", refused.Message);
        Assert.Equal(ada.Id, refused.OtherUserId);
        Assert.Equal("bob@example.com", users.Get(bob.Id)!.Email);

        // nor can an administrator give one account another's address, nor create a second account at it
        Assert.Throws<AccountConflictException>(() => users.SetVerifiedEmail(bob.Id, "ADA@example.com"));
        Assert.Throws<AccountConflictException>(() => users.CreateUser("Ada again", "ada@EXAMPLE.com", UserRole.Viewer, emailVerified: true));
        Assert.Equal(2, users.List().Count);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_address_nobody_vouched_for_neither_finds_an_account_nor_is_found_by_one(string engine)
    {
        var users = new IdentityUserStore(Open(engine));
        var linker = new AccountLinker(users);
        // typed by whoever redeemed an invitation, as every address was before this slice
        var typed = users.CreateUser("Mallory", "ceo@example.com", UserRole.Admin);
        users.AddCredential(typed.Id, CredentialMethods.Passkey, "mallory-key", "pk");
        Assert.False(typed.EmailVerified);
        Assert.Null(users.FindByEmail("ceo@example.com"));

        // the real person arrives by Windows and by the home: neither is joined to Mallory's account
        var windows = linker.SignInWindows("S-1-5-21-6", "CORP\\ceo", "ceo@example.com", UserRole.Viewer);
        Assert.NotEqual(typed.Id, windows.Id);
        Assert.Equal(UserRole.Admin, users.Get(typed.Id)!.Role);
        Assert.Single(users.CredentialsOf(typed.Id));

        // a home that does not say the address is verified links nothing either: a local user holds ada@example.com, and the home person stays a separate one
        var local = users.CreateUser("Ada", "ada@example.com", UserRole.Admin, emailVerified: true);
        users.AddCredential(local.Id, CredentialMethods.Passkey, "ada-key", "pk");
        var unverifiedHome = linker.SignInHome("home-1", "Someone", "ada@example.com", emailVerified: false, UserRole.Viewer);
        Assert.Equal("home-1", unverifiedHome.Id);
        Assert.False(unverifiedHome.EmailVerified);
        Assert.NotNull(users.Get(local.Id));
        Assert.Equal(local.Id, users.FindByEmail("ada@example.com")!.Id);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_person_the_home_knows_takes_over_the_local_account_with_the_home_id_and_keeps_everything_it_had(string engine)
    {
        var database = Open(engine);
        var users = new IdentityUserStore(database);
        var linker = new AccountLinker(users);
        var local = users.CreateUser("Ada Lovelace", "Ada@Example.com", UserRole.Admin, emailVerified: true);
        users.AddCredential(local.Id, CredentialMethods.Passkey, "ada-key", "pk");
        linker.SignInWindows("S-1-5-21-7", "CORP\\ada", "ada@example.com", UserRole.Admin);
        var session = new IdentitySessionStore(database).Create(local.Id);
        var invite = new IdentityInviteStore(database).Create(UserRole.Viewer, createdByUserId: local.Id, email: "bob@example.com");

        var home = linker.SignInHome("home-ada", "Ada Lovelace", "ADA@example.com", emailVerified: true, UserRole.Admin);

        Assert.Equal("home-ada", home.Id);
        Assert.Null(users.Get(local.Id));                                                    // one account, not two
        Assert.Single(users.List(), u => u.Email!.Equals("ada@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("home-ada", users.FindByCredential(CredentialMethods.Passkey, "ada-key")!.Id);
        Assert.Equal("home-ada", users.FindByCredential(CredentialMethods.Windows, "S-1-5-21-7")!.Id);
        Assert.Equal("home-ada", new IdentitySessionStore(database).Resolve(session.Id, users)!.Id);   // a session already open stays open, as the same user
        Assert.Equal("home-ada", users.FindByEmail("ada@example.com")!.Id);
        Assert.NotNull(invite);

        // a Windows sign-in afterwards is the home's user
        Assert.Equal("home-ada", linker.SignInWindows("S-1-5-21-7", "CORP\\ada", "ada@example.com", UserRole.Admin).Id);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_home_person_whose_address_a_mirror_of_another_home_person_holds_is_refused(string engine)
    {
        var users = new IdentityUserStore(Open(engine));
        var linker = new AccountLinker(users);
        linker.SignInHome("home-1", "Ada", "ada@example.com", emailVerified: true, UserRole.Viewer);          // a mirror: no credential here

        var refused = Assert.Throws<AccountConflictException>(() => linker.SignInHome("home-2", "Ada", "ada@example.com", emailVerified: true, UserRole.Viewer));
        Assert.Contains("under a different id", refused.Message);
        Assert.Null(users.Get("home-2"));
        Assert.NotNull(users.Get("home-1"));

        // a home person already mirrored here who is now said to hold a local user's address is refused too, and keeps the address they had
        var local = users.CreateUser("Bob", "bob@example.com", UserRole.Viewer, emailVerified: true);
        Assert.Throws<AccountConflictException>(() => linker.SignInHome("home-1", "Ada", "BOB@example.com", emailVerified: true, UserRole.Viewer));
        Assert.Equal("ada@example.com", users.Get("home-1")!.Email);
        Assert.NotNull(users.Get(local.Id));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_invitation_carries_the_address_an_administrator_entered(string engine)
    {
        var database = Open(engine);
        var invites = new IdentityInviteStore(database);
        var minted = invites.Create(UserRole.Viewer, email: "  New.Person@Example.com ");

        Assert.Equal("New.Person@Example.com", invites.Find(minted.Code)!.Email);
        Assert.Equal("New.Person@Example.com", invites.ListOutstanding().Single().Email);
        Assert.Throws<AccountConflictException>(() => invites.Create(UserRole.Viewer, email: "not an address"));
        var existing = new IdentityUserStore(database).CreateUser("Ada", null, UserRole.Viewer);
        Assert.Throws<ArgumentException>(() => invites.Create(UserRole.Viewer, forUserId: existing.Id, email: "ada@example.com"));
        Assert.Null(invites.Create(UserRole.Viewer, forUserId: existing.Id).Invite.Email);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_administrator_sets_an_address_and_an_old_account_then_links(string engine)
    {
        var users = new IdentityUserStore(Open(engine));
        var linker = new AccountLinker(users);
        var old = users.CreateUser("Old Local", "typed@example.com", UserRole.Admin);   // made before addresses were keyed
        users.AddCredential(old.Id, CredentialMethods.Passkey, "old-key", "pk");

        Assert.False(users.Get(old.Id)!.EmailVerified);
        var set = users.SetVerifiedEmail(old.Id, "Old.Local@Example.com");
        Assert.True(set.EmailVerified);
        Assert.Equal(old.Id, linker.SignInWindows("S-1-5-21-8", "CORP\\old", "old.local@example.com", UserRole.Viewer).Id);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_database_itself_refuses_a_second_account_at_the_same_key_and_allows_many_without_one(string engine)
    {
        var database = Open(engine);
        void Insert(string id, string? key)
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command("identity", connection, "INSERT INTO {table:account} (id, display_name, email, role, enabled, created_at, email_key) VALUES ($id, 'x', NULL, 'Viewer', $enabled, $at, $key);");
            cmd.Bind(database, "id", id).Bind(database, "enabled", true).Bind(database, "at", DateTimeOffset.Parse("2026-01-01T00:00:00Z")).Bind(database, "key", key, System.Data.DbType.String);
            cmd.ExecuteNonQuery();
        }

        Insert("a", "same@example.com");
        Assert.ThrowsAny<System.Data.Common.DbException>(() => Insert("b", "same@example.com"));
        Insert("c", null);
        Insert("d", null);          // many accounts with no vouched address: a filtered index, not one that treats null as a value
        Assert.Equal(3, new IdentityUserStore(database).List().Count);
    }
}
