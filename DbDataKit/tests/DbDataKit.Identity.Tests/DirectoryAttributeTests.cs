using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity.Tests;

/// <summary>
/// Plan 22a GP04, D4: an external group maps to a directory attribute and a value; a person's attributes are multi-valued, follow their last sign-in, and travel in the home's id token and userinfo and in the console's token to a server
/// (the claim <c>attrs</c>, left out when there are none). Every engine; the claim shape is in the Identity README.
/// </summary>
public sealed class DirectoryAttributeTests : IDisposable
{
    private const string Server = "build-1", Redirect = "https://console.example.invalid/signed-in";
    private const string EastGroup = "7f3c1b0e-bbbb-4b1d-9c11-000000000001", WestGroup = "7f3c1b0e-bbbb-4b1d-9c11-000000000002", SalesGroup = "7f3c1b0e-bbbb-4b1d-9c11-000000000003", OtherGroup = "7f3c1b0e-bbbb-4b1d-9c11-000000000009";

    private readonly StateEngines _engines = new();
    public void Dispose() => _engines.Dispose();

    private sealed class MaskProtector : ISigningKeyProtector
    {
        public byte[] Protect(byte[] privateKey) => privateKey.Select(b => (byte)(b ^ 0x5a)).ToArray();
        public byte[] Unprotect(byte[] protectedKey) => Protect(protectedKey);
    }

    private sealed record Home(StateDatabase Database, IdentityUserStore Users, HomeDirectory Directory, OpenIdProvider Provider, ExternalSignIns SignIns, string ClientId)
    {
        public UserRecord SignIn(string subject, params string[] groups) =>
            SignIns.SignIn(new ExternalIdentity(ExternalSources.Entra, subject, subject, null, false, groups));

        /// <summary>The claims of the id token the home issues for the person, through the real authorization code exchange.</summary>
        public JsonElement IdToken(string userId, string scope = "openid profile")
        {
            var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
            var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var result = Provider.Authorize(new AuthorizeRequest(ClientId, Redirect, "code", scope, "s", "n", challenge, "S256"), userId);
            var code = System.Web.HttpUtility.ParseQueryString(new Uri(result.RedirectTo!).Query)["code"];
            var tokens = Provider.Exchange(new TokenRequest("authorization_code", code, Redirect, ClientId, null, verifier));
            return JsonSerializer.Deserialize<JsonElement>(Base64Url.DecodeFromChars(tokens.IdToken.Split('.')[1]));
        }
    }

    private Home Open(string engine)
    {
        var database = _engines.Provision(engine).Open([IdentityModule.Module]);
        var users = new IdentityUserStore(database);
        var directory = new HomeDirectory(database);
        var clients = new OidcClients(database);
        var provider = new OpenIdProvider(database, users, directory, clients, new SigningKeys(database, new MaskProtector()), "https://home.example.invalid");
        return new Home(database, users, directory, provider, new ExternalSignIns(users, directory), clients.Register("Operations console", [Redirect], confidential: false).Client.ClientId);
    }

    private static string[] Values(JsonElement attrs, string name) => [.. attrs.GetProperty(name).EnumerateArray().Select(e => e.GetString()!)];

    [Fact]
    public void The_migration_that_adds_the_mapping_table_is_compatible_with_the_previous_version() =>
        Assert.Equal(1, IdentityModule.Module.CompatibleWithPrevious);

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_group_maps_to_an_attribute_and_a_person_in_it_holds_the_value_in_the_token(string engine)
    {
        var home = Open(engine);
        home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, EastGroup, "region", "east");
        var ada = home.SignIn("oid-ada", EastGroup, OtherGroup);

        Assert.Equal(["east"], home.Directory.AttributesOf(ada.Id)["region"]);
        var claims = home.IdToken(ada.Id);
        Assert.Equal(["east"], Values(claims.GetProperty("attrs"), "region"));
        Assert.Equal(JsonValueKind.Object, claims.GetProperty("dbdata_roles").ValueKind);     // what a client that ignores attrs reads is still there
        Assert.Equal(ada.Id, claims.GetProperty("sub").GetString());
        Assert.Equal(ada.Id, home.Provider.UserInfo(AccessTokenOf(home, ada.Id))!["sub"]);
        Assert.Equal(["east"], ((IReadOnlyDictionary<string, IReadOnlyList<string>>)home.Provider.UserInfo(AccessTokenOf(home, ada.Id))!["attrs"]!)["region"]);
    }

    private static string AccessTokenOf(Home home, string userId)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var result = home.Provider.Authorize(new AuthorizeRequest(home.ClientId, Redirect, "code", "openid profile", "s", "n", challenge, "S256"), userId);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(result.RedirectTo!).Query)["code"];
        return home.Provider.Exchange(new TokenRequest("authorization_code", code, Redirect, home.ClientId, null, verifier)).AccessToken;
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_person_in_two_groups_gets_two_values_and_each_attribute_has_its_own_list(string engine)
    {
        var home = Open(engine);
        home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, EastGroup, "region", "east");
        home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, WestGroup, "region", "west");
        home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, WestGroup, "department", "sales");
        home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, SalesGroup, "department", "sales");     // the same value from a second group is one value
        var ada = home.SignIn("oid-ada", WestGroup, EastGroup, SalesGroup);

        var attrs = home.IdToken(ada.Id).GetProperty("attrs");

        Assert.Equal(["east", "west"], Values(attrs, "region"));
        Assert.Equal(["sales"], Values(attrs, "department"));
        // and as a caller of the serving layer sees them
        var request = HomeTokens.For(home.Users, home.Directory, ada.Id, Server)!;
        Assert.Equal(["east", "west"], request.Attributes!["region"]);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_person_with_no_mapped_group_has_no_claim_and_a_token_a_client_reads_as_before(string engine)
    {
        var home = Open(engine);
        home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, EastGroup, "region", "east");
        var bob = home.SignIn("oid-bob", OtherGroup);

        var claims = home.IdToken(bob.Id);

        Assert.False(claims.TryGetProperty("attrs", out _));
        Assert.Empty(home.Directory.AttributesOf(bob.Id));
        Assert.Empty(HomeTokens.For(home.Users, home.Directory, bob.Id, Server)!.Attributes!);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Leaving_a_group_or_removing_the_mapping_removes_the_value(string engine)
    {
        var home = Open(engine);
        home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, EastGroup, "region", "east");
        var west = home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, WestGroup, "region", "west");
        var ada = home.SignIn("oid-ada", EastGroup, WestGroup);
        Assert.Equal(["east", "west"], home.Directory.AttributesOf(ada.Id)["region"]);

        home.SignIn("oid-ada", WestGroup);                           // left the east group at the provider
        Assert.Equal(["west"], home.Directory.AttributesOf(ada.Id)["region"]);

        Assert.True(home.Directory.RemoveAttributeMapping(west.Id));  // at once, no sign-in needed
        Assert.Empty(home.Directory.AttributesOf(ada.Id));
        Assert.False(home.Directory.RemoveAttributeMapping(west.Id));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Mapping_the_same_again_changes_nothing_and_a_group_matches_whatever_its_case_and_only_its_own_source(string engine)
    {
        var home = Open(engine);
        var first = home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, EastGroup, "region", "east");
        var again = home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, EastGroup.ToUpperInvariant(), "region", "east");
        Assert.Equal(first.Id, again.Id);
        Assert.Single(home.Directory.AttributeMappings());

        var ada = home.SignIn("oid-ada", EastGroup.ToUpperInvariant());
        Assert.Equal(["east"], home.Directory.AttributesOf(ada.Id)["region"]);

        var windows = home.SignIns.SignIn(new ExternalIdentity(ExternalSources.Windows, "S-1-5-21-1", "Win", null, false, [EastGroup]));
        Assert.Empty(home.Directory.AttributesOf(windows.Id));        // the mapping is for Entra
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_mapping_that_cannot_be_made_is_refused_naming_why(string engine)
    {
        var home = Open(engine);
        Assert.Throws<ArgumentException>(() => home.Directory.MapExternalGroupToAttribute("ldap", EastGroup, "region", "east"));
        Assert.Throws<ArgumentException>(() => home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, " ", "region", "east"));
        Assert.Throws<ArgumentException>(() => home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, EastGroup, "Region", "east"));
        Assert.Throws<ArgumentException>(() => home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, EastGroup, "1region", "east"));
        Assert.Throws<ArgumentException>(() => home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, EastGroup, "region", " "));
        Assert.Throws<ArgumentException>(() => home.Directory.MapExternalGroupToAttribute(ExternalSources.Entra, EastGroup, "region", new string('x', DirectoryAttributes.MaxValueLength + 1)));
        Assert.Empty(home.Directory.AttributeMappings());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_admin_api_lists_adds_and_removes_attribute_mappings(string engine)
    {
        var home = Open(engine);
        var api = new IdentityAdminApi(home.Database, "https://home.example.invalid");
        JsonNode Body(string group, string attribute, string value) => new JsonObject { ["source"] = "entra", ["group"] = group, ["attribute"] = attribute, ["value"] = value };

        Assert.Equal(403, api.Handle("POST", "attribute-mappings", Body(EastGroup, "region", "east"), isAdmin: false, "x").Status);
        Assert.Equal(200, api.Handle("POST", "attribute-mappings", Body(EastGroup, "region", "east"), true, "x").Status);
        Assert.Equal(200, api.Handle("POST", "attribute-mappings", Body(EastGroup, "region", "east"), true, "x").Status);     // again: still one
        var listed = api.Handle("POST", "attribute-mappings", Body(WestGroup, "region", "west"), true, "x");
        var rows = listed.Body!["attribute_mappings"]!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, api.Handle("GET", "attribute-mappings", null, true, "x").Body!["attribute_mappings"]!.AsArray().Count);
        Assert.Equal("region", rows[0]!["attribute"]!.GetValue<string>());

        Assert.Equal(400, api.Handle("POST", "attribute-mappings", Body(EastGroup, "Not A Name", "east"), true, "x").Status);
        Assert.Equal(400, api.Handle("POST", "attribute-mappings", new JsonObject { ["source"] = "entra", ["group"] = EastGroup }, true, "x").Status);

        var id = rows[0]!["id"]!.GetValue<string>();
        Assert.Equal(204, api.Handle("DELETE", $"attribute-mappings/{id}", null, true, "x").Status);
        Assert.Equal(404, api.Handle("DELETE", $"attribute-mappings/{id}", null, true, "x").Status);
        Assert.Single(home.Directory.AttributeMappings());
    }
}
