using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Connections.Http.Tests;

/// <summary>One request the fake API received: what a test says the connection sent. It keeps the headers the sign-in uses so a test can say a secret was in the right one and in no other.</summary>
public sealed record Seen(string Method, string Path, string Query, string? Authorization, string? ApiKey, string Body);

/// <summary>
/// A local API on a real socket (Kestrel on 127.0.0.1, any port), shaped as the systems the sign-in methods talk to: <c>/data</c> answers 200 for a request that signs in the way the fake expects and 401 otherwise, <c>/token</c> is an OAuth 2 token address for client credentials
/// (the client sent as Basic or in the form; a wrong client is 401; a token that expires after <see cref="TokenSeconds"/>), and <c>/limited</c>, <c>/boom</c>, <c>/missing</c> and <c>/bad</c> answer 429, 500, 404 and 400. Over TLS it can require a client certificate.
/// </summary>
public sealed class FakeApi : IAsyncDisposable
{
    private readonly WebApplication _app;
    private int _tokens;

    public const string ClientId = "client-1";
    public const string ClientSecret = "client-secret-canary-4c1d";
    public const string ApiKey = "api-key-canary-9b7e";
    public const string User = "ada";
    public const string Password = "password-canary-77aa";

    public ConcurrentQueue<Seen> Requests { get; } = new();

    /// <summary>The tokens the token address issued and still honours.</summary>
    public ConcurrentDictionary<string, bool> ValidTokens { get; } = new();

    public int TokenCalls { get; private set; }

    public int TokenSeconds { get; set; } = 3600;

    /// <summary>How <c>/data</c> checks the sign-in: <c>api-key</c>, <c>api-key-query</c>, <c>basic</c>, <c>bearer</c>, <c>cert</c> (any request that got through TLS) or <c>none</c>.</summary>
    public string Expect { get; set; } = "none";

    public string Prefix { get; set; } = "";

    public string? ClientCertificateThumbprint { get; }

    public string BaseAddress { get; }

    public FakeApi(X509Certificate2? serverCertificate = null, string? requiredClientThumbprint = null)
    {
        ClientCertificateThumbprint = requiredClientThumbprint;
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k =>
        {
            if (serverCertificate is null) k.Listen(IPAddress.Loopback, 0);
            else k.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(https =>
            {
                https.ServerCertificate = serverCertificate;
                https.ClientCertificateMode = requiredClientThumbprint is null ? ClientCertificateMode.NoCertificate : ClientCertificateMode.RequireCertificate;
                https.ClientCertificateValidation = (certificate, _, _) => requiredClientThumbprint is null || certificate.Thumbprint == requiredClientThumbprint;
            }));
        });
        _app = builder.Build();
        _app.Run(Handle);
        _app.StartAsync().GetAwaiter().GetResult();
        BaseAddress = _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
    }

    private async Task Handle(HttpContext context)
    {
        var request = context.Request;
        string body;
        using (var reader = new StreamReader(request.Body, Encoding.UTF8)) body = await reader.ReadToEndAsync();
        Requests.Enqueue(new Seen(request.Method, request.Path, request.QueryString.Value ?? "", request.Headers.Authorization.ToString() is { Length: > 0 } a ? a : null, request.Headers.TryGetValue("X-Api-Key", out var k) ? k.ToString() : null, body));

        switch (request.Path.Value)
        {
            case "/token": await Token(context, body); return;
            case "/data": await Data(context); return;
            case "/limited": context.Response.StatusCode = 429; context.Response.Headers.RetryAfter = "1"; return;
            case "/boom": context.Response.StatusCode = 500; return;
            case "/missing": context.Response.StatusCode = 404; return;
            case "/bad": context.Response.StatusCode = 400; return;
            case "/forbidden": context.Response.StatusCode = 403; return;
            default: context.Response.StatusCode = 404; return;
        }
    }

    private async Task Token(HttpContext context, string body)
    {
        TokenCalls++;
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1] : "").Replace('+', ' '), StringComparer.Ordinal);
        string? id = null, secret = null;
        var authorization = context.Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Basic ", StringComparison.Ordinal))
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(authorization[6..])).Split(':', 2);
            (id, secret) = (Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts.Length > 1 ? parts[1] : ""));
        }
        else { form.TryGetValue("client_id", out id); form.TryGetValue("client_secret", out secret); }
        if (form.GetValueOrDefault("grant_type") != "client_credentials" || id != ClientId || secret != ClientSecret)
        {
            context.Response.StatusCode = 401;
            await context.Response.WriteAsync("{\"error\":\"invalid_client\"}");
            return;
        }
        var token = "token-" + Interlocked.Increment(ref _tokens);
        ValidTokens[token] = true;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync($"{{\"access_token\":\"{token}\",\"token_type\":\"Bearer\",\"expires_in\":{TokenSeconds}}}");
    }

    private async Task Data(HttpContext context)
    {
        var h = context.Request.Headers;
        var ok = Expect switch
        {
            "api-key" => h["X-Api-Key"].ToString() == Prefix + ApiKey,
            "api-key-query" => context.Request.Query["key"].ToString() == ApiKey,
            "basic" => h.Authorization.ToString() == "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{User}:{Password}")),
            "bearer" => h.Authorization.ToString() is var a && a.StartsWith("Bearer ", StringComparison.Ordinal) && ValidTokens.TryGetValue(a[7..], out var valid) && valid,
            "cert" => context.Connection.ClientCertificate is not null || ClientCertificateThumbprint is null,
            _ => true,
        };
        context.Response.StatusCode = ok ? 200 : 401;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(ok ? "{\"ok\":true}" : "{\"error\":\"unauthorized\"}");
    }

    /// <summary>Makes the token address's answer for a token not valid any more: the system revoked it before it expired.</summary>
    public void Revoke(string token) => ValidTokens[token] = false;

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    // ---- certificates for the TLS story ----

    /// <summary>A self-signed certificate with a private key: a server's (its name is localhost) or a client's.</summary>
    public static X509Certificate2 SelfSigned(string subject, bool server)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={subject}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2")], false));
        if (server)
        {
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
        }
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx, "pw"), "pw");
    }
}
