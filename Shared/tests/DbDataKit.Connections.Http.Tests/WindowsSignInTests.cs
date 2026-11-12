using System.Collections.Concurrent;
using System.Net;
using DbDataKit.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Connections.Http.Tests;

/// <summary>
/// The Windows sign-in of a <c>report-catalog</c> connection (owner decision 2026-11-12): <c>windows</c> (user and password as a secret) and <c>integrated</c> (the identity of the process), NTLM or Negotiate. A faithful NTLM or Kerberos exchange needs a domain, so these tests check what can be
/// checked here: the handler is given the right credential (and only for NTLM and Negotiate), the fake server's challenge is answered with a Windows scheme and never with Basic, and the Test says what to do. Against a real SSRS and on a Windows host it is unverified.
/// </summary>
public sealed class WindowsSignInTests
{
    private static ISecretResolver Secrets() => new SecretResolverChain([new EnvSecretProvider(name => name == "CAT_PW" ? "pw-canary-31f" : null)]);

    private static ConnectionDefinition Catalog(string endpoint, string signIn) =>
        ConnectionDefinitionLoader.LoadConnectionOfAnyKind($"schema: connection/2\nkind: report-catalog\nname: reports\nendpoint: {endpoint}\nreport_catalog: {{ provider: ssrs }}\n{signIn}", "connections/reports.yml").Require();

    private const string Windows = "sign_in: { method: windows, user: 'CORP\\svc-reports', password_secret: env:CAT_PW }\n";
    private const string Integrated = "sign_in: { method: integrated }\n";

    [Fact]
    public void Windows_gives_the_handler_the_user_and_the_password_for_NTLM_and_Negotiate_and_never_for_Basic()
    {
        var d = Catalog("https://reports.example.com/Reports", Windows);
        var credentials = HttpConnections.WindowsCredentials(d, "https://reports.example.com/Reports", d.SignIn!, Secrets())!;
        var portal = new Uri("https://reports.example.com/Reports/api/v2.0/CatalogItems");
        foreach (var scheme in new[] { "NTLM", "Negotiate" })
        {
            var c = credentials.GetCredential(portal, scheme)!;
            Assert.Equal(("svc-reports", "CORP", "pw-canary-31f"), (c.UserName, c.Domain, c.Password));
        }

        Assert.Null(credentials.GetCredential(portal, "Basic"));
        Assert.Null(credentials.GetCredential(new Uri("https://elsewhere.example.com/"), "NTLM"));
    }

    [Fact]
    public void Integrated_uses_the_identity_the_process_runs_as_and_holds_no_secret()
    {
        var d = Catalog("https://reports.example.com/Reports", Integrated);
        var credentials = HttpConnections.WindowsCredentials(d, "https://reports.example.com/Reports", d.SignIn!, new SecretResolverChain([]))!;
        var portal = new Uri("https://reports.example.com/Reports/");
        Assert.Same(CredentialCache.DefaultNetworkCredentials, credentials.GetCredential(portal, "Negotiate"));
        Assert.Same(CredentialCache.DefaultNetworkCredentials, credentials.GetCredential(portal, "NTLM"));
        Assert.Null(credentials.GetCredential(portal, "Basic"));
    }

    [Fact]
    public void The_report_server_address_on_another_host_is_given_the_credential_too_and_other_sign_ins_none()
    {
        var d = ConnectionDefinitionLoader.LoadConnectionOfAnyKind($"schema: connection/2\nkind: report-catalog\nname: reports\nendpoint: https://portal.example.com/Reports\nreport_catalog: {{ provider: ssrs, report_server_address: 'https://rs.example.com/ReportServer' }}\n{Integrated}", "connections/reports.yml").Require();
        var credentials = HttpConnections.WindowsCredentials(d, "https://portal.example.com/Reports", d.SignIn!, Secrets())!;
        Assert.NotNull(credentials.GetCredential(new Uri("https://rs.example.com/ReportServer/x"), "NTLM"));
        Assert.NotNull(credentials.GetCredential(new Uri("https://portal.example.com/Reports/x"), "NTLM"));
        var basic = Catalog("https://reports.example.com/Reports", "sign_in: { method: basic, user: svc, password_secret: env:CAT_PW }\n");
        Assert.Null(HttpConnections.WindowsCredentials(basic, "https://reports.example.com/Reports", basic.SignIn!, Secrets()));
    }

    [Fact]
    public void A_password_secret_that_is_not_set_is_named_and_nothing_is_made()
    {
        var d = Catalog("https://reports.example.com/Reports", Windows);
        var e = Assert.Throws<SecretNotSetException>(() => HttpConnections.Create(d, new SecretResolverChain([])));
        Assert.Equal("env:CAT_PW", e.Reference);
    }

    [Fact]
    public async Task A_server_that_challenges_with_NTLM_is_answered_with_an_NTLM_or_Negotiate_message_and_the_password_is_not_sent_in_the_clear()
    {
        await using var server = new ChallengeServer(accept: true);
        using var connection = HttpConnections.Create(Catalog(server.Address + "/Reports", Windows), Secrets());
        using var response = await connection.Client.GetAsync("api/v2.0/CatalogItems");

        // a host with the machinery (Windows, or Linux with gss-ntlmssp or a Kerberos ticket) completes the first step and the server accepts; a host without it (a plain Linux container, this one) cannot start the handshake
        // and the 401 stays: then the test checks the same thing the other way, that nothing but a Windows scheme was ever sent
        if (server.Authorizations.IsEmpty) Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.All(server.Authorizations, a => Assert.True(a.StartsWith("NTLM ", StringComparison.Ordinal) || a.StartsWith("Negotiate ", StringComparison.Ordinal), "a Windows scheme only"));
        }

        Assert.DoesNotContain(server.Authorizations, a => a.StartsWith("Basic", StringComparison.OrdinalIgnoreCase));
        Assert.All(server.Authorizations, a => Assert.DoesNotContain("pw-canary-31f", a, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_server_that_asks_for_Basic_only_is_not_given_the_Windows_password()
    {
        await using var server = new ChallengeServer(accept: false, scheme: "Basic realm=\"x\"");
        using var connection = HttpConnections.Create(Catalog(server.Address + "/Reports", Windows), Secrets());
        using var response = await connection.Client.GetAsync("api/v2.0/CatalogItems");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(server.Authorizations);
    }

    [Fact]
    public async Task Test_says_a_refused_Windows_sign_in_in_words_and_on_Linux_names_the_packages()
    {
        await using var server = new ChallengeServer(accept: false);
        var result = await HttpConnectionTester.TestAsync(Catalog(server.Address + "/Reports", Windows), Secrets());

        Assert.False(result.Ok);
        Assert.Equal(("rejected", ConnectionFailureKind.Login, 401), (result.SignIn, result.Kind, result.Status));
        Assert.Contains("Windows sign-in", result.Message, StringComparison.Ordinal);
        if (!OperatingSystem.IsWindows()) Assert.Contains("gss-ntlmssp", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pw-canary-31f", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Test_of_a_server_that_accepts_is_ok_or_says_the_handshake_is_unavailable_and_a_missing_password_secret_is_named()
    {
        await using var server = new ChallengeServer(accept: true);
        var ok = await HttpConnectionTester.TestAsync(Catalog(server.Address + "/Reports", Windows), Secrets());
        // as above: where the handshake cannot start (no GSSAPI NTLM or Kerberos on the host) the Test says what to install instead
        if (server.Authorizations.IsEmpty) Assert.Contains(OperatingSystem.IsWindows() ? "Check the user" : "gss-ntlmssp", ok.Message, StringComparison.Ordinal);
        else { Assert.True(ok.Ok, ok.Message); Assert.Equal("accepted", ok.SignIn); }

        var unset = await HttpConnectionTester.TestAsync(Catalog(server.Address + "/Reports", Windows), new SecretResolverChain([]));
        Assert.False(unset.Ok);
        Assert.Contains("env:CAT_PW", unset.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("windows", false, "gss-ntlmssp")]
    [InlineData("integrated", false, "kinit")]
    [InlineData("windows", true, "DOMAIN")]
    [InlineData("integrated", true, "Kerberos")]
    public void The_hint_for_a_refused_Windows_sign_in_depends_on_the_platform_and_the_mode(string method, bool windows, string expected) =>
        Assert.Contains(expected, HttpConnectionTester.WindowsHint(method, windows), StringComparison.Ordinal);

    /// <summary>A local server that answers 401 with <c>WWW-Authenticate: NTLM</c> and <c>Negotiate</c> (or the scheme given) to a request that has no Authorization, and, when it accepts, 200 to one that has an NTLM or Negotiate header.</summary>
    private sealed class ChallengeServer : IAsyncDisposable
    {
        private readonly WebApplication _app;

        public ConcurrentQueue<string> Authorizations { get; } = new();

        public string Address { get; }

        public ChallengeServer(bool accept, string? scheme = null)
        {
            var builder = WebApplication.CreateSlimBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            _app = builder.Build();
            _app.Run(async context =>
            {
                var authorization = context.Request.Headers.Authorization.ToString();
                if (authorization.Length > 0) Authorizations.Enqueue(authorization);
                if (accept && (authorization.StartsWith("NTLM ", StringComparison.Ordinal) || authorization.StartsWith("Negotiate ", StringComparison.Ordinal)))
                {
                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync("{\"value\":[]}");
                    return;
                }

                context.Response.StatusCode = 401;
                if (scheme is not null) context.Response.Headers.Append("WWW-Authenticate", scheme);
                else { context.Response.Headers.Append("WWW-Authenticate", "Negotiate"); context.Response.Headers.Append("WWW-Authenticate", "NTLM"); }
            });
            _app.StartAsync().GetAwaiter().GetResult();
            Address = _app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!.Addresses.First();
        }

        public async ValueTask DisposeAsync() => await _app.DisposeAsync();
    }
}
