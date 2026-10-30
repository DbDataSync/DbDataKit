using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DbDataKit.Identity.Tests;

/// <summary>
/// U13's test: a person signs in through the provider (a real HTTP server, the authorization code flow with PKCE, a real OpenID Connect library validating the id token against the published keys), gets the home's user id
/// and roles, and the console's tokens (T01) carry them to a server that validates them.
/// </summary>
public sealed class OpenIdFlowTests : IAsyncLifetime
{
    private const string Redirect = "https://console.example.invalid/signed-in", Server = "build-1";

    private readonly HomeFixture _home = new();
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private OpenIdProvider _provider = null!;
    private string _issuer = null!, _clientId = null!;
    private UserRecord _ada = null!;

    public async ValueTask InitializeAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        _issuer = $"http://127.0.0.1:{port}";

        _provider = new OpenIdProvider(_home.Database, _home.Users, _home.Directory, _home.Clients, _home.Keys, _issuer);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(_issuer);
        _app = builder.Build();
        // The host's sign-in is a header here; in a console it is the session cookie.
        _app.MapOpenIdProvider(_provider, _home.Keys, new OpenIdHostOptions(c => c.Request.Headers["X-Signed-In-User"].FirstOrDefault()));
        await _app.StartAsync();
        _http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri(_issuer) };

        _ada = _home.Users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Viewer);
        var ops = _home.Directory.CreateGroup("operators");
        _home.Directory.AddMember(ops.Id, _ada.Id);
        _home.Directory.Assign(AssignmentSubject.Group(ops.Id), Server, ServerRole.Operator);
        _home.Directory.Assign(AssignmentSubject.User(_ada.Id), Server, ServerRole.Author, project: "retail");
        _clientId = _home.Clients.Register("Operations console", [Redirect], confidential: false).Client.ClientId;
    }

    public async ValueTask DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _home.Dispose();
    }

    private static (string Verifier, string Challenge) Pkce()
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        return (verifier, Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).Replace('+', '-').Replace('/', '_').TrimEnd('='));
    }

    private string AuthorizeUri(string challenge, string method = "S256", string redirect = Redirect, string scope = "openid profile email") =>
        $"/identity/authorize?response_type=code&client_id={_clientId}&redirect_uri={Uri.EscapeDataString(redirect)}&scope={Uri.EscapeDataString(scope)}&state=xyz&nonce=n-1&code_challenge={challenge}&code_challenge_method={method}";

    private async Task<HttpResponseMessage> AuthorizeAs(string? userId, string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (userId is not null) request.Headers.Add("X-Signed-In-User", userId);
        return await _http.SendAsync(request);
    }

    private static string CodeOf(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!;
        Assert.StartsWith(Redirect, location.ToString());
        var query = System.Web.HttpUtility.ParseQueryString(location.Query);
        Assert.Equal("xyz", query["state"]);
        return query["code"]!;
    }

    private Task<HttpResponseMessage> Token(string code, string verifier, string? redirect = Redirect, string? client = null) =>
        _http.PostAsync("/identity/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirect!, ["client_id"] = client ?? _clientId, ["code_verifier"] = verifier,
        }));

    private async Task<string> SignIn()
    {
        var (verifier, challenge) = Pkce();
        var code = CodeOf(await AuthorizeAs(_ada.Id, AuthorizeUri(challenge)));
        var token = await Token(code, verifier);
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        return (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id_token").GetString()!;
    }

    [Fact]
    public async Task Signing_in_through_the_provider_gives_the_homes_user_id_and_roles()
    {
        var idToken = await SignIn();

        // Validated the way any client library would: the issuer's published keys, the issuer, the audience, the nonce.
        var jwks = await _http.GetStringAsync("/identity/jwks");
        var discovery = await _http.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        Assert.Equal(_issuer, discovery.GetProperty("issuer").GetString());
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = _issuer, ValidAudience = _clientId, IssuerSigningKeys = new JsonWebKeySet(jwks).GetSigningKeys(), RequireSignedTokens = true,
        });
        Assert.True(validation.IsValid, validation.Exception?.Message);

        var token = (JsonWebToken)validation.SecurityToken;
        Assert.Equal(_ada.Id, token.Subject);
        Assert.Equal("Ada Lovelace", token.GetClaim("name").Value);
        Assert.Equal("n-1", token.GetClaim("nonce").Value);
        Assert.Contains("operators", token.GetPayloadValue<string[]>("groups"));
        var roles = token.GetPayloadValue<JsonElement>("dbdata_roles");
        Assert.Contains("operator", roles.GetProperty($"server:{Server}").EnumerateArray().Select(e => e.GetString()));
        Assert.Contains("author", roles.GetProperty($"server:{Server}/retail").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task The_consoles_token_carries_the_homes_user_id_and_roles_to_the_server()
    {
        await SignIn();
        var consoleKey = ConsoleSigningKey.Generate();
        var issuer = new ConsoleTokenIssuer("console-a", consoleKey);
        var token = HomeTokens.Issue(issuer, _home.Users, _home.Directory, _ada.Id, Server)!;

        var validator = new ConsoleTokenValidator(Server, new Consoles(new PairedClient("console-a", "Operations", consoleKey.PublicKey, ServerRole.Admin | ServerRole.Author | ServerRole.Approver)), new MemoryReplayCache());
        var result = validator.Validate(token);

        Assert.NotNull(result.Token);
        Assert.Equal(_ada.Id, result.Token!.SubjectId);
        Assert.True(ServerRoles.Allows(result.Token.Roles, ServerRole.Operator));
        Assert.False(ServerRoles.Allows(result.Token.Roles, ServerRole.Admin));
        Assert.True(ServerRoles.Allows(result.Token.Projects["retail"], ServerRole.Author));
        // A console whose ceiling is lower still limits what the home gave.
        var viewerConsole = new ConsoleTokenValidator(Server, new Consoles(new PairedClient("console-a", "Operations", consoleKey.PublicKey, ServerRole.Viewer)), new MemoryReplayCache());
        Assert.Null(viewerConsole.Validate(HomeTokens.Issue(issuer, _home.Users, _home.Directory, _ada.Id, Server)).Token);
    }

    [Fact]
    public async Task Someone_who_is_not_signed_in_is_sent_to_sign_in_and_nothing_is_issued()
    {
        var response = await AuthorizeAs(null, AuthorizeUri(Pkce().Challenge));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith("/sign-in?returnUrl=", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task A_code_works_once()
    {
        var (verifier, challenge) = Pkce();
        var code = CodeOf(await AuthorizeAs(_ada.Id, AuthorizeUri(challenge)));
        Assert.Equal(HttpStatusCode.OK, (await Token(code, verifier)).StatusCode);

        var second = await Token(code, verifier);
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);
        Assert.Equal("invalid_grant", (await second.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task A_code_without_its_verifier_is_refused_and_is_spent()
    {
        var (verifier, challenge) = Pkce();
        var code = CodeOf(await AuthorizeAs(_ada.Id, AuthorizeUri(challenge)));

        Assert.Equal(HttpStatusCode.BadRequest, (await Token(code, Pkce().Verifier)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Token(code, verifier)).StatusCode);   // whoever held the code once cannot retry it
    }

    [Fact]
    public async Task A_code_is_for_the_redirect_uri_and_client_it_was_made_for()
    {
        var (verifier, challenge) = Pkce();
        var code = CodeOf(await AuthorizeAs(_ada.Id, AuthorizeUri(challenge)));
        var other = _home.Clients.Register("Another", ["https://other.example.invalid/cb"], confidential: false).Client.ClientId;
        Assert.Equal(HttpStatusCode.BadRequest, (await Token(code, verifier, client: other)).StatusCode);

        var code2 = CodeOf(await AuthorizeAs(_ada.Id, AuthorizeUri(challenge)));
        Assert.Equal(HttpStatusCode.BadRequest, (await Token(code2, verifier, redirect: "https://evil.example.invalid/")).StatusCode);
    }

    [Fact]
    public async Task An_unregistered_redirect_uri_gets_a_message_and_never_a_redirect()
    {
        var response = await AuthorizeAs(_ada.Id, AuthorizeUri(Pkce().Challenge, redirect: "https://evil.example.invalid/steal"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("")]
    public async Task PKCE_with_S256_is_required(string method)
    {
        var response = await AuthorizeAs(_ada.Id, AuthorizeUri(Pkce().Challenge, method));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("error=invalid_request", response.Headers.Location!.Query);
    }

    [Fact]
    public async Task A_disabled_user_is_refused_even_with_a_code_made_before()
    {
        var (verifier, challenge) = Pkce();
        var code = CodeOf(await AuthorizeAs(_ada.Id, AuthorizeUri(challenge)));
        _home.Users.SetEnabled(_ada.Id, false);

        Assert.Equal(HttpStatusCode.BadRequest, (await Token(code, verifier)).StatusCode);
    }

    [Fact]
    public async Task The_id_token_vouches_for_an_address_only_when_an_administrator_entered_it()
    {
        // plan 13a U16: Ada's address was typed (no key), so a client must not link accounts on it; Grace's was entered by an administrator
        async Task<JsonElement> Claims(string userId)
        {
            var (verifier, challenge) = Pkce();
            var token = await Token(CodeOf(await AuthorizeAs(userId, AuthorizeUri(challenge))), verifier);
            var idToken = (await token.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id_token").GetString()!;
            return JsonDocument.Parse(Base64UrlEncoder.Decode(idToken.Split('.')[1])).RootElement;
        }

        var typed = await Claims(_ada.Id);
        Assert.Equal("ada@example.invalid", typed.GetProperty("email").GetString());
        Assert.False(typed.GetProperty("email_verified").GetBoolean());

        var grace = _home.Users.CreateUser("Grace", "Grace@Example.com", UserRole.Viewer, emailVerified: true);
        Assert.True((await Claims(grace.Id)).GetProperty("email_verified").GetBoolean());
    }

    [Fact]
    public async Task Userinfo_answers_for_the_access_token_and_for_nothing_else()
    {
        var (verifier, challenge) = Pkce();
        var response = await Token(CodeOf(await AuthorizeAs(_ada.Id, AuthorizeUri(challenge))), verifier);
        var access = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("access_token").GetString()!;

        var info = new HttpRequestMessage(HttpMethod.Get, "/identity/userinfo");
        info.Headers.Authorization = new("Bearer", access);
        var ok = await _http.SendAsync(info);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(_ada.Id, (await ok.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sub").GetString());

        var forged = new HttpRequestMessage(HttpMethod.Get, "/identity/userinfo");
        forged.Headers.Authorization = new("Bearer", access[..^4] + "AAAA");
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(forged)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/identity/userinfo")).StatusCode);
        // An id token is not an access token.
        var idAsAccess = new HttpRequestMessage(HttpMethod.Get, "/identity/userinfo");
        idAsAccess.Headers.Authorization = new("Bearer", await SignIn());
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.SendAsync(idAsAccess)).StatusCode);
    }

    [Fact]
    public async Task A_rotated_key_signs_new_tokens_and_the_old_one_still_verifies_until_retired()
    {
        var before = await SignIn();
        var old = _home.Keys.Current().Kid;
        var fresh = _home.Keys.Rotate().Kid;
        Assert.NotEqual(old, fresh);
        Assert.Equal(fresh, new JsonWebToken(await SignIn()).Kid);
        Assert.Equal(old, new JsonWebToken(before).Kid);
        var kids = (await _http.GetFromJsonAsync<JsonElement>("/identity/jwks")).GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToList();
        Assert.Contains(old, kids);
        Assert.Contains(fresh, kids);

        _home.Keys.Retire(old);
        var after = (await _http.GetFromJsonAsync<JsonElement>("/identity/jwks")).GetProperty("keys").EnumerateArray().Select(k => k.GetProperty("kid").GetString()).ToList();
        Assert.DoesNotContain(old, after);
    }

    private sealed class Consoles(params PairedClient[] list) : IPairedClients
    {
        public PairedClient? Find(string id) => list.FirstOrDefault(c => c.Id == id);
    }
}
