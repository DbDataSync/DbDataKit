using System.Security.Cryptography;
using System.Text;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataKit.Identity.Tests;

/// <summary>
/// 13a U19: an app instance is a client of the home, and its access token has the instance as its audience and the approved manifest's scope narrowed by the person's roles. The tests sign people in through the
/// provider (the code and the token exchange, as a client does) and check the tokens with the validator the serving layer's modules use.
/// </summary>
public sealed class AppTokenTests : IDisposable
{
    private const string Redirect = "https://apps.example.invalid/apps/acme/signed-in";

    private readonly HomeFixture _home = new();
    private readonly Grants _grants = new();
    private readonly OpenIdProvider _provider;
    private readonly AppTokenValidator _validator;
    private readonly UserRecord _ada, _bob, _cy;

    public AppTokenTests()
    {
        _provider = new OpenIdProvider(_home.Database, _home.Users, _home.Directory, _home.Clients, _home.Keys, "https://home.example.invalid", apps: _grants);
        _validator = new AppTokenValidator(_provider);
        _grants.Scopes["acme"] = [TokenScope.Entry("sales", "order", write: true), TokenScope.Entry("sales", "customer", write: false)];
        _grants.Scopes["globex"] = [TokenScope.Entry("sales", "order", write: true)];
        _ada = _home.Users.CreateUser("Ada", "ada@example.invalid", UserRole.Viewer);   // may write
        _bob = _home.Users.CreateUser("Bob", "bob@example.invalid", UserRole.Viewer);   // may only read
        _cy = _home.Users.CreateUser("Cy", "cy@example.invalid", UserRole.Viewer);      // no role on the app
        foreach (var app in new[] { "acme", "globex" })
        {
            _home.Directory.AssignApp(AssignmentSubject.User(_ada.Id), app, ServerRole.Author);
            _home.Directory.AssignApp(AssignmentSubject.User(_bob.Id), app, ServerRole.Viewer);
        }
    }

    public void Dispose() => _home.Dispose();

    private sealed class Grants : IAppGrants
    {
        public Dictionary<string, IReadOnlyList<string>> Scopes { get; } = [];

        public IReadOnlyList<string>? ScopeOf(string instance) => Scopes.GetValueOrDefault(instance);
    }

    private (string Access, string? Refresh) SignIn(OidcClient client, UserRecord who)
    {
        var verifier = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        var result = _provider.Authorize(new AuthorizeRequest(client.ClientId, Redirect, "code", "openid offline_access", "s", "n", challenge, "S256"), who.Id);
        var code = System.Web.HttpUtility.ParseQueryString(new Uri(result.RedirectTo!).Query)["code"] ?? throw new InvalidOperationException(result.RedirectTo);
        var tokens = _provider.Exchange(new TokenRequest("authorization_code", code, Redirect, client.ClientId, null, verifier));
        return (tokens.AccessToken, tokens.RefreshToken);
    }

    private OidcClient App(string instance) => _home.Clients.RegisterApp(instance, [Redirect]);

    [Fact]
    public void An_instances_token_names_the_instance_and_the_scope_of_its_manifest()
    {
        var (access, _) = SignIn(App("acme"), _ada);

        var result = _validator.Validate(access, "acme");
        Assert.True(result.Ok);
        var token = result.Token!;
        Assert.Equal("acme", token.Instance);
        Assert.Equal(_ada.Id, token.SubjectId);
        Assert.Contains("author", token.Roles);
        Assert.True(token.Allows("sales", "order", write: true));
        Assert.True(token.Allows("sales", "order", write: false));
        Assert.True(token.Allows("sales", "customer", write: false));
        // the audience in the token itself is the instance, not the home
        Assert.Equal("app:acme", _provider.Verify(access, "at+jwt")!["aud"]);
    }

    [Fact]
    public void Instance_B_refuses_instance_As_token()
    {
        var (access, _) = SignIn(App("acme"), _ada);
        App("globex");

        var result = _validator.Validate(access, "globex");
        Assert.False(result.Ok);
        Assert.Equal(AppTokenFailure.WrongAudience, result.Failure);
    }

    [Fact]
    public void A_model_or_an_operation_the_manifest_does_not_name_is_refused()
    {
        var token = _validator.Validate(SignIn(App("acme"), _ada).Access, "acme").Token!;

        Assert.False(token.Allows("sales", "invoice", write: false));       // an entity the manifest does not name
        Assert.False(token.Allows("hr", "order", write: false));            // a model it does not name
        Assert.False(token.Allows("sales", "customer", write: true));       // an operation it does not grant on a named entity
    }

    [Fact]
    public void The_manifest_never_adds_a_right_the_person_lacks()
    {
        var acme = App("acme");

        var reader = _validator.Validate(SignIn(acme, _bob).Access, "acme").Token!;
        Assert.True(reader.Allows("sales", "order", write: false));
        Assert.False(reader.Allows("sales", "order", write: true));         // the manifest grants write, Bob may only read
        Assert.DoesNotContain(reader.Scope, e => e.EndsWith(":write", StringComparison.Ordinal));

        var nobody = _validator.Validate(SignIn(acme, _cy).Access, "acme").Token!;
        Assert.Empty(nobody.Scope);
        Assert.False(nobody.Allows("sales", "order", write: false));
    }

    [Fact]
    public void The_consoles_and_other_clients_tokens_are_unchanged_and_are_not_an_apps()
    {
        var other = _home.Clients.Register("A console", [Redirect], confidential: false).Client;
        var (access, _) = SignIn(other, _ada);

        var claims = _provider.Verify(access, "at+jwt")!;
        Assert.Equal(_provider.Issuer, claims["aud"]);                       // the home's own audience, as before
        Assert.False(claims.ContainsKey("dbdata_scope"));
        Assert.NotNull(_provider.UserInfo(access));
        Assert.Equal(AppTokenFailure.WrongAudience, _validator.Validate(access, "acme").Failure);
        // and an app's token is not good for the home's userinfo
        Assert.Null(_provider.UserInfo(SignIn(App("acme"), _ada).Access));
    }

    [Fact]
    public void A_refresh_reads_the_manifest_and_the_roles_again()
    {
        var acme = App("acme");
        var (_, refresh) = SignIn(acme, _ada);
        _grants.Scopes["acme"] = [TokenScope.Entry("sales", "order", write: false)];            // a new approved version asks for less

        var next = _provider.Exchange(new TokenRequest("refresh_token", null, null, acme.ClientId, null, null, refresh));
        var token = _validator.Validate(next.AccessToken, "acme").Token!;

        Assert.Equal(["sales/order:read"], token.Scope);
    }

    [Fact]
    public void An_instance_with_no_approved_version_gets_no_token()
    {
        var acme = App("acme");
        _grants.Scopes.Remove("acme");

        var result = _provider.Authorize(new AuthorizeRequest(acme.ClientId, Redirect, "code", "openid", "s", "n", new string('a', 43), "S256"), _ada.Id);
        Assert.Contains("error=access_denied", result.RedirectTo);
    }

    [Fact]
    public void An_app_registers_once_and_removing_its_client_removes_the_registration()
    {
        var acme = App("acme");
        Assert.Equal("acme", _home.Clients.Find(acme.ClientId)!.App);
        Assert.Equal(acme.ClientId, _home.Clients.AppClientOf("acme")!.ClientId);
        Assert.Throws<ArgumentException>(() => _home.Clients.RegisterApp("acme", [Redirect]));
        Assert.Throws<ArgumentException>(() => _home.Clients.RegisterApp("a:b", [Redirect]));

        Assert.True(_home.Clients.Remove(acme.ClientId));
        Assert.Null(_home.Clients.AppClientOf("acme"));
        Assert.NotNull(_home.Clients.RegisterApp("acme", [Redirect]));
    }

    [Fact]
    public void Without_a_resolver_an_app_client_gets_nothing()
    {
        var bare = new OpenIdProvider(_home.Database, _home.Users, _home.Directory, _home.Clients, _home.Keys, "https://home.example.invalid");
        var acme = App("acme");
        var result = bare.Authorize(new AuthorizeRequest(acme.ClientId, Redirect, "code", "openid", "s", "n", new string('a', 43), "S256"), _ada.Id);
        Assert.Contains("error=access_denied", result.RedirectTo);
    }

    private sealed class JwksHandler(Func<object> body) : HttpMessageHandler
    {
        public int Asked { get; private set; }

        public string? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Asked++;
            LastUri = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = System.Net.Http.Json.JsonContent.Create(body()) });
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public async Task A_host_that_does_not_run_the_home_validates_its_tokens_from_the_keys_the_home_publishes()
    {
        var (access, _) = SignIn(App("acme"), _ada);
        App("globex");
        var handler = new JwksHandler(() => _home.Keys.Jwks());
        var verifier = new JwksTokenVerifier("https://home.example.invalid/", new HttpClient(handler));
        var validator = new AppTokenValidator(verifier);

        // the host has no keys until it asks; then it reads what the home itself reads
        Assert.False(validator.Validate(access, "acme").Ok);
        await verifier.PrepareAsync(access, default);
        var result = validator.Validate(access, "acme");
        Assert.True(result.Ok);
        Assert.Equal((_ada.Id, "acme"), (result.Token!.SubjectId, result.Token.Instance));
        Assert.True(result.Token.Allows("sales", "order", write: true));
        Assert.Equal(_provider.Verify(access, "at+jwt")!["sub"], verifier.Verify(access, "at+jwt")!["sub"]);
        Assert.Equal(1, handler.Asked);
        Assert.Equal("https://home.example.invalid/identity/jwks", handler.LastUri);   // the home's published keys, whatever trailing slash the address had
        await verifier.PrepareAsync(access, default);
        Assert.Equal(1, handler.Asked);   // a key it holds is not asked for again

        // instance B's validator refuses instance A's token, a token of another type is no access token, and a changed token fails its signature
        Assert.Equal(AppTokenFailure.WrongAudience, validator.Validate(access, "globex").Failure);
        Assert.Null(verifier.Verify(access, "JWT"));
        var parts = access.Split('.');
        var tampered = parts[0] + "." + parts[1] + "." + parts[2][..^2] + (parts[2][^1] == 'A' ? "BB" : "AA");
        Assert.Null(verifier.Verify(tampered, "at+jwt"));
        Assert.Null(verifier.Verify(null, "at+jwt"));
        Assert.Null(verifier.Verify("not.a.token", "at+jwt"));

        // another home's token (the same key id would not matter: the issuer is checked) is refused
        var elsewhere = new JwksTokenVerifier("https://other-home.example.invalid", new HttpClient(new JwksHandler(() => _home.Keys.Jwks())));
        await elsewhere.RefreshAsync(default);
        Assert.Null(elsewhere.Verify(access, "at+jwt"));
    }

    [Fact]
    public async Task A_key_the_home_made_since_is_fetched_once_and_a_flood_of_invented_key_ids_is_not_asked_for()
    {
        var clock = new Clock();
        var handler = new JwksHandler(() => _home.Keys.Jwks());
        var verifier = new JwksTokenVerifier("https://home.example.invalid", new HttpClient(handler), clock);
        var acme = App("acme");
        var (first, _) = SignIn(acme, _ada);
        await verifier.PrepareAsync(first, default);
        Assert.NotNull(verifier.Verify(first, "at+jwt"));

        _home.Keys.Rotate();   // the home signs with a new key from now on
        var (second, _) = SignIn(acme, _ada);
        Assert.Null(verifier.Verify(second, "at+jwt"));   // not trusted before the home published it to this host
        clock.Now += JwksTokenVerifier.Retry + TimeSpan.FromSeconds(1);
        await verifier.PrepareAsync(second, default);
        Assert.NotNull(verifier.Verify(second, "at+jwt"));
        Assert.NotNull(verifier.Verify(first, "at+jwt"));   // the older key is still published
        var asked = handler.Asked;

        // a token with a key id nobody published makes the host look once, then not again within the wait
        string Invented(string kid)
        {
            var header = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"alg\":\"ES256\",\"typ\":\"at+jwt\",\"kid\":\"" + kid + "\"}")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            return header + "." + second.Split('.')[1] + "." + second.Split('.')[2];
        }
        clock.Now += JwksTokenVerifier.Retry + TimeSpan.FromSeconds(1);
        await verifier.PrepareAsync(Invented("aaaa"), default);
        await verifier.PrepareAsync(Invented("bbbb"), default);
        await verifier.PrepareAsync(Invented("cccc"), default);
        Assert.Equal(asked + 1, handler.Asked);
        Assert.Null(verifier.Verify(Invented("aaaa"), "at+jwt"));
    }
}
