using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Identity.Tests;

/// <summary>
/// Plan 20 O06: the home as shared hosting code (what the serving layer and <c>identity run</c> both host), the key protection that lets instances of a group share the signing keys, and the
/// administration verbs both command lines have.
/// </summary>
public sealed class IdentityHomeHostingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ddk-idhome-" + Guid.NewGuid().ToString("N")[..8]);

    public IdentityHomeHostingTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { /* released later */ }
    }

    private StateDatabase NewDatabase(string name = "state.db") => StateDatabase.OpenModules(Path.Combine(_directory, name), IdentityHomeState.Modules);

    [Fact]
    public void Instances_given_the_same_key_share_the_signing_keys_and_another_key_cannot_read_them()
    {
        var database = NewDatabase();
        var key = KeySigningKeyProtector.NewKey();
        var first = new SigningKeys(database, KeySigningKeyProtector.FromBase64(key));
        var made = first.Current();

        // a second instance of the group, with the group's key: the same signing key, usable
        var second = new SigningKeys(database, KeySigningKeyProtector.FromBase64(key)).Current();
        Assert.Equal(made.Kid, second.Kid);
        var data = "a token"u8.ToArray();
        Assert.True(made.Private.VerifyData(data, second.Private.SignData(data, HashAlgorithmName.SHA256), HashAlgorithmName.SHA256));

        // an instance given another key cannot sign as the group
        Assert.ThrowsAny<CryptographicException>(() => new SigningKeys(database, KeySigningKeyProtector.FromBase64(KeySigningKeyProtector.NewKey())).Current());
    }

    [Fact]
    public void A_key_that_is_not_32_bytes_of_base64_is_refused_with_what_is_wrong()
    {
        Assert.Contains("not base64", Assert.Throws<FormatException>(() => KeySigningKeyProtector.FromBase64("not a key!")).Message);
        Assert.Contains("32 bytes; this one is 16", Assert.Throws<FormatException>(() => KeySigningKeyProtector.FromBase64(Convert.ToBase64String(new byte[16]))).Message);
    }

    [Fact]
    public void The_protected_form_is_not_the_key_and_a_changed_byte_is_refused()
    {
        var protector = new KeySigningKeyProtector(RandomNumberGenerator.GetBytes(32));
        var secret = RandomNumberGenerator.GetBytes(48);
        var sealedKey = protector.Protect(secret);
        Assert.False(sealedKey.AsSpan().IndexOf(secret.AsSpan(0, 16)) >= 0);
        Assert.Equal(secret, protector.Unprotect(sealedKey));
        sealedKey[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(sealedKey));
    }

    [Fact]
    public async Task The_hosted_home_serves_its_endpoints_and_pages_with_no_web_app_and_keeps_everything_else_closed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        var database = NewDatabase();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddLogging();
        // the issuer is the public address, whatever the listener: a serving layer behind a balancer is reached at a name of its own
        builder.AddIdentityHome(new IdentityHomeOptions(new Uri("https://identity.example.invalid"), _directory, _ => database,
            _ => new KeySigningKeyProtector(RandomNumberGenerator.GetBytes(32)), new AuthProduct("DbDataHomeTest", "identity invite")));
        await using var app = builder.Build();
        app.MapIdentityHome(_ => { });
        await app.StartAsync();
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        var discovery = JsonNode.Parse(await http.GetStringAsync("/.well-known/openid-configuration"))!;
        Assert.Equal("https://identity.example.invalid", (string?)discovery["issuer"]);
        Assert.Equal("https://identity.example.invalid/identity/authorize", (string?)discovery["authorization_endpoint"]);
        Assert.NotEmpty(JsonNode.Parse(await http.GetStringAsync("/identity/jwks"))!["keys"]!.AsArray());

        Assert.Contains("data-testid=\"identity-sign-in\"", await http.GetStringAsync(IdentityHome.SignInPath));
        Assert.Contains("data-testid=\"identity-invite\"", await http.GetStringAsync(IdentityHome.InvitePath));

        // the first-run invitation names the home's own invitation page
        var firstRun = await File.ReadAllTextAsync(Path.Combine(_directory, BootstrapInvite.FileName));
        Assert.Contains("/invite#", firstRun);

        // nobody is signed in: the provider sends them to the sign-in page, and the people are not listed
        var authorize = await http.GetAsync("/identity/authorize?client_id=x");
        Assert.Equal(HttpStatusCode.Redirect, authorize.StatusCode);
        Assert.StartsWith(IdentityHome.SignInPath + "?returnUrl=", authorize.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await http.GetAsync("/api/users")).StatusCode);
        await app.StopAsync();
    }

    [Fact]
    public void The_home_paths_are_told_apart_from_a_hosts_own()
    {
        foreach (var path in new[] { "/identity/token", "/.well-known/openid-configuration", "/invite", "/api/auth/status", "/api/invites/check", "/api/users" })
            Assert.True(IdentityHome.IsHomePath(path), path);
        foreach (var path in new[] { "/", "/api/v1/semantic-models", "/management/v1/pairing", "/health/live", "/identityx", "/api/authority" })
            Assert.False(IdentityHome.IsHomePath(path), path);
    }

    [Fact]
    public async Task The_administration_verbs_invite_list_register_and_assign_on_the_homes_state()
    {
        var database = NewDatabase();
        var where = new IdentityAdminLocation("[--repo <dir>]", _ => database, _ => "https://identity.example.invalid/");
        var noun = new Noun("identity", "test", IdentityVerbs.Admin(where));

        async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Run(params string[] args)
        {
            var o = new StringWriter();
            var e = new StringWriter();
            var context = new CliContext(o, e, TextReader.Null);
            var verb = noun.Verbs.Single(v => v.Name == args[0]);
            var exit = await verb.Run(args[1..], context);
            return (exit, o.ToString(), e.ToString(), context.Data);
        }

        // an invitation needs to say who it is for
        Assert.Equal(CliExit.Usage, (await Run("invite")).Exit);
        var invite = await Run("invite", "--email", "ada@example.invalid", "--role", "admin");
        Assert.Equal(CliExit.Ok, invite.Exit);
        Assert.StartsWith("https://identity.example.invalid/invite#", (string)invite.Data["url"]!);
        Assert.Equal("Admin", invite.Data["role"]);

        var ada = new IdentityUserStore(database).CreateUser("Ada Lovelace", "grace@example.invalid", UserRole.Viewer, emailVerified: true);
        var list = await Run("user-list");
        Assert.Equal(CliExit.Ok, list.Exit);
        Assert.Contains(ada.Id, list.Out);

        var client = await Run("client-add", "--name", "Operations console", "--redirect", "https://console.example.invalid/api/identity/callback");
        Assert.Equal(CliExit.Ok, client.Exit);
        Assert.NotNull(client.Data["secret"]);
        Assert.Contains("Operations console", (await Run("client-list")).Out);

        Assert.Equal(CliExit.Failure, (await Run("assign", "--user", "nobody", "--server", "proxy-1", "--roles", "admin")).Exit);
        Assert.Equal(CliExit.Ok, (await Run("assign", "--user", ada.Id, "--server", "proxy-1", "--roles", "admin")).Exit);
        Assert.Equal(ServerRoles.Close(ServerRole.Admin), new HomeDirectory(database).RolesOn(ada.Id, "proxy-1").Roles);
    }
}
