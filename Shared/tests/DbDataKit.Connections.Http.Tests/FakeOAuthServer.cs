using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Connections.Http.Tests;

/// <summary>
/// A local OAuth 2 provider on a real socket (plan 27a IX03), shaped like the real ones: <c>/authorize</c> is where a person gives consent (a test calls <see cref="Consent"/> for the person: it gives back the address the browser would be sent to, with a code and the state),
/// <c>/token</c> exchanges a code (PKCE S256 checked) and refreshes a refresh token (the client signed in with Basic; a refresh token that was revoked or replaced is 400 <c>invalid_grant</c>), <c>/revoke</c> is RFC 7009, and <c>/data</c> answers 200 for a bearer token it issued and still honours.
/// Shared by the library's tests and the console's.
/// </summary>
public sealed class FakeOAuthServer : IAsyncDisposable
{
    public const string ClientId = "console-client";
    public const string ClientSecret = "oauth-client-secret-canary-31ac";

    private readonly WebApplication _app;
    private int _counter;
    private readonly ConcurrentDictionary<string, (string Challenge, string Redirect)> _codes = new();

    public string BaseAddress { get; }

    /// <summary>The refresh tokens that are good.</summary>
    public ConcurrentDictionary<string, bool> RefreshTokens { get; } = new();

    public ConcurrentDictionary<string, bool> AccessTokens { get; } = new();

    public int AccessTokenSeconds { get; set; } = 3600;

    /// <summary>A provider that rotates: every refresh gives a new refresh token and the old one stops working.</summary>
    public bool Rotate { get; set; }

    /// <summary>A provider that gives no refresh token (the scope did not ask for offline access).</summary>
    public bool GiveNoRefreshToken { get; set; }

    public int RefreshCalls { get; private set; }

    /// <summary>How many times the client signed in with client credentials (no person, no refresh token).</summary>
    public int ClientCredentialCalls { get; private set; }

    public ConcurrentQueue<string> Revoked { get; } = new();

    public ConcurrentQueue<string> Authorizations { get; } = new();

    public FakeOAuthServer()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        _app = builder.Build();
        _app.Run(Handle);
        _app.StartAsync().GetAwaiter().GetResult();
        BaseAddress = _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
    }

    /// <summary>The person signs in at the provider and agrees: the address the browser is sent back to (<c>redirect_uri</c>) with a code and the state that came with the consent request. Pass the address the connection's Authorize gave.</summary>
    public string Consent(string authorizeUrl)
    {
        var query = System.Web.HttpUtility.ParseQueryString(new Uri(authorizeUrl).Query);
        Authorizations.Enqueue(authorizeUrl);
        var code = "code-" + Interlocked.Increment(ref _counter);
        _codes[code] = (query["code_challenge"] ?? "", query["redirect_uri"] ?? "");
        return $"{query["redirect_uri"]}?code={code}&state={Uri.EscapeDataString(query["state"] ?? "")}";
    }

    /// <summary>The person takes consent back at the provider (or it expires): every refresh token stops working.</summary>
    public void RevokeEverything() { foreach (var k in RefreshTokens.Keys) RefreshTokens[k] = false; foreach (var k in AccessTokens.Keys) AccessTokens[k] = false; }

    private async Task Handle(HttpContext context)
    {
        string body;
        using (var reader = new StreamReader(context.Request.Body, Encoding.UTF8)) body = await reader.ReadToEndAsync();
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString((p.Length > 1 ? p[1] : "").Replace('+', ' ')), StringComparer.Ordinal);
        switch (context.Request.Path.Value)
        {
            case "/token": await Token(context, form); return;
            case "/revoke":
                if (!ClientOk(context, form)) { context.Response.StatusCode = 401; return; }
                if (form.TryGetValue("token", out var token)) { Revoked.Enqueue(token); RefreshTokens[token] = false; }
                context.Response.StatusCode = 200;
                return;
            case "/data":
                var a = context.Request.Headers.Authorization.ToString();
                var ok = a.StartsWith("Bearer ", StringComparison.Ordinal) && AccessTokens.TryGetValue(a[7..], out var live) && live;
                context.Response.StatusCode = ok ? 200 : 401;
                return;
            default: context.Response.StatusCode = 404; return;
        }
    }

    private static bool ClientOk(HttpContext context, Dictionary<string, string> form)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Basic ", StringComparison.Ordinal)) return false;
        var parts = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..])).Split(':', 2);
        return Uri.UnescapeDataString(parts[0]) == ClientId && Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : "") == ClientSecret;
    }

    private async Task Token(HttpContext context, Dictionary<string, string> form)
    {
        async Task Error(int status, string error) { context.Response.StatusCode = status; context.Response.ContentType = "application/json"; await context.Response.WriteAsync($"{{\"error\":\"{error}\"}}"); }
        if (!ClientOk(context, form)) { await Error(401, "invalid_client"); return; }
        string? refresh = null;
        switch (form.GetValueOrDefault("grant_type"))
        {
            case "authorization_code":
                if (!_codes.TryRemove(form.GetValueOrDefault("code") ?? "", out var issued)) { await Error(400, "invalid_grant"); return; }   // used twice, or never issued
                var verifier = form.GetValueOrDefault("code_verifier") ?? "";
                var challenge = Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
                if (challenge != issued.Challenge || form.GetValueOrDefault("redirect_uri") != issued.Redirect) { await Error(400, "invalid_grant"); return; }
                if (!GiveNoRefreshToken) { refresh = "refresh-" + Interlocked.Increment(ref _counter); RefreshTokens[refresh] = true; }
                break;
            case "refresh_token":
                RefreshCalls++;
                var given = form.GetValueOrDefault("refresh_token") ?? "";
                if (!RefreshTokens.TryGetValue(given, out var good) || !good) { await Error(400, "invalid_grant"); return; }
                if (Rotate) { RefreshTokens[given] = false; refresh = "refresh-" + Interlocked.Increment(ref _counter); RefreshTokens[refresh] = true; }
                break;
            case "client_credentials":
                ClientCredentialCalls++;
                break;
            default: await Error(400, "unsupported_grant_type"); return;
        }

        var access = "access-" + Interlocked.Increment(ref _counter);
        AccessTokens[access] = true;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($"{{\"access_token\":\"{access}\",\"token_type\":\"Bearer\",\"expires_in\":{AccessTokenSeconds}{(refresh is null ? "" : $",\"refresh_token\":\"{refresh}\"")}}}");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}
