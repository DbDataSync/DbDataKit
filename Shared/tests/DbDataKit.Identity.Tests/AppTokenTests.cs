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
}
