using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Identity.Tests;

/// <summary>
/// Plan 20 O02b: a passkey ceremony's challenge is held by the server, in the state, and answers one response. So a sign-in started at one instance of a home's group finishes at another, and a response captured once
/// signs nobody in again: not with the cookie it came with, not after a new start, and not with a cookie that carries the old challenge (which the browser could write when the challenge lived in the cookie).
/// The authenticator is a software one: an ES256 key whose assertions are built and signed as a browser's would be.
/// </summary>
public sealed class PasskeyChallengeTests : IAsyncLifetime
{
    private const string Origin = "https://home.example.invalid";

    private static readonly AuthProduct Product = new("DbDataHomePasskeyTest", "identity invite");

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "ddk-passkey-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly byte[] _credentialId = RandomNumberGenerator.GetBytes(16);
    private readonly List<WebApplication> _homes = [];
    private StateDatabase _database = null!;
    private UserRecord _ada = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        _database = StateDatabase.OpenModules(Path.Combine(_directory, "state.db"), IdentityHomeState.Modules);
        var users = new IdentityUserStore(_database);
        _ada = users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Admin, emailVerified: true);
        users.AddCredential(_ada.Id, CredentialMethods.Passkey, Base64Url.EncodeToString(_credentialId), Convert.ToBase64String(CosePublicKey()), label: "Passkey");
        await Task.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var home in _homes) await home.DisposeAsync();
        _key.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, true); } catch (IOException) { /* released later */ }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    /// <summary>An instance of the home on the shared state: a browser that talks to it.</summary>
    private async Task<HttpClient> Home()
    {
        var url = $"http://127.0.0.1:{FreePort()}";
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls(url);
        builder.Services.AddLogging();
        builder.AddIdentityHome(new IdentityHomeOptions(new Uri(Origin), _directory, _ => _database, _ => new KeySigningKeyProtector(new byte[32]), Product));
        var home = builder.Build();
        home.MapIdentityHome(_ => { });
        await home.StartAsync();
        _homes.Add(home);
        return new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { BaseAddress = new Uri(url) };
    }

    /// <summary>The key's public half as WebAuthn stores it: a COSE EC2 key (kty 2, alg -7, crv 1, x, y).</summary>
    private byte[] CosePublicKey()
    {
        var p = _key.ExportParameters(false);
        return [0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21, 0x58, 0x20, .. p.Q.X!, 0x22, 0x58, 0x20, .. p.Q.Y!];
    }

    /// <summary>Starts a sign-in: the challenge, and the cookie that names it.</summary>
    private static async Task<(string Challenge, string Cookie)> Begin(HttpClient browser)
    {
        var begun = await browser.PostAsync("/api/auth/passkey/begin", null);
        Assert.Equal(HttpStatusCode.OK, begun.StatusCode);
        var challenge = JsonDocument.Parse(await begun.Content.ReadAsStringAsync()).RootElement.GetProperty("challenge").GetString()!;
        var cookie = begun.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith(Product.PasskeyAssertionCookie + "=", StringComparison.Ordinal));
        return (challenge, cookie);
    }

    /// <summary>What the browser sends back: the authenticator's signature over its data and the client data that names the challenge and the origin.</summary>
    private string Assertion(string challenge)
    {
        var clientData = JsonSerializer.SerializeToUtf8Bytes(new { type = "webauthn.get", challenge, origin = Origin, crossOrigin = false });
        // the relying party's id hash, user present and verified, a counter of 0 (as a synced passkey reports)
        byte[] authenticatorData = [.. SHA256.HashData(Encoding.UTF8.GetBytes(new Uri(Origin).Host)), 0x05, 0, 0, 0, 0];
        var signature = _key.SignData([.. authenticatorData, .. SHA256.HashData(clientData)], HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var id = Base64Url.EncodeToString(_credentialId);
        return JsonSerializer.Serialize(new
        {
            id, rawId = id, type = "public-key",
            response = new { authenticatorData = Base64Url.EncodeToString(authenticatorData), clientDataJSON = Base64Url.EncodeToString(clientData), signature = Base64Url.EncodeToString(signature) },
            clientExtensionResults = new { },
        });
    }

    private static Task<HttpResponseMessage> Complete(HttpClient browser, string cookie, string assertion)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/passkey/complete") { Content = new StringContent(assertion, Encoding.UTF8, "application/json") };
        request.Headers.Add("Cookie", cookie);
        return browser.SendAsync(request);
    }

    private static bool SignedIn(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(c => c.StartsWith(Product.SessionCookie + "=", StringComparison.Ordinal) && c.Split(';')[0].Split('=', 2)[1].Length > 0);

    [Fact]
    public async Task A_sign_in_started_at_one_instance_finishes_at_another()
    {
        using var a = await Home();
        using var b = await Home();

        var (challenge, cookie) = await Begin(a);
        Assert.DoesNotContain(challenge, Uri.UnescapeDataString(cookie), StringComparison.Ordinal);   // the cookie names the challenge; it does not carry it
        var signedIn = await Complete(b, cookie, Assertion(challenge));
        Assert.Equal(HttpStatusCode.OK, signedIn.StatusCode);
        Assert.True(SignedIn(signedIn));
        Assert.Equal(_ada.Id, JsonDocument.Parse(await signedIn.Content.ReadAsStringAsync()).RootElement.GetProperty("userId").GetString());
    }

    [Fact]
    public async Task A_captured_response_signs_nobody_in_again()
    {
        using var a = await Home();
        using var b = await Home();
        var (challenge, cookie) = await Begin(a);
        var captured = Assertion(challenge);
        Assert.True(SignedIn(await Complete(a, cookie, captured)));

        // the same response with the same cookie, at the instance that took it and at another
        var again = await Complete(a, cookie, captured);
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.False(SignedIn(again));
        Assert.False(SignedIn(await Complete(b, cookie, captured)));

        // with a cookie of a new start: the challenge is the new one, which the old response does not answer
        var (_, fresh) = await Begin(b);
        var stale = await Complete(b, fresh, captured);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.False(SignedIn(stale));

        // with a cookie the caller wrote, carrying the old challenge as the cookie once did
        var written = Product.PasskeyAssertionCookie + "=" + Uri.EscapeDataString(JsonSerializer.Serialize(new { challenge, timeout = 60000, rpId = new Uri(Origin).Host, allowCredentials = Array.Empty<object>(), userVerification = "preferred" }));
        var forged = await Complete(a, written, captured);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.False(SignedIn(forged));
    }
}
