using Microsoft.Data.Sqlite;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 13a U16: one account per person across every way of signing in. The key is the address (a Windows account's UPN), matched case-insensitively; a SAM name is never a key; an address nobody vouched for never links; an address one
/// account holds is refused to another. On every engine the stores run on, so the unique index on the key (a filtered index on SQL Server) is exercised where the shared engines are up.
/// </summary>
public sealed class AccountLinkingTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    [Theory]
    [InlineData("ada@example.com", "ada@example.com")]
    [InlineData("Ada@Example.COM", "ada@example.com")]
    [InlineData("  ADA@EXAMPLE.COM \t", "ada@example.com")]
    [InlineData("Ada.Lovelace@corp.example.com", "ada.lovelace@corp.example.com")]
    [InlineData("ada@corp", "ada@corp")]                       // a UPN suffix need not have a dot
    [InlineData("ÄDA@Example.com", "äda@example.com")]
    [InlineData("I@X.example", "i@x.example")]                  // invariant, not Turkish, casing
    public void An_address_is_trimmed_and_lower_cased_into_its_key(string address, string key) => Assert.Equal(key, AccountKey.Normalize(address));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ada")]
    [InlineData("ada@")]
    [InlineData("@example.com")]
    [InlineData("a@b@example.com")]
    [InlineData("ada lovelace@example.com")]
    [InlineData("ada@example..com")]
    [InlineData("ada@.example.com")]
    [InlineData("Ada <ada@example.com>")]
    [InlineData("CORP\\ada")]                                   // a SAM name is not an address
    [InlineData("corp\\ada@example.com")]
    public void Something_that_is_not_an_address_has_no_key(string? address) => Assert.Null(AccountKey.Normalize(address));

    [Fact]
    public void A_sam_name_is_recognised_and_is_never_the_same_key_as_the_upn()
    {
        Assert.True(AccountKey.IsSamName("CORP\\ada"));
        Assert.False(AccountKey.IsSamName("ada@corp.example.com"));
        // no guessing of a suffix: CORP\ada does not become ada@corp or ada@corp.local
        Assert.NotEqual(AccountKey.Normalize("ada@corp"), AccountKey.Normalize("CORP\\ada"));
        Assert.Null(AccountKey.Normalize("CORP\\ada"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_local_user_a_windows_account_and_a_second_windows_sign_in_are_one_user_whatever_the_case(string engine)
    {
        var users = new UserStore(_engines.Open(engine));
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
        var database = _engines.Open(engine);
        var users = new UserStore(database);
        var windows = new AccountLinker(users).SignInWindows("S-1-5-21-2", "CORP\\grace", "Grace@Corp.Example", UserRole.Viewer);

        Assert.Equal("Grace@Corp.Example", windows.Email);
        Assert.True(windows.EmailVerified);

        // an administrator inviting the same address (any case) as a new user is told which account has it
        var refused = Assert.Throws<AccountConflictException>(() => new InviteStore(database).Create(UserRole.Viewer, email: "grace@corp.example"));
        Assert.Contains("already belongs to", refused.Message);
        Assert.Equal(windows.Id, refused.OtherUserId);
        Assert.Empty(new InviteStore(database).ListOutstanding());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_windows_account_with_only_a_sam_name_is_not_matched_by_name_and_is_refused_as_a_new_user(string engine)
    {
        var users = new UserStore(_engines.Open(engine));
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
        var users = new UserStore(_engines.Open(engine));
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
        var users = new UserStore(_engines.Open(engine));
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
        var database = _engines.Open(engine);
        var users = new UserStore(database);
        var linker = new AccountLinker(users);
        var local = users.CreateUser("Ada Lovelace", "Ada@Example.com", UserRole.Admin, emailVerified: true);
        users.AddCredential(local.Id, CredentialMethods.Passkey, "ada-key", "pk");
        linker.SignInWindows("S-1-5-21-7", "CORP\\ada", "ada@example.com", UserRole.Admin);
        var session = new SessionStore(database).Create(local.Id);
        var invite = new InviteStore(database).Create(UserRole.Viewer, createdByUserId: local.Id, email: "bob@example.com");

        var home = linker.SignInHome("home-ada", "Ada Lovelace", "ADA@example.com", emailVerified: true, UserRole.Admin);

        Assert.Equal("home-ada", home.Id);
        Assert.Null(users.Get(local.Id));                                                    // one account, not two
        Assert.Single(users.List(), u => u.Email!.Equals("ada@example.com", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("home-ada", users.FindByCredential(CredentialMethods.Passkey, "ada-key")!.Id);
        Assert.Equal("home-ada", users.FindByCredential(CredentialMethods.Windows, "S-1-5-21-7")!.Id);
        Assert.Equal("home-ada", new SessionStore(database).Resolve(session.Id, users)!.Id);   // a session already open stays open, as the same user
        Assert.Equal("home-ada", users.FindByEmail("ada@example.com")!.Id);
        Assert.NotNull(invite);

        // a Windows sign-in afterwards is the home's user
        Assert.Equal("home-ada", linker.SignInWindows("S-1-5-21-7", "CORP\\ada", "ada@example.com", UserRole.Admin).Id);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_home_person_whose_address_a_mirror_of_another_home_person_holds_is_refused(string engine)
    {
        var users = new UserStore(_engines.Open(engine));
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
        var database = _engines.Open(engine);
        var invites = new InviteStore(database);
        var minted = invites.Create(UserRole.Viewer, email: "  New.Person@Example.com ");

        Assert.Equal("New.Person@Example.com", invites.Find(minted.Code)!.Email);
        Assert.Equal("New.Person@Example.com", invites.ListOutstanding().Single().Email);
        Assert.Throws<AccountConflictException>(() => invites.Create(UserRole.Viewer, email: "not an address"));
        var existing = new UserStore(database).CreateUser("Ada", null, UserRole.Viewer);
        Assert.Throws<ArgumentException>(() => invites.Create(UserRole.Viewer, forUserId: existing.Id, email: "ada@example.com"));
        Assert.Null(invites.Create(UserRole.Viewer, forUserId: existing.Id).Invite.Email);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_administrator_sets_an_address_and_an_old_account_then_links(string engine)
    {
        var users = new UserStore(_engines.Open(engine));
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
        var database = _engines.Open(engine);
        void Insert(string id, string? key)
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "INSERT INTO Users (Id, DisplayName, Email, Role, Enabled, CreatedAtUtc, EmailKey) VALUES ($id, 'x', NULL, 'Viewer', 1, '2026-01-01T00:00:00Z', $key);");
            cmd.Bind(database, "id", id);
            cmd.Bind(database, "key", (object?)key ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }

        Insert("a", "same@example.com");
        Assert.ThrowsAny<System.Data.Common.DbException>(() => Insert("b", "same@example.com"));
        Insert("c", null);
        Insert("d", null);          // many accounts with no vouched address: a filtered index, not one that treats null as a value
        Assert.Equal(3, new UserStore(database).List().Count);
    }

    [Fact]
    public void A_database_from_before_the_account_keys_opens_and_its_users_keep_signing_in_without_being_linkable()
    {
        var dir = Directory.CreateTempSubdirectory("dbdatakit-u16-");
        try
        {
            var path = Path.Combine(dir.FullName, "state.db");
            var before = new StateDatabase(path, new StateSchema([IdentitySchema.Accounts, IdentitySchema.Invites]));
            using (var connection = before.OpenConnection())
            {
                using var insert = before.Command(connection, "INSERT INTO Users (Id, DisplayName, Email, Role, Enabled, CreatedAtUtc) VALUES ('u1', 'Old', 'old@example.com', 'Admin', 1, '2026-01-01T00:00:00Z');");
                insert.ExecuteNonQuery();
                using var credential = before.Command(connection, "INSERT INTO UserCredentials (Id, UserId, Method, Subject, Secret, CreatedAtUtc) VALUES ('c1', 'u1', 'Passkey', 'old-key', 'pk', '2026-01-01T00:00:00Z');");
                credential.ExecuteNonQuery();
            }

            var after = new StateDatabase(path, new StateSchema([IdentitySchema.Accounts, IdentitySchema.Invites, IdentitySchema.AccountKeys]));
            var users = new UserStore(after);
            var old = users.FindByCredential(CredentialMethods.Passkey, "old-key")!;
            Assert.Equal("u1", old.Id);
            Assert.Equal("old@example.com", old.Email);                        // still shown
            Assert.False(old.EmailVerified);                                   // never matched on: nobody vouched for it
            Assert.Null(users.FindByEmail("old@example.com"));

            // the first invitation after the upgrade carries an address, as a new one must
            Assert.Equal("new@example.com", new InviteStore(after).Create(UserRole.Viewer, email: "new@example.com").Invite.Email);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            dir.Delete(recursive: true);
        }
    }
}
