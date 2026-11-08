using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Identity.Tests;

/// <summary>
/// Plan 13a U20: personal access tokens and service accounts in the identity home. A token is shown once and kept only as a hash; a person's lasts 90 days unless asked otherwise and at most a year, or the install's lower
/// maximum; an expired or revoked token is refused; a service account's may never expire; an administrator revokes anyone's; and a user name with a token signs in as the same user, with the same id, as the person's other
/// ways in.
/// </summary>
public sealed class AccessTokenTests : IDisposable
{
    private readonly HomeFixture _home = new();
    private readonly Clock _clock = new(DateTimeOffset.Parse("2026-11-02T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));

    public void Dispose() => _home.Dispose();

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private AccessTokens Tokens(AccessTokenPolicy? policy = null) => new(_home.Database, _home.Users, policy, _clock);

    private UserRecord Ada() => _home.Users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Viewer, emailVerified: true);

    [Fact]
    public void A_token_is_shown_once_and_kept_only_as_a_hash()
    {
        var ada = Ada();
        var made = Tokens().Create(ada.Id, "Excel on my laptop", ada.Id);
        Assert.StartsWith("ddk_" + made.Record.Id + "_", made.Secret);

        // the secret is in no column and no listing
        var secret = made.Secret.Split('_', 3)[2];
        using (var connection = _home.Database.OpenConnection())
        using (var cmd = _home.Database.Command("identity", connection, "SELECT * FROM {table:access_token};"))
        using (var reader = cmd.ExecuteReader())
            while (reader.Read())
                for (var i = 0; i < reader.FieldCount; i++)
                    Assert.DoesNotContain(secret, reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, JsonSerializer.Serialize(Tokens().List(ada.Id)), StringComparison.Ordinal);
        Assert.Equal(ada.Id, Tokens().Verify("ada@example.invalid", made.Secret)?.Id);
    }

    [Fact]
    public void A_persons_token_lasts_90_days_by_default_at_most_a_year_and_the_installs_lower_maximum_applies()
    {
        var ada = Ada();
        var now = _clock.Now;
        Assert.Equal(now.AddDays(90), Tokens().Create(ada.Id, "default", ada.Id).Record.ExpiresAtUtc);
        Assert.Equal(now.AddDays(365), Tokens().Create(ada.Id, "a year", ada.Id, TimeSpan.FromDays(365)).Record.ExpiresAtUtc);
        Assert.Contains("at most 365 days", Assert.Throws<AccessTokenException>(() => Tokens().Create(ada.Id, "too long", ada.Id, TimeSpan.FromDays(366))).Message);
        Assert.Throws<AccessTokenException>(() => Tokens().Create(ada.Id, "forever", ada.Id, noExpiry: true));

        // an install that allows 30 days: the default is 30 too, and 31 is refused
        var strict = Tokens(new AccessTokenPolicy(maxDays: 30));
        Assert.Equal(now.AddDays(30), strict.Create(ada.Id, "default here", ada.Id).Record.ExpiresAtUtc);
        Assert.Contains("at most 30 days", Assert.Throws<AccessTokenException>(() => strict.Create(ada.Id, "too long here", ada.Id, TimeSpan.FromDays(31))).Message);
        // and no install may allow more than a year
        Assert.Throws<ArgumentOutOfRangeException>(() => new AccessTokenPolicy(maxDays: 366));
    }

    [Fact]
    public void An_expired_or_revoked_token_or_one_given_with_another_user_name_is_refused()
    {
        var ada = Ada();
        var grace = _home.Users.CreateUser("Grace Hopper", "grace@example.invalid", UserRole.Viewer, emailVerified: true);
        var tokens = Tokens();
        var expiring = tokens.Create(ada.Id, "short", ada.Id, TimeSpan.FromDays(1));
        var revoked = tokens.Create(ada.Id, "revoked", ada.Id);

        Assert.NotNull(tokens.Verify("ADA@example.invalid", expiring.Secret));   // the address is compared as the account key is
        Assert.NotNull(tokens.Verify(ada.Id, expiring.Secret));
        Assert.Null(tokens.Verify("grace@example.invalid", expiring.Secret));     // another person's name
        Assert.Null(tokens.Verify("ada@example.invalid", expiring.Secret[..^1] + (expiring.Secret[^1] == 'A' ? "B" : "A")));
        Assert.Null(tokens.Verify("ada@example.invalid", "not a token"));

        Assert.True(tokens.Revoke(revoked.Record.Id, ownerId: ada.Id));
        Assert.Null(tokens.Verify("ada@example.invalid", revoked.Secret));
        Assert.False(tokens.Revoke(revoked.Record.Id, ownerId: ada.Id));          // already revoked

        _clock.Now = _clock.Now.AddDays(1);
        Assert.Null(tokens.Verify("ada@example.invalid", expiring.Secret));
        Assert.False(tokens.List(ada.Id).Single(t => t.Id == expiring.Record.Id).IsActiveAt(_clock.Now));

        // a disabled person's tokens stop working with them
        var other = tokens.Create(grace.Id, "grace's", grace.Id);
        _home.Users.SetEnabled(grace.Id, false);
        Assert.Null(tokens.Verify("grace@example.invalid", other.Secret));
    }

    [Fact]
    public void A_service_accounts_token_may_never_expire_and_goes_with_the_accounts_name()
    {
        var tokens = Tokens(new AccessTokenPolicy(maxDays: 30));
        var refresher = tokens.CreateServiceAccount("Nightly-Refresh", "the 2am refresh", createdBy: "admin");
        Assert.Equal("nightly-refresh", refresher.Name);
        Assert.Throws<AccessTokenException>(() => tokens.CreateServiceAccount("nightly-refresh", null, "admin"));
        Assert.Throws<AccessTokenException>(() => tokens.CreateServiceAccount("someone@example.invalid", null, "admin"));

        var forever = tokens.Create(refresher.UserId, "report server", "admin", noExpiry: true);
        Assert.Null(forever.Record.ExpiresAtUtc);
        var longer = tokens.Create(refresher.UserId, "two years", "admin", TimeSpan.FromDays(730));   // not bound by the install's maximum for people
        Assert.Equal(_clock.Now.AddDays(730), longer.Record.ExpiresAtUtc);

        _clock.Now = _clock.Now.AddYears(5);
        Assert.Equal(refresher.UserId, tokens.Verify("nightly-refresh", forever.Secret)?.Id);
        Assert.Contains(tokens.List(), t => t.Id == forever.Record.Id);              // always listed, and revocable

        Assert.True(tokens.RemoveServiceAccount("nightly-refresh"));
        Assert.Null(tokens.Verify("nightly-refresh", forever.Secret));
        Assert.Null(tokens.Verify(refresher.UserId, forever.Secret));
    }

    [Fact]
    public void An_administrator_revokes_another_persons_token_and_a_person_cannot()
    {
        var ada = Ada();
        var grace = _home.Users.CreateUser("Grace Hopper", "grace@example.invalid", UserRole.Admin, emailVerified: true);
        var tokens = Tokens();
        var adas = tokens.Create(ada.Id, "ada's", ada.Id);

        Assert.False(tokens.Revoke(adas.Record.Id, ownerId: grace.Id));   // as a person, only one's own
        Assert.NotNull(tokens.Verify(ada.Id, adas.Secret));
        Assert.True(tokens.Revoke(adas.Record.Id));                        // as an administrator, anyone's
        Assert.Null(tokens.Verify(ada.Id, adas.Secret));
    }

    [Fact]
    public async Task Signing_in_with_the_user_name_and_a_token_is_the_same_user_as_the_persons_other_ways_in_and_a_token_cannot_make_another()
    {
        // Ada signs in with a passkey (her session) and through Entra too: one account
        var ada = Ada();
        var throughEntra = new ExternalSignIns(_home.Users, _home.Directory).SignIn(new ExternalIdentity(ExternalSources.Entra, "entra-oid-ada", "Ada Lovelace", "ada@example.invalid", true, []));
        Assert.Equal(ada.Id, throughEntra.Id);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");   // the server binds the port itself (no pick-then-bind race)
        builder.Services.AddLogging();
        var product = new AuthProduct("DbDataTokenTest", "identity invite");
        var directory = Path.Combine(Path.GetTempPath(), "ddk-tokens-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(directory);
        builder.AddIdentityHome(new IdentityHomeOptions(new Uri("https://home.example.invalid"), directory, _ => _home.Database, _ => new KeySigningKeyProtector(RandomNumberGenerator.GetBytes(32)), product));
        await using var app = builder.Build();
        app.MapIdentityHome(_ => { });
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var session = new IdentitySessionStore(_home.Database).Create(ada.Id).Id;
            HttpRequestMessage WithSession(HttpMethod method, string path, object? body = null)
            {
                var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
                request.Headers.Add("Cookie", $"{product.SessionCookie}={session}");
                return request;
            }

            // she makes a token at the home, signed in; it is answered once
            var made = await http.SendAsync(WithSession(HttpMethod.Post, AccessTokenEndpoints.Path, new { name = "Excel", days = 30 }));
            Assert.Equal(HttpStatusCode.OK, made.StatusCode);
            var token = (await made.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
            var listed = await (await http.SendAsync(WithSession(HttpMethod.Get, AccessTokenEndpoints.Path))).Content.ReadAsStringAsync();
            Assert.Contains("Excel", listed);
            Assert.DoesNotContain(token.Split('_', 3)[2], listed);

            // the same user id signed in with her session and with her address and the token
            static async Task<string?> WhoAmI(HttpClient http, HttpRequestMessage request) =>
                (await (await http.SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetString();
            var basic = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
            basic.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"ada@example.invalid:{token}")));
            Assert.Equal(ada.Id, await WhoAmI(http, basic));
            Assert.Equal(ada.Id, await WhoAmI(http, WithSession(HttpMethod.Get, "/api/auth/status")));

            // a wrong token is no way in, and a token cannot make another token
            var wrong = new HttpRequestMessage(HttpMethod.Get, AccessTokenEndpoints.Path);
            wrong.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("ada@example.invalid:ddk_0000_nope")));
            Assert.Equal(HttpStatusCode.Unauthorized, (await http.SendAsync(wrong)).StatusCode);
            var withToken = new HttpRequestMessage(HttpMethod.Post, AccessTokenEndpoints.Path) { Content = JsonContent.Create(new { name = "another" }) };
            withToken.Headers.Authorization = basic.Headers.Authorization;
            Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(withToken)).StatusCode);

            // she revokes it; it stops working
            var id = (await (await http.SendAsync(WithSession(HttpMethod.Get, AccessTokenEndpoints.Path))).Content.ReadFromJsonAsync<JsonElement>())[0].GetProperty("id").GetString();
            Assert.Equal(HttpStatusCode.NoContent, (await http.SendAsync(WithSession(HttpMethod.Delete, $"{AccessTokenEndpoints.Path}/{id}"))).StatusCode);
            var after = new HttpRequestMessage(HttpMethod.Get, "/api/auth/status");
            after.Headers.Authorization = basic.Headers.Authorization;
            Assert.Null((await (await http.SendAsync(after)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("userId").GetString());
            await app.StopAsync();
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { /* released later */ }
        }
    }

    [Fact]
    public async Task The_command_line_makes_service_accounts_and_their_tokens_and_lists_and_revokes_anyones()
    {
        var ada = Ada();
        var adas = Tokens().Create(ada.Id, "ada's", ada.Id);
        var where = new IdentityAdminLocation("[--repo <dir>]", _ => _home.Database, _ => "https://home.example.invalid/", _ => new AccessTokenPolicy(maxDays: 30));
        var noun = new Noun("identity", "test", IdentityVerbs.Admin(where));
        async Task<(int Exit, IDictionary<string, object?> Data)> Run(params string[] args)
        {
            var context = new CliContext(new StringWriter(), new StringWriter(), TextReader.Null);
            return (await noun.Verbs.Single(v => v.Name == args[0]).Run(args[1..], context), context.Data);
        }

        Assert.Equal(CliExit.Ok, (await Run("service-account-add", "--name", "reports")).Exit);
        var made = await Run("token-create", "--account", "reports", "--name", "report server", "--no-expiry");
        Assert.Equal(CliExit.Ok, made.Exit);
        Assert.Equal("reports", made.Data["user_name"]);
        Assert.NotNull(Tokens().Verify("reports", (string)made.Data["token"]!));
        // a person's tokens are made by the person, not at the command line
        Assert.Equal(CliExit.Failure, (await Run("token-create", "--account", ada.Id, "--name", "x")).Exit);
        Assert.Equal(CliExit.Usage, (await Run("token-create", "--account", "reports", "--name", "x", "--days", "3", "--no-expiry")).Exit);

        var list = await Run("token-list");
        Assert.Equal(2, ((System.Collections.IList)list.Data["tokens"]!).Count);
        Assert.Equal(CliExit.Ok, (await Run("token-revoke", "--id", adas.Record.Id)).Exit);
        Assert.Null(Tokens().Verify(ada.Id, adas.Secret));
        Assert.Equal(CliExit.Failure, (await Run("token-revoke", "--id", adas.Record.Id)).Exit);

        Assert.Equal(CliExit.Ok, (await Run("service-account-remove", "--name", "reports")).Exit);
        Assert.Null(Tokens().Verify("reports", (string)made.Data["token"]!));
    }
}
