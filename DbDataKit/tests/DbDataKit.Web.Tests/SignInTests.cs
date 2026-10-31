using Xunit;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DbDataKit.Web.Tests;

/// <summary>The session sign-in is shared (plan 13, U05): nothing in it may be one product's. Every test runs for a product that is not Sync, and checks that its names, section and cookie are its own.</summary>
public sealed class SignInTests : IDisposable
{
    private static readonly AuthProduct Console = new("DbDataKit");
    private readonly string _dir = Directory.CreateTempSubdirectory("signin-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private StateDatabase Open() =>
        new(Path.Combine(_dir, "state.db"), new StateSchema([IdentitySchema.Accounts, IdentitySchema.Invites, IdentitySchema.AccountKeys]));

    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    [Fact]
    public void A_product_names_its_own_cookie_scheme_claim_section_and_author()
    {
        Assert.Equal("DbDataKit:Auth", Console.Section);
        Assert.Equal("dbdatakit.session", Console.SessionCookie);
        Assert.Equal("DbDataKitSession", Console.SchemeName);
        Assert.Equal("dbdatakit:userId", Console.UserIdClaim);
        Assert.Equal("dbdatakit invite", Console.InviteCommand);
        Assert.Equal("DbDataKit", Console.SystemAuthor.Name);
        Assert.Equal("dbdatakit@localhost", Console.SystemAuthor.Email);
        Assert.NotEqual(new AuthProduct("DbDataSync").SessionCookie, Console.SessionCookie);
    }

    [Fact]
    public void Options_are_read_from_the_products_own_section_only()
    {
        var configuration = Config(
            ("DbDataKit:Auth:Windows:AdminGroup", "kit-admins"),
            ("DbDataKit:Auth:Network:Viewer", "Remote"),
            ("DbDataSync:Auth:Windows:AdminGroup", "sync-admins"),
            ("DbDataSync:Auth:Network:Admin", "Loopback"));

        var options = AuthOptions.FromConfiguration(configuration, Console);

        Assert.Equal("kit-admins", options.AdminGroup);
        Assert.True(options.WindowsEnabled);
        Assert.Equal(ViewerNetworkTrust.Remote, options.NetworkViewer);
        Assert.Equal(AdminNetworkTrust.Disabled, options.NetworkAdmin);
        Assert.Equal("dbdatakit.session", options.CookieName);
    }

    [Fact]
    public void Passkey_options_default_the_relying_party_name_to_the_product_and_take_the_origins_they_are_given()
    {
        var configuration = Config(("DbDataKit:Auth:Passkeys:RelyingPartyId", "console.example.invalid"));

        var options = PasskeyOptions.FromConfiguration(configuration, Console, "https://console.example.invalid", ["https://alt.console.example.invalid"], ["http://localhost:5173"]);

        Assert.Equal("DbDataKit", options.RelyingPartyName);
        Assert.Equal("console.example.invalid", options.RelyingPartyId);
        Assert.Equal(["https://alt.console.example.invalid", "https://console.example.invalid"], options.Origins.Order());
        Assert.Null(options.Problem());
    }

    [Fact]
    public void The_development_origins_are_used_only_when_no_alternate_url_is_configured()
    {
        var configuration = Config();

        var fresh = PasskeyOptions.FromConfiguration(configuration, Console, "http://localhost:5080", [], ["http://localhost:5173"]);
        var configured = PasskeyOptions.FromConfiguration(configuration, Console, "http://localhost:5080", ["http://localhost:6000"], ["http://localhost:5173"]);

        Assert.Contains("http://localhost:5173", fresh.Origins);
        Assert.DoesNotContain("http://localhost:5173", configured.Origins);
        Assert.Contains("http://localhost:6000", configured.Origins);
    }

    [Fact]
    public void An_origin_outside_the_relying_party_id_is_a_problem_that_names_it()
    {
        var options = PasskeyOptions.FromConfiguration(
            Config(("DbDataKit:Auth:Passkeys:RelyingPartyId", "console.example.invalid")), Console, "https://elsewhere.example.invalid", [], []);

        Assert.Contains("elsewhere.example.invalid", options.Problem());
    }

    private async Task<AuthenticateResult> Authenticate(AuthOptions options, UserStore users, SessionStore sessions, string? cookie)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
        if (cookie is not null) context.Request.Headers.Cookie = $"{options.CookieName}={cookie}";
        var handler = new SessionAuthenticationHandler(
            new StaticOptions(), NullLoggerFactory.Instance, UrlEncoder.Default, sessions, users, options);
        await handler.InitializeAsync(new AuthenticationScheme(Console.SchemeName, null, typeof(SessionAuthenticationHandler)), context);
        return await handler.AuthenticateAsync();
    }

    [Fact]
    public async Task A_session_cookie_signs_the_user_in_under_the_products_scheme_and_claim()
    {
        var database = Open();
        var users = new UserStore(database);
        var sessions = new SessionStore(database);
        var options = AuthOptions.FromConfiguration(Config(), Console);
        var user = users.CreateUser("Ada", "ada@example.invalid", UserRole.Admin);
        var session = sessions.Create(user.Id);

        var result = await Authenticate(options, users, sessions, session.Id);

        Assert.True(result.Succeeded);
        Assert.Equal(Console.SchemeName, result.Ticket!.AuthenticationScheme);
        Assert.Equal(user.Id, result.Principal!.FindFirst(Console.UserIdClaim)?.Value);
        Assert.Equal("Admin", result.Principal.FindFirst(ClaimTypes.Role)?.Value);
        Assert.Equal("ada@example.invalid", result.Principal.FindFirst(ClaimTypes.Email)?.Value);
    }

    [Fact]
    public async Task Another_products_cookie_is_not_a_session_here()
    {
        var database = Open();
        var users = new UserStore(database);
        var sessions = new SessionStore(database);
        var options = AuthOptions.FromConfiguration(Config(), Console);
        var session = sessions.Create(users.CreateUser("Ada", null, UserRole.Admin).Id);
        var context = new DefaultHttpContext();
        context.Request.Headers.Cookie = $"{new AuthProduct("DbDataSync").SessionCookie}={session.Id}";
        var handler = new SessionAuthenticationHandler(new StaticOptions(), NullLoggerFactory.Instance, UrlEncoder.Default, sessions, users, options);
        await handler.InitializeAsync(new AuthenticationScheme(Console.SchemeName, null, typeof(SessionAuthenticationHandler)), context);

        var result = await handler.AuthenticateAsync();

        Assert.False(result.Succeeded);
        Assert.True(result.None);
    }

    [Fact]
    public async Task A_cookie_for_a_missing_session_fails_and_no_cookie_falls_back_to_the_network_trust()
    {
        var database = Open();
        var users = new UserStore(database);
        var sessions = new SessionStore(database);

        var stale = await Authenticate(AuthOptions.FromConfiguration(Config(), Console), users, sessions, "not-a-session");
        var remoteViewer = await Authenticate(
            AuthOptions.FromConfiguration(Config(("DbDataKit:Auth:Network:Viewer", "Remote")), Console), users, sessions, cookie: null);
        var nobody = await Authenticate(AuthOptions.FromConfiguration(Config(), Console), users, sessions, cookie: null);

        Assert.NotNull(stale.Failure);
        Assert.Equal("Viewer", remoteViewer.Principal!.FindFirst(ClaimTypes.Role)?.Value);
        Assert.Null(remoteViewer.Principal.FindFirst(Console.UserIdClaim));
        Assert.True(nobody.None);
    }

    [Fact]
    public void A_config_commit_is_attributed_to_the_signed_in_user_or_else_to_the_product()
    {
        var options = AuthOptions.FromConfiguration(Config(), Console);
        var passkeys = PasskeyOptions.FromConfiguration(Config(), Console, "https://localhost", [], []);
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext() };
        var current = new CurrentUser(accessor, options, passkeys);

        Assert.Equal(Console.SystemAuthor, current.Author);

        accessor.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "Grace"), new Claim(Console.UserIdClaim, "u1")], "test"));

        Assert.Equal("Grace", current.Author.Name);
        Assert.Equal(Console.SystemAuthor.Email, current.Author.Email);
        Assert.Equal("u1", current.Id);
    }

    [Fact]
    public async Task The_bootstrap_invite_is_written_where_it_is_told_and_names_the_products_command()
    {
        var database = Open();
        var users = new UserStore(database);
        var invites = new InviteStore(database);
        var options = AuthOptions.FromConfiguration(Config(), Console);
        var bootstrap = new BootstrapInvite(users, invites, options, new BootstrapInviteLocation(_dir), NullLogger<BootstrapInvite>.Instance);

        await bootstrap.StartAsync(CancellationToken.None);

        var text = File.ReadAllText(Path.Combine(_dir, BootstrapInvite.FileName));
        Assert.Contains("/invite#", text);
        Assert.Contains("`dbdatakit invite`", text);
        Assert.DoesNotContain("dbdatasync", text);

        users.CreateUser("Ada", null, UserRole.Admin);
        await bootstrap.StartAsync(CancellationToken.None);
        Assert.False(File.Exists(Path.Combine(_dir, BootstrapInvite.FileName)));
    }

    private sealed class StaticOptions : IOptionsMonitor<AuthenticationSchemeOptions>
    {
        public AuthenticationSchemeOptions CurrentValue { get; } = new();
        public AuthenticationSchemeOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<AuthenticationSchemeOptions, string?> listener) => null;
    }
}
