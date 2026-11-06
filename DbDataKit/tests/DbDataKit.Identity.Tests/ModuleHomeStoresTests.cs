using System.Security.Cryptography;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity.Tests;

/// <summary>Plan 29a Q09: the home's clients, signing keys, access tokens, service accounts and external groups on the <c>identity</c> module, on every engine.</summary>
public sealed class ModuleHomeStoresTests : IDisposable
{
    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(DateTimeOffset.Parse("2026-11-06T08:00:00Z"));

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine) => _engines.Provision(engine).Open([IdentityModule.Module], time: _clock);

    private sealed class MaskProtector : ISigningKeyProtector
    {
        public byte[] Protect(byte[] privateKey) => privateKey.Select(b => (byte)(b ^ 0x5a)).ToArray();
        public byte[] Unprotect(byte[] protectedKey) => Protect(protectedKey);
    }

    [Fact]
    public void The_module_follows_the_conventions() => Assert.Empty(StateConventions.Violations(IdentityModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_client_registers_authenticates_and_is_removed_with_its_refresh_tokens_revoked(string engine)
    {
        var database = Open(engine);
        var clients = new ModuleOidcClients(database);

        var (confidential, secret) = clients.Register("console", ["https://console.example/cb"], confidential: true, ["https://console.example/bye"]);
        _clock.Advance(TimeSpan.FromSeconds(1));   // clients list in the order they were registered
        var (publicClient, none) = clients.Register("spa", ["https://spa.example/cb"], confidential: false);

        Assert.NotNull(secret);
        Assert.Null(none);
        Assert.True(clients.Authenticates(confidential.ClientId, secret));
        Assert.False(clients.Authenticates(confidential.ClientId, "wrong"));
        Assert.False(clients.Authenticates(confidential.ClientId, null));
        Assert.True(clients.Authenticates(publicClient.ClientId, null));
        var found = clients.Find(confidential.ClientId)!;
        Assert.Equal(["https://console.example/cb"], found.RedirectUris);
        Assert.Equal(["https://console.example/bye"], found.PostLogoutRedirectUris);
        Assert.Equal([confidential.ClientId, publicClient.ClientId], clients.List().Select(c => c.ClientId));
        Assert.True(clients.Remove(confidential.ClientId));
        Assert.Null(clients.Find(confidential.ClientId));
        Assert.False(clients.Remove(confidential.ClientId));
        Assert.Throws<ArgumentException>(() => clients.Register("bad", ["not a uri"], false));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Signing_keys_are_kept_protected_rotate_and_a_retired_one_leaves_the_jwks(string engine)
    {
        var database = Open(engine);
        var keys = new ModuleSigningKeys(database, new MaskProtector());

        var first = keys.Current();
        Assert.Equal(first.Kid, keys.Current().Kid);                 // the same key until it is rotated
        _clock.Advance(TimeSpan.FromSeconds(1));
        var second = keys.Rotate();

        Assert.NotEqual(first.Kid, second.Kid);
        Assert.Equal(second.Kid, keys.Current().Kid);                // the newest signs
        Assert.NotNull(keys.Find(first.Kid));                        // the older still verifies
        // what is stored is the protected form: the private key is not readable from the table
        using (var connection = database.OpenConnection())
        {
            using var read = database.Command("identity", connection, "SELECT private_key FROM {table:signing_key} WHERE kid = $kid;");
            read.Bind(database, "kid", first.Kid);
            var stored = Convert.FromBase64String((string)read.ExecuteScalar()!);
            Assert.NotEqual(first.Private.ExportPkcs8PrivateKey(), stored);
        }

        keys.Retire(first.Kid);
        Assert.Null(keys.Find(first.Kid));
        var jwks = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(keys.Jwks())).RootElement.GetProperty("keys");
        Assert.Equal([second.Kid], jwks.EnumerateArray().Select(k => k.GetProperty("kid").GetString()));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_access_token_signs_in_until_it_expires_or_is_revoked_and_only_its_hash_is_kept(string engine)
    {
        var database = Open(engine);
        var users = new IdentityUserStore(database);
        var tokens = new ModuleAccessTokens(database, users);
        var ada = users.CreateUser("Ada", null, UserRole.Admin);

        var made = tokens.Create(ada.Id, "ci", createdBy: ada.Id, lifetime: TimeSpan.FromDays(30));

        Assert.StartsWith("ddk_", made.Secret);
        Assert.Equal(ada.Id, tokens.Verify(ada.Id, made.Secret)!.Id);
        Assert.Null(tokens.Verify(ada.Id, made.Secret + "x"));
        Assert.NotNull(tokens.List(ada.Id).Single().LastUsedAtUtc);          // a token that signs someone in records when
        using (var connection = database.OpenConnection())
        {
            using var read = database.Command("identity", connection, "SELECT secret_hash FROM {table:access_token};");
            Assert.DoesNotContain(made.Secret, (string)read.ExecuteScalar()!);   // the secret is in no row
        }

        Assert.True(tokens.Revoke(made.Record.Id));
        Assert.Null(tokens.Verify(ada.Id, made.Secret));
        Assert.True(tokens.Get(made.Record.Id)!.RevokedAtUtc is not null);      // revoked tokens stay, listed as revoked

        var shortLived = tokens.Create(ada.Id, "short", ada.Id, TimeSpan.FromDays(1));
        _clock.Advance(TimeSpan.FromDays(2));
        Assert.Null(tokens.Verify(ada.Id, shortLived.Secret));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_service_account_signs_in_only_with_its_tokens_and_removing_it_revokes_them(string engine)
    {
        var database = Open(engine);
        var users = new IdentityUserStore(database);
        var tokens = new ModuleAccessTokens(database, users);

        var account = tokens.CreateServiceAccount("nightly-deploy", "runs the deploy", "ada");
        var made = tokens.Create(account.UserId, "main", "ada", noExpiry: true);

        Assert.Equal(account, tokens.FindServiceAccount("Nightly-Deploy"));
        Assert.Equal(account, tokens.ServiceAccountOf(account.UserId));
        Assert.Equal([account], tokens.ServiceAccounts());
        Assert.NotNull(tokens.Verify("nightly-deploy", made.Secret));
        Assert.True(tokens.RemoveServiceAccount("nightly-deploy"));
        Assert.Null(tokens.Verify("nightly-deploy", made.Secret));
        Assert.Null(tokens.FindServiceAccount("nightly-deploy"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_external_group_gives_roles_only_to_a_person_seen_in_it_and_membership_is_replaced_whole(string engine)
    {
        var database = Open(engine);
        var users = new IdentityUserStore(database);
        var directory = new ModuleHomeDirectory(database);
        var ada = users.CreateUser("Ada", null, UserRole.Viewer);
        var bob = users.CreateUser("Bob", null, UserRole.Viewer);
        directory.MapExternalGroup(ExternalSources.Entra, "  Data-Team ", "build-1", ServerRole.Author);

        directory.SetExternalGroups(ada.Id, ExternalSources.Entra, ["data-team", "DATA-TEAM", "other"]);

        Assert.True(directory.RolesOn(ada.Id, "build-1").Roles.HasFlag(ServerRole.Author));
        Assert.Equal(ServerRole.None, directory.RolesOn(bob.Id, "build-1").Roles);
        Assert.Equal([(ExternalSources.Entra, "data-team"), (ExternalSources.Entra, "other")], directory.ExternalGroupsOf(ada.Id));
        directory.SetExternalGroups(ada.Id, ExternalSources.Entra, ["other"]);                       // the person left the group
        Assert.Equal(ServerRole.None, directory.RolesOn(ada.Id, "build-1").Roles);
        Assert.Single(directory.ExternalMappings());
        Assert.True(directory.RemoveExternalMapping(directory.ExternalMappings()[0].Id));
        Assert.Empty(directory.ExternalMappings());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Removing_a_group_removes_its_members_and_its_role_assignments(string engine)
    {
        var database = Open(engine);
        var users = new IdentityUserStore(database);
        var directory = new ModuleHomeDirectory(database);
        var ada = users.CreateUser("Ada", null, UserRole.Viewer);
        var group = directory.CreateGroup("operators");
        directory.AddMember(group.Id, ada.Id);
        directory.Assign(AssignmentSubject.Group(group.Id), "build-1", ServerRole.Operator);

        Assert.True(directory.RemoveGroup(group.Id));

        Assert.Empty(directory.Groups());
        Assert.Empty(directory.Assignments());
        Assert.Equal(ServerRole.None, directory.RolesOn(ada.Id, "build-1").Roles);
    }
}
