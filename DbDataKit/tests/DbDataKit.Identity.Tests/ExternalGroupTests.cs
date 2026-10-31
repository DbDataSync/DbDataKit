using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.IdentityModel.JsonWebTokens;

namespace DbDataKit.Identity.Tests;

/// <summary>
/// Plan 13a U18: a person in a mapped Entra group gets the role through the OpenID <c>groups</c> claim; an unmapped group gives nothing; a removed mapping removes the role at the next sign-in. The upstream is a stand-in for an Entra
/// tenant (a real HTTP server that signs RS256 id tokens with a <c>groups</c> claim of group object ids); the client is the real <see cref="OpenIdClient"/>, and the home's own provider issues the token the console reads.
/// </summary>
public sealed class ExternalGroupTests : IAsyncLifetime
{
    private const string Server = "build-1", Redirect = "https://home.example.invalid/entra-callback", ConsoleRedirect = "https://console.example.invalid/signed-in";
    private const string OperatorsGroup = "7f3c1b0e-aaaa-4b1d-9c11-000000000001", AuthorsGroup = "7f3c1b0e-aaaa-4b1d-9c11-000000000002", OtherGroup = "7f3c1b0e-aaaa-4b1d-9c11-000000000009";

    private readonly HomeFixture _home = new();
    private WebApplication _entra = null!;
    private string _authority = null!, _consoleClient = null!;
    private readonly RSA _rsa = RSA.Create(2048);
    private OpenIdProvider _provider = null!;
    private ExternalSignIns _signIns = null!;

    // what the next token from the stand-in tenant says
    private string _subject = "entra-oid-ada", _email = "ada@clearwater.example.invalid";
    private string[] _groups = [];
    private string _nonce = "", _algorithm = "RS256";
    private RSA _signingKey = null!;
    private HttpClient _http = null!;

    public async ValueTask InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        _authority = $"http://127.0.0.1:{port}/tenant-1/v2.0";

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _entra = builder.Build();
        _entra.MapGet("/tenant-1/v2.0/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = _authority, authorization_endpoint = _authority + "/authorize", token_endpoint = _authority + "/token", jwks_uri = _authority + "/keys",
        }));
        _entra.MapGet("/tenant-1/v2.0/keys", () =>
        {
            var p = _rsa.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "entra-key-1", n = Base64Url.EncodeToString(p.Modulus!), e = Base64Url.EncodeToString(p.Exponent!) } } });
        });
        _entra.MapPost("/tenant-1/v2.0/token", () => Results.Json(new { id_token = IdToken("entra-key-1", _algorithm, _signingKey), access_token = "not-used", token_type = "Bearer" }));
        await _entra.StartAsync();
        _http = new HttpClient();
        _signingKey = _rsa;

        _provider = new OpenIdProvider(_home.Database, _home.Users, _home.Directory, _home.Clients, _home.Keys, "https://home.example.invalid");
        _signIns = new ExternalSignIns(_home.Users, _home.Directory);
        _consoleClient = _home.Clients.Register("Operations console", [ConsoleRedirect], confidential: false).Client.ClientId;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _entra.DisposeAsync();
        _rsa.Dispose();
        _home.Dispose();
    }

    private string IdToken(string kid, string alg, RSA key)
    {
        string B(object o) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(o));
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = _authority, ["aud"] = "home-app", ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), ["sub"] = _subject, ["name"] = "Ada Lovelace", ["email"] = _email, ["nonce"] = _nonce, ["groups"] = _groups,
        };
        var signingInput = B(new { alg, kid, typ = "JWT" }) + "." + B(claims);
        return signingInput + "." + Base64Url.EncodeToString(key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    /// <summary>The home signs a person in through the stand-in tenant: the browser goes out, comes back with a code, the client checks the id token and the home's user for them is found or made and their groups recorded.</summary>
    private async Task<UserRecord> SignInThroughEntra(params string[] groups)
    {
        _groups = groups;
        var client = new OpenIdClient(new OpenIdClientOptions(_authority, "home-app", "secret", Redirect), _http);
        var query = System.Web.HttpUtility.ParseQueryString((await client.BeginAsync(null, default)).Query);
        _nonce = query["nonce"]!;
        var signedIn = await client.CompleteAsync(query["state"], "code-from-entra", null, default);
        return _signIns.SignIn(ExternalSignIns.FromOpenId(ExternalSources.Entra, signedIn.Identity));
    }

    /// <summary>What the console would read: the roles the home's id token carries for the person (through the real authorization code exchange).</summary>
    private Dictionary<string, string[]> RolesInHomeToken(string userId)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var result = _provider.Authorize(new AuthorizeRequest(_consoleClient, ConsoleRedirect, "code", "openid profile", "s", "n", challenge, "S256"), userId);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(result.RedirectTo!).Query)["code"];
        var tokens = _provider.Exchange(new TokenRequest("authorization_code", code, ConsoleRedirect, _consoleClient, null, verifier));
        var token = new JsonWebTokenHandler().ReadJsonWebToken(tokens.IdToken);
        return token.TryGetPayloadValue<JsonElement>("dbdata_roles", out var roles)
            ? roles.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(e => e.GetString()!).ToArray())
            : [];
    }

    [Fact]
    public async Task A_person_in_a_mapped_entra_group_gets_the_role_through_the_groups_claim()
    {
        _home.Directory.MapExternalGroup(ExternalSources.Entra, OperatorsGroup, Server, ServerRole.Operator);
        _home.Directory.MapExternalGroup(ExternalSources.Entra, AuthorsGroup, Server, ServerRole.Author, project: "retail");
        _home.Directory.MapExternalGroupToApp(ExternalSources.Entra, OperatorsGroup, "console-a", ServerRole.Admin);

        var ada = await SignInThroughEntra(OperatorsGroup, AuthorsGroup, OtherGroup);

        var roles = RolesInHomeToken(ada.Id);
        Assert.Contains("operator", roles[$"server:{Server}"]);
        Assert.Contains("author", roles[$"server:{Server}/retail"]);
        Assert.Contains("admin", roles["app:console-a"]);
        // the same through the token a console sends a server
        var held = _home.Directory.RolesOn(ada.Id, Server);
        Assert.True(held.Roles.HasFlag(ServerRole.Operator));
        Assert.True(held.Projects["retail"].HasFlag(ServerRole.Author));
        Assert.Equal(ada.Id, HomeTokens.For(_home.Users, _home.Directory, ada.Id, Server)!.SubjectId);
    }

    [Fact]
    public async Task An_unmapped_group_gives_nothing()
    {
        _home.Directory.MapExternalGroup(ExternalSources.Entra, OperatorsGroup, Server, ServerRole.Operator);

        var bob = await SignInThroughEntra(OtherGroup);

        Assert.Empty(RolesInHomeToken(bob.Id));
        Assert.Equal(ServerRole.None, _home.Directory.RolesOn(bob.Id, Server).Roles);
        Assert.Empty(_home.Directory.AllRoles(bob.Id));
    }

    [Fact]
    public async Task A_removed_mapping_removes_the_role_at_the_next_sign_in()
    {
        _home.Directory.MapExternalGroup(ExternalSources.Entra, OperatorsGroup, Server, ServerRole.Operator);
        var ada = await SignInThroughEntra(OperatorsGroup);
        Assert.Contains("operator", RolesInHomeToken(ada.Id)[$"server:{Server}"]);

        _home.Directory.RemoveExternalMapping(Assert.Single(_home.Directory.ExternalMappings()).Id);
        var again = await SignInThroughEntra(OperatorsGroup);

        Assert.Equal(ada.Id, again.Id);
        Assert.Empty(RolesInHomeToken(ada.Id));
    }

    [Fact]
    public async Task Leaving_the_group_at_the_provider_removes_the_role_at_the_next_sign_in_and_joining_a_group_adds_it()
    {
        _home.Directory.MapExternalGroup(ExternalSources.Entra, OperatorsGroup, Server, ServerRole.Operator);
        _home.Directory.MapExternalGroup(ExternalSources.Entra, AuthorsGroup, Server, ServerRole.Author);
        var ada = await SignInThroughEntra(OperatorsGroup);
        Assert.Equal(["operator"], RolesInHomeToken(ada.Id)[$"server:{Server}"].Where(r => r != "viewer"));

        await SignInThroughEntra(AuthorsGroup);

        var now = RolesInHomeToken(ada.Id)[$"server:{Server}"];
        Assert.Contains("author", now);
        Assert.DoesNotContain("operator", now);
    }

    [Fact]
    public async Task Mapping_again_replaces_and_none_removes_and_the_role_is_added_to_what_the_person_holds_otherwise()
    {
        var ada = await SignInThroughEntra(OperatorsGroup);
        var home = _home.Directory.CreateGroup("approvers");
        _home.Directory.AddMember(home.Id, ada.Id);
        _home.Directory.Assign(AssignmentSubject.Group(home.Id), Server, ServerRole.Approver);

        _home.Directory.MapExternalGroup(ExternalSources.Entra, OperatorsGroup, Server, ServerRole.Admin);
        _home.Directory.MapExternalGroup(ExternalSources.Entra, OperatorsGroup, Server, ServerRole.Operator);   // replaces
        var held = _home.Directory.RolesOn(ada.Id, Server).Roles;
        Assert.True(held.HasFlag(ServerRole.Operator));
        Assert.True(held.HasFlag(ServerRole.Approver));      // the home group's role stays
        Assert.False(held.HasFlag(ServerRole.Admin));
        Assert.Single(_home.Directory.ExternalMappings());

        _home.Directory.MapExternalGroup(ExternalSources.Entra, OperatorsGroup, Server, ServerRole.None);
        Assert.Empty(_home.Directory.ExternalMappings());
        Assert.False(_home.Directory.RolesOn(ada.Id, Server).Roles.HasFlag(ServerRole.Operator));
        Assert.True(_home.Directory.RolesOn(ada.Id, Server).Roles.HasFlag(ServerRole.Approver));
    }

    [Fact]
    public async Task A_group_matches_whatever_its_case_and_only_through_its_own_source()
    {
        var ada = await SignInThroughEntra(OperatorsGroup.ToUpperInvariant());
        _home.Directory.MapExternalGroup(ExternalSources.Entra, " " + OperatorsGroup.ToLowerInvariant() + " ", Server, ServerRole.Operator);
        _home.Directory.MapExternalGroup(ExternalSources.Windows, OperatorsGroup, "build-2", ServerRole.Admin);

        Assert.True(_home.Directory.RolesOn(ada.Id, Server).Roles.HasFlag(ServerRole.Operator));
        Assert.Equal(ServerRole.None, _home.Directory.RolesOn(ada.Id, "build-2").Roles);     // the same value from Windows would be another group
    }

    [Fact]
    public void Windows_groups_are_recorded_and_mapped_apart_from_entra_ones_and_one_sources_sign_in_leaves_the_others_alone()
    {
        _home.Directory.MapExternalGroup(ExternalSources.Windows, @"CLEARWATER\Build Operators", Server, ServerRole.Operator);
        var entra = new ExternalIdentity(ExternalSources.Entra, "entra-oid-ada", "Ada Lovelace", "ada@clearwater.example.invalid", true, [OtherGroup]);
        var ada = _signIns.SignIn(entra);
        Assert.False(_home.Directory.RolesOn(ada.Id, Server).Roles.HasFlag(ServerRole.Operator));

        var viaWindows = _signIns.SignIn(new ExternalIdentity(ExternalSources.Windows, "S-1-5-21-1-2-3-1001", "Ada Lovelace", "ada@clearwater.example.invalid", true, [@"clearwater\build operators"]));
        Assert.Equal(ada.Id, viaWindows.Id);      // the same person: the verified address joined the Windows sign-in to the Entra account
        Assert.True(_home.Directory.RolesOn(ada.Id, Server).Roles.HasFlag(ServerRole.Operator));

        _signIns.SignIn(entra);                   // an Entra sign-in does not forget the Windows groups
        Assert.True(_home.Directory.RolesOn(ada.Id, Server).Roles.HasFlag(ServerRole.Operator));

        _signIns.SignIn(new ExternalIdentity(ExternalSources.Windows, "S-1-5-21-1-2-3-1001", "Ada Lovelace", null, false, []));
        Assert.False(_home.Directory.RolesOn(ada.Id, Server).Roles.HasFlag(ServerRole.Operator));      // the next Windows sign-in without the group
        Assert.Contains((ExternalSources.Entra, OtherGroup), _home.Directory.ExternalGroupsOf(ada.Id));
    }

    [Fact]
    public async Task One_person_is_one_account_a_verified_address_finds_the_local_user_and_an_unverified_one_does_not()
    {
        var local = _home.Users.CreateUser("Ada (local)", "Ada@Clearwater.Example.Invalid", UserRole.Admin, emailVerified: true);
        _home.Directory.MapExternalGroup(ExternalSources.Entra, OperatorsGroup, Server, ServerRole.Operator);

        // Entra sends no email_verified, so the address links nothing: a new account, not the local one
        var viaEntra = await SignInThroughEntra(OperatorsGroup);
        Assert.NotEqual(local.Id, viaEntra.Id);
        Assert.Equal(ServerRole.None, _home.Directory.RolesOn(local.Id, Server).Roles);

        // a host that trusts its tenant's addresses says so
        var trusted = _signIns.SignIn(new ExternalIdentity(ExternalSources.Entra, "entra-oid-other", "Ada", "ADA@clearwater.example.invalid", true, [OperatorsGroup]));
        Assert.Equal(local.Id, trusted.Id);
        Assert.Equal(local.Id, _signIns.SignIn(new ExternalIdentity(ExternalSources.Entra, "entra-oid-other", "Ada", null, false, [OperatorsGroup])).Id);   // the credential finds it from then on
        Assert.True(_home.Directory.RolesOn(local.Id, Server).Roles.HasFlag(ServerRole.Operator));
    }

    [Fact]
    public async Task A_token_signed_by_a_key_the_tenant_does_not_publish_or_with_the_wrong_algorithm_is_refused()
    {
        using var forger = RSA.Create(2048);
        _signingKey = forger;
        var forged = await Assert.ThrowsAsync<OpenIdClientException>(() => SignInThroughEntra(OperatorsGroup));
        Assert.Contains("signature", forged.Message);

        // the algorithm in the header must be the key's: ES256 over an RSA key is refused, not tried
        _signingKey = _rsa;
        _algorithm = "ES256";
        var confused = await Assert.ThrowsAsync<OpenIdClientException>(() => SignInThroughEntra(OperatorsGroup));
        Assert.Contains("algorithm", confused.Message);
        Assert.Empty(_home.Users.List());     // nobody was signed in or made
    }
}
