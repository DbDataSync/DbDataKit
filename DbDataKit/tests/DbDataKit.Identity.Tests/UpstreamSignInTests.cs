using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Identity.Tests;

/// <summary>
/// Plan 20 O06b: signing in at the home through Microsoft Entra ID (a stand-in tenant: a real HTTP server with discovery, keys and a token endpoint that signs RS256 id tokens with a <c>groups</c> claim) and with Windows (Negotiate,
/// on Windows). The home is the shared hosted one, served over HTTP on loopback; the browser is an <see cref="HttpClient"/> that follows nothing on its own, so each hop is checked.
/// </summary>
public sealed class UpstreamSignInTests : IAsyncLifetime
{
    private const string Issuer = "https://home.example.invalid", Server = "build-1", OperatorsGroup = "7f3c1b0e-aaaa-4b1d-9c11-000000000001";

    private static readonly AuthProduct Product = new("DbDataHomeUpstreamTest", "identity invite");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ddk-upstream-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly RSA _tenantKey = RSA.Create(2048);
    private readonly KeySigningKeyProtector _protector = new(RandomNumberGenerator.GetBytes(32));
    private WebApplication? _tenant, _home;
    private StateDatabase _database = null!;
    private string _authority = null!, _homeUrl = null!;

    // what the stand-in tenant's next id token says
    private string _nonce = "", _subject = "entra-oid-ada";
    private string[] _groups = [];
    private RSA? _signWith;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = new StateDatabase(Path.Combine(_directory, "state.db"), HomeSchema.Schema);

        var port = FreePort();
        _authority = $"http://127.0.0.1:{port}/tenant-1/v2.0";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        _tenant = builder.Build();
        _tenant.MapGet("/tenant-1/v2.0/.well-known/openid-configuration", () => Results.Json(new
        {
            issuer = _authority, authorization_endpoint = _authority + "/authorize", token_endpoint = _authority + "/token", jwks_uri = _authority + "/keys",
        }));
        _tenant.MapGet("/tenant-1/v2.0/keys", () =>
        {
            var p = _tenantKey.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", use = "sig", alg = "RS256", kid = "entra-key-1", n = Base64Url.EncodeToString(p.Modulus!), e = Base64Url.EncodeToString(p.Exponent!) } } });
        });
        _tenant.MapPost("/tenant-1/v2.0/token", async (HttpRequest request) =>
        {
            var form = await request.ReadFormAsync();
            // the home is a confidential client of the tenant
            if (form["client_id"] != "home-app" || form["client_secret"] != "tenant-secret") return Results.Json(new { error = "invalid_client" }, statusCode: 401);
            return Results.Json(new { id_token = IdToken(), access_token = "not-used", token_type = "Bearer" });
        });
        await _tenant.StartAsync();
    }

    private async Task StartHome(EntraUpstream? entra, WindowsUpstream? windows = null)
    {
        var port = FreePort();
        _homeUrl = $"http://127.0.0.1:{port}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(_homeUrl);
        builder.Services.AddLogging();
        builder.AddIdentityHome(new IdentityHomeOptions(new Uri(Issuer), _directory, _ => _database, _ => _protector,
            Product, Entra: entra, Windows: windows));
        _home = builder.Build();
        _home.MapIdentityHome(_ => { });
        await _home.StartAsync();
    }

    private EntraUpstream Entra(bool trustEmail = false) => new(_authority, "home-app", "tenant-secret", trustEmail);

    public async ValueTask DisposeAsync()
    {
        if (_home is not null) await _home.DisposeAsync();
        if (_tenant is not null) await _tenant.DisposeAsync();
        _tenantKey.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { /* released later */ }
    }

    private string IdToken()
    {
        static string B(object o) => Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(o));
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = _authority, ["aud"] = "home-app", ["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds(), ["sub"] = _subject, ["name"] = "Ada Lovelace",
            ["email"] = "ada@clearwater.example.invalid", ["nonce"] = _nonce, ["groups"] = _groups,
        };
        var signingInput = B(new { alg = "RS256", kid = "entra-key-1", typ = "JWT" }) + "." + B(claims);
        return signingInput + "." + Base64Url.EncodeToString((_signWith ?? _tenantKey).SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }

    private static HttpClient Browser(string baseAddress) => new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(baseAddress) };

    private static string? SessionCookie(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith(Product.SessionCookie + "=", StringComparison.Ordinal) && c.Split('=', 2)[1].Length > 0) : null;

    /// <summary>The browser starts at the home, is sent to the tenant, and the tenant sends it back with a code (or with <paramref name="tenantError"/>): the home's answer to the callback.</summary>
    private async Task<HttpResponseMessage> SignInThroughTenant(HttpClient browser, string? returnUrl = null, string? tenantError = null)
    {
        var start = await browser.GetAsync(UpstreamSignIn.EntraPath + (returnUrl is null ? "" : "?returnUrl=" + Uri.EscapeDataString(returnUrl)));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        var toTenant = start.Headers.Location!;
        Assert.StartsWith(_authority + "/authorize?", toTenant.AbsoluteUri);
        var query = System.Web.HttpUtility.ParseQueryString(toTenant.Query);
        Assert.Equal(Issuer + UpstreamSignIn.EntraCallbackPath, query["redirect_uri"]);
        _nonce = query["nonce"]!;
        var back = tenantError is null ? $"?state={Uri.EscapeDataString(query["state"]!)}&code=code-from-entra" : $"?state={Uri.EscapeDataString(query["state"]!)}&error={tenantError}";
        return await browser.GetAsync(UpstreamSignIn.EntraCallbackPath + back);
    }

    [Fact]
    public async Task A_person_signs_in_at_the_home_through_entra_and_is_the_homes_user_with_the_mapped_roles_in_the_token_a_console_reads()
    {
        await StartHome(Entra(trustEmail: true));
        var directory = new HomeDirectory(_database);
        directory.MapExternalGroup(ExternalSources.Entra, OperatorsGroup, Server, ServerRole.Operator);
        var console = new OidcClients(_database).Register("Operations console", ["https://console.example.invalid/callback"], confidential: false).Client.ClientId;

        // the console sent the browser to the home's authorize endpoint; nobody is signed in there, so the home sends it to its sign-in page, which offers Entra
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48));
        var challenge = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorize = $"/identity/authorize?response_type=code&client_id={console}&redirect_uri={Uri.EscapeDataString("https://console.example.invalid/callback")}&scope=openid%20profile&state=s1&nonce=n1&code_challenge={challenge}&code_challenge_method=S256";
        using var browser = Browser(_homeUrl);
        var notSignedIn = await browser.GetAsync(authorize);
        Assert.StartsWith(IdentityHome.SignInPath + "?returnUrl=", notSignedIn.Headers.Location!.OriginalString);
        Assert.Contains("data-testid=\"identity-sign-in-entra\"", await browser.GetStringAsync(notSignedIn.Headers.Location.OriginalString));

        _groups = [OperatorsGroup, "7f3c1b0e-aaaa-4b1d-9c11-000000000009"];
        var callback = await SignInThroughTenant(browser, authorize);

        // signed in at the home, and back to the sign-in page, which goes on to the authorize request (a navigation of the home's own, so the strict cookie goes with it)
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal(IdentityHome.SignInPath + "?returnUrl=" + Uri.EscapeDataString(authorize), callback.Headers.Location!.OriginalString);
        var cookie = SessionCookie(callback);
        Assert.NotNull(cookie);
        var ada = Assert.Single(new UserStore(_database).List());
        Assert.Equal("ada@clearwater.example.invalid", ada.Email);
        Assert.Contains(new UserStore(_database).CredentialsOf(ada.Id), c => c.Method == ExternalSources.CredentialMethod(ExternalSources.Entra) && c.Subject == "entra-oid-ada");

        // the console's authorize request now answers with a code, and the home's id token carries the role the Entra group maps to
        var again = new HttpRequestMessage(HttpMethod.Get, authorize);
        again.Headers.Add("Cookie", cookie);
        var withCode = await browser.SendAsync(again);
        Assert.Equal(HttpStatusCode.Redirect, withCode.StatusCode);
        var code = System.Web.HttpUtility.ParseQueryString(withCode.Headers.Location!.Query)["code"]!;
        var token = await browser.PostAsync("/identity/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = "https://console.example.invalid/callback", ["client_id"] = console, ["code_verifier"] = verifier,
        }));
        Assert.Equal(HttpStatusCode.OK, token.StatusCode);
        var idToken = JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement.GetProperty("id_token").GetString()!;
        var claims = JsonDocument.Parse(Base64Url.DecodeFromChars(idToken.Split('.')[1])).RootElement;
        Assert.Equal(ada.Id, claims.GetProperty("sub").GetString());
        Assert.Contains("operator", claims.GetProperty("dbdata_roles").GetProperty($"server:{Server}").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_sign_in_the_tenant_refuses_or_that_was_not_started_here_or_is_signed_with_another_key_signs_nobody_in()
    {
        await StartHome(Entra());
        using var browser = Browser(_homeUrl);

        // the tenant said no
        var refused = await SignInThroughTenant(browser, tenantError: "access_denied");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Null(SessionCookie(refused));
        Assert.Contains("access_denied", await refused.Content.ReadAsStringAsync());

        // a callback the home did not start (a forged or replayed state)
        var forged = await browser.GetAsync(UpstreamSignIn.EntraCallbackPath + "?state=not-mine&code=code-from-entra");
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Null(SessionCookie(forged));

        // an id token the tenant did not sign
        using var other = RSA.Create(2048);
        _signWith = other;
        var badSignature = await SignInThroughTenant(browser);
        Assert.Equal(HttpStatusCode.Forbidden, badSignature.StatusCode);
        Assert.Null(SessionCookie(badSignature));

        Assert.Empty(new UserStore(_database).List());
    }

    [Fact]
    public async Task An_address_the_tenant_is_not_trusted_for_links_no_account_and_a_disabled_person_is_refused()
    {
        await StartHome(Entra(trustEmail: false));
        var users = new UserStore(_database);
        var grace = users.CreateUser("Grace Hopper", "ada@clearwater.example.invalid", UserRole.Admin, emailVerified: true);

        using var browser = Browser(_homeUrl);
        Assert.Equal(HttpStatusCode.Redirect, (await SignInThroughTenant(browser)).StatusCode);
        // the account that holds the address is not taken over by a tenant that is not trusted for addresses: the person is a new account
        var made = Assert.Single(users.List(), u => u.Id != grace.Id);
        Assert.DoesNotContain(users.CredentialsOf(grace.Id), c => c.Method == ExternalSources.CredentialMethod(ExternalSources.Entra));

        users.SetEnabled(made.Id, false);
        var disabled = await SignInThroughTenant(browser);
        Assert.Equal(HttpStatusCode.Forbidden, disabled.StatusCode);
        Assert.Null(SessionCookie(disabled));
    }

    [Fact]
    public async Task The_sign_in_page_offers_each_upstream_that_is_configured_and_no_other()
    {
        await StartHome(entra: null);
        using (var browser = Browser(_homeUrl))
        {
            var page = await browser.GetStringAsync(IdentityHome.SignInPath);
            Assert.Contains("identity-sign-in-passkey", page);
            Assert.DoesNotContain("identity-sign-in-entra", page);
            Assert.DoesNotContain("identity-sign-in-windows", page);
            // not offered: the address is nothing of the home's, so it is closed like anything else unmarked
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(UpstreamSignIn.EntraPath)).StatusCode);
        }
        await _home!.DisposeAsync();

        await StartHome(Entra() with { Label = "Sign in with Clearwater <Entra>" }, new WindowsUpstream());
        using var configured = Browser(_homeUrl);
        var both = await configured.GetStringAsync(IdentityHome.SignInPath);
        Assert.Contains("data-testid=\"identity-sign-in-entra\"", both);
        Assert.Contains("Sign in with Clearwater &lt;Entra&gt;", both);
        Assert.Contains("data-testid=\"identity-sign-in-windows\"", both);
        // Windows is negotiated, never anonymous
        var windows = await configured.GetAsync(UpstreamSignIn.WindowsPath);
        Assert.Equal(HttpStatusCode.Unauthorized, windows.StatusCode);
        Assert.Contains("Negotiate", windows.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public void A_negotiated_account_is_its_sid_its_upn_and_its_groups()
    {
        // Windows: the SID is the subject, the UPN the address, the group SIDs the groups
        var windows = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, @"CLEARWATER\ada"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001"), new Claim(ClaimTypes.Upn, "ada@clearwater.example.invalid"),
            new Claim(ClaimTypes.GroupSid, "S-1-5-21-1-2-3-2001"),
        ], "Negotiate"));
        var ada = UpstreamSignIn.FromNegotiate(windows)!;
        Assert.Equal((ExternalSources.Windows, "S-1-5-21-1-2-3-1001", @"CLEARWATER\ada", "ada@clearwater.example.invalid", true), (ada.Source, ada.Subject, ada.Name, ada.Email, ada.EmailVerified));
        Assert.Contains("S-1-5-21-1-2-3-2001", ada.Groups);

        // Linux with a keytab: no SID, the Kerberos name is the subject, the groups an LDAP lookup gave are role claims, and a UPN that is not an address is no key
        var linux = UpstreamSignIn.FromNegotiate(new ClaimsPrincipal(new ClaimsIdentity([
            new Claim(ClaimTypes.Name, "grace@CLEARWATER.EXAMPLE"), new Claim(ClaimTypes.Upn, @"CLEARWATER\grace"), new Claim(ClaimTypes.Role, "Data Operators"),
        ], "Negotiate")))!;
        Assert.Equal(("grace@CLEARWATER.EXAMPLE", null, false), (linux.Subject, linux.Email, linux.EmailVerified));
        Assert.Equal(["Data Operators"], linux.Groups);

        Assert.Null(UpstreamSignIn.FromNegotiate(new ClaimsPrincipal(new ClaimsIdentity())));
    }

    [Fact]
    public async Task On_windows_a_person_signs_in_at_the_home_with_their_windows_account_and_gets_what_its_groups_map_to()
    {
        if (!OperatingSystem.IsWindows()) { Assert.Skip("Windows only: Negotiate with the account the tests run as"); return; }
        await StartHome(entra: null, new WindowsUpstream());
        var me = WindowsIdentity.GetCurrent();
        // BUILTIN\Users: every account the tests could run as is in it
        new HomeDirectory(_database).MapExternalGroup(ExternalSources.Windows, "S-1-5-32-545", Server, ServerRole.Operator);

        var port = new Uri(_homeUrl).Port;
        using var browser = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = true }) { BaseAddress = new Uri($"http://localhost:{port}") };
        var signedIn = await browser.GetAsync(UpstreamSignIn.WindowsPath + "?returnUrl=" + Uri.EscapeDataString("/identity/authorize?client_id=x"));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.StartsWith(IdentityHome.SignInPath + "?returnUrl=", signedIn.Headers.Location!.OriginalString);
        Assert.NotNull(SessionCookie(signedIn));

        var users = new UserStore(_database);
        var user = users.FindByCredential(CredentialMethods.Windows, me.User!.Value);
        Assert.NotNull(user);
        var directory = new HomeDirectory(_database);
        Assert.Contains(directory.ExternalGroupsOf(user.Id), g => g.Source == ExternalSources.Windows && g.Group == "s-1-5-32-545");
        Assert.Contains(directory.ExternalGroupsOf(user.Id), g => g.Group == @"builtin\users");
        Assert.True(directory.RolesOn(user.Id, Server).Roles.HasFlag(ServerRole.Operator));
    }
}
