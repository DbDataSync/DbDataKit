using System.Net;
using DbDataKit.Secrets;
using Microsoft.Extensions.Time.Testing;

namespace DbDataKit.Connections.Http.Tests;

/// <summary>
/// Plan 27a IX02 against a local API on a real socket: each sign-in method authenticates; an expired OAuth 2 token is refreshed once (by the clock, and when the system revokes it early); a wrong secret classifies as a permanent login failure and is not retried; a secret that is
/// not set says so; the canary secrets are in no failure, message or test result.
/// </summary>
public sealed class SignInTests
{
    private static readonly Dictionary<string, string> Env = new()
    {
        ["API_KEY"] = FakeApi.ApiKey, ["PASSWORD"] = FakeApi.Password, ["CLIENT_SECRET"] = FakeApi.ClientSecret, ["WRONG"] = "not-the-secret-canary",
    };

    private static ISecretResolver Secrets(Dictionary<string, string>? env = null) =>
        new SecretResolverChain([new EnvSecretProvider(name => (env ?? Env).GetValueOrDefault(name))]);

    private static ConnectionDefinition Connection(FakeApi api, string signIn, string extra = "")
    {
        var yaml = $"schema: connection/2\nkind: http\nname: crm\nendpoint: {api.BaseAddress}\n{signIn}{extra}";
        var result = ConnectionDefinitionLoader.Load(yaml, "connections/crm.yml", outsideKinds: true);
        Assert.True(result.Ok, string.Join("\n", result.Diagnostics));
        return result.Definition!;
    }

    private static async Task<HttpStatusCode> Get(HttpConnectionClient c, string path = "data") => (await c.Client.GetAsync(path)).StatusCode;

    [Fact]
    public async Task An_api_key_in_a_header_with_a_prefix_and_in_the_query_authenticates_and_is_sent_nowhere_else()
    {
        await using var api = new FakeApi { Expect = "api-key" };
        using (var plain = HttpConnections.Create(Connection(api, "sign_in: { method: api_key, key_secret: env:API_KEY }\n"), Secrets()))
        {
            Assert.Equal(HttpStatusCode.OK, await Get(plain));
        }
        api.Expect = "api-key";
        api.Prefix = "Token ";
        using (var prefixed = HttpConnections.Create(Connection(api, "sign_in: { method: api_key, key_secret: env:API_KEY, name: X-Api-Key, prefix: 'Token ' }\n"), Secrets()))
        {
            Assert.Equal(HttpStatusCode.OK, await Get(prefixed));
        }
        api.Expect = "api-key-query";
        using (var query = HttpConnections.Create(Connection(api, "sign_in: { method: api_key, key_secret: env:API_KEY, in: query, name: key }\n"), Secrets()))
        {
            Assert.Equal(HttpStatusCode.OK, await Get(query));
            Assert.Equal(HttpStatusCode.OK, await Get(query, "data?page=2"));
        }
        // the key was in the header (or the query) of each request and not in a body
        Assert.All(api.Requests, r => Assert.DoesNotContain(FakeApi.ApiKey, r.Body));
        Assert.Contains(api.Requests, r => r.Query.Contains("page=2", StringComparison.Ordinal) && r.Query.Contains("key=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Basic_authenticates()
    {
        await using var api = new FakeApi { Expect = "basic" };
        using var connection = HttpConnections.Create(Connection(api, "sign_in: { method: basic, user: ada, password_secret: env:PASSWORD }\n"), Secrets());
        Assert.Equal(HttpStatusCode.OK, await Get(connection));
        Assert.StartsWith("Basic ", api.Requests.Last().Authorization, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("basic")]
    [InlineData("body")]
    public async Task OAuth_client_credentials_gets_a_token_once_and_reuses_it(string clientAuth)
    {
        await using var api = new FakeApi { Expect = "bearer" };
        using var connection = HttpConnections.Create(Connection(api, $"sign_in:\n  method: oauth2_client_credentials\n  token_url: {api.BaseAddress}/token\n  client_id: client-1\n  client_secret_secret: env:CLIENT_SECRET\n  scope: read\n  client_auth: {clientAuth}\n"), Secrets());

        Assert.Equal(HttpStatusCode.OK, await Get(connection));
        Assert.Equal(HttpStatusCode.OK, await Get(connection));
        Assert.Equal(HttpStatusCode.OK, await Get(connection));

        Assert.Equal(1, api.TokenCalls);
        Assert.Equal(1, connection.TokenRequests);
        var token = api.Requests.Single(r => r.Path == "/token");
        Assert.Contains("scope=read", token.Body, StringComparison.Ordinal);
        Assert.Equal(clientAuth == "basic", token.Authorization?.StartsWith("Basic ", StringComparison.Ordinal) == true);
        Assert.Equal(clientAuth == "body", token.Body.Contains("client_secret=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_expired_token_is_refreshed_once_by_the_clock_and_callers_that_meet_the_expiry_together_share_the_refresh()
    {
        await using var api = new FakeApi { Expect = "bearer", TokenSeconds = 120 };
        var time = new FakeTimeProvider();
        using var connection = HttpConnections.Create(Connection(api, $"sign_in:\n  method: oauth2_client_credentials\n  token_url: {api.BaseAddress}/token\n  client_id: client-1\n  client_secret_secret: env:CLIENT_SECRET\n"), Secrets(), new HttpConnectionOptions(Time: time));

        Assert.Equal(HttpStatusCode.OK, await Get(connection));
        time.Advance(TimeSpan.FromSeconds(60));                       // still good: 120 s, less the 30 s kept in hand
        Assert.Equal(HttpStatusCode.OK, await Get(connection));
        Assert.Equal(1, api.TokenCalls);

        time.Advance(TimeSpan.FromSeconds(61));                       // past it
        var together = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Get(connection)));
        Assert.All(together, s => Assert.Equal(HttpStatusCode.OK, s));
        Assert.Equal(2, api.TokenCalls);                              // refreshed once, not eight times
        Assert.Equal(2, connection.TokenRequests);
    }

    [Fact]
    public async Task A_token_the_system_revoked_early_is_refreshed_once_and_the_request_sent_again_with_its_body()
    {
        await using var api = new FakeApi { Expect = "bearer" };
        using var connection = HttpConnections.Create(Connection(api, $"sign_in:\n  method: oauth2_client_credentials\n  token_url: {api.BaseAddress}/token\n  client_id: client-1\n  client_secret_secret: env:CLIENT_SECRET\n"), Secrets());
        Assert.Equal(HttpStatusCode.OK, await Get(connection));
        api.Revoke("token-1");

        var response = await connection.Client.PostAsync("data", new StringContent("{\"n\":1}"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, api.TokenCalls);
        var posts = api.Requests.Where(r => r.Method == "POST" && r.Path == "/data").ToList();
        Assert.Equal(2, posts.Count);                                  // refused once, then accepted
        Assert.All(posts, p => Assert.Equal("{\"n\":1}", p.Body));     // the body went again
        Assert.Equal(["Bearer token-1", "Bearer token-2"], posts.Select(p => p.Authorization));
    }

    [Fact]
    public async Task A_fresh_token_that_is_still_refused_is_not_retried_in_a_loop()
    {
        await using var api = new FakeApi { Expect = "basic" };       // the data address wants basic: a bearer token is never right
        using var connection = HttpConnections.Create(Connection(api, $"sign_in:\n  method: oauth2_client_credentials\n  token_url: {api.BaseAddress}/token\n  client_id: client-1\n  client_secret_secret: env:CLIENT_SECRET\n"), Secrets());

        Assert.Equal(HttpStatusCode.Unauthorized, await Get(connection));
        Assert.Equal(1, api.TokenCalls);                              // the first token was fresh: no second one was asked for
        Assert.Single(api.Requests, r => r.Path == "/data");
    }

    [Fact]
    public async Task A_wrong_secret_is_a_permanent_login_failure_and_nothing_is_retried()
    {
        await using var api = new FakeApi { Expect = "bearer" };
        using var oauth = HttpConnections.Create(Connection(api, $"sign_in:\n  method: oauth2_client_credentials\n  token_url: {api.BaseAddress}/token\n  client_id: client-1\n  client_secret_secret: env:WRONG\n"), Secrets());
        var refused = await Assert.ThrowsAsync<ConnectionFailureException>(() => Get(oauth));
        Assert.Equal(ConnectionFailureKind.Login, refused.Failure.Kind);
        Assert.False(refused.Failure.IsTemporary);
        Assert.Equal("oauth-401", refused.Failure.Code);
        Assert.Equal(1, api.TokenCalls);
        Assert.DoesNotContain(api.Requests, r => r.Path == "/data");   // the data address was never called without a token

        api.Expect = "api-key";
        using var key = HttpConnections.Create(Connection(api, "sign_in: { method: api_key, key_secret: env:WRONG }\n"), Secrets());
        var response = await key.Client.GetAsync("data");
        var failure = HttpFailures.Classify(response.StatusCode);
        Assert.Equal((HttpStatusCode.Unauthorized, ConnectionFailureKind.Login, false, "http-401"), (response.StatusCode, failure.Kind, failure.IsTemporary, failure.Code));
        Assert.Throws<ConnectionFailureException>(() => HttpFailures.ThrowIfFailed(response));
    }

    [Fact]
    public async Task A_secret_that_is_not_set_is_a_permanent_login_failure_that_names_the_reference_only()
    {
        await using var api = new FakeApi { Expect = "api-key" };
        using var connection = HttpConnections.Create(Connection(api, "sign_in: { method: api_key, key_secret: env:NOT_SET }\n"), Secrets());

        var unset = await Assert.ThrowsAsync<SecretNotSetException>(() => Get(connection));

        Assert.Equal("env:NOT_SET", unset.Reference);
        Assert.Contains("`env:NOT_SET` is not set", unset.Message, StringComparison.Ordinal);
        Assert.Equal((ConnectionFailureKind.Login, false), (HttpFailures.Classify(unset).Kind, HttpFailures.Classify(unset).IsTemporary));
        Assert.Empty(api.Requests);                                    // nothing was sent without the key
    }

    [Fact]
    public async Task A_client_certificate_from_a_file_or_a_secret_authenticates_and_a_connection_without_it_is_refused_by_the_handshake()
    {
        using var server = FakeApi.SelfSigned("localhost", server: true);
        using var client = FakeApi.SelfSigned("crm-client", server: false);
        var pfx = client.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Pfx, "pw");
        var file = Path.Combine(Path.GetTempPath(), $"ddk-client-{Guid.NewGuid():N}.pfx");
        await File.WriteAllBytesAsync(file, pfx);
        try
        {
            await using var api = new FakeApi(server, client.Thumbprint) { Expect = "cert" };
            var env = new Dictionary<string, string>(Env) { ["PFX"] = Convert.ToBase64String(pfx), ["PFX_PASSWORD"] = "pw" };
            var tls = "tls: { verify: false }\n";

            using (var fromFile = HttpConnections.Create(Connection(api, $"sign_in: {{ method: client_certificate, certificate_file: '{file}', certificate_password_secret: env:PFX_PASSWORD }}\n", tls), Secrets(env)))
                Assert.Equal(HttpStatusCode.OK, await Get(fromFile));
            using (var fromSecret = HttpConnections.Create(Connection(api, "sign_in: { method: client_certificate, certificate_secret: env:PFX, certificate_password_secret: env:PFX_PASSWORD }\n", tls), Secrets(env)))
                Assert.Equal(HttpStatusCode.OK, await Get(fromSecret));

            // no certificate: the server ends the handshake, and that is a failure of the connection, not an answer
            using var without = HttpConnections.Create(Connection(api, "", tls), Secrets());
            var failed = await Record.ExceptionAsync(() => Get(without));
            Assert.NotNull(failed);
            var failure = HttpFailures.Classify(failed);
            Assert.Contains(failure.Kind, new[] { ConnectionFailureKind.Tls, ConnectionFailureKind.Network });

            // a wrong password for the file is a classified TLS failure that does not repeat the path or the password
            var broken = Assert.Throws<ConnectionFailureException>(() => HttpConnections.Create(Connection(api, $"sign_in: {{ method: client_certificate, certificate_file: '{file}', certificate_password_secret: env:WRONG }}\n", tls), Secrets(env)));
            Assert.Equal((ConnectionFailureKind.Tls, "client-certificate"), (broken.Failure.Kind, broken.Failure.Code));
            Assert.DoesNotContain(file, broken.Message, StringComparison.Ordinal);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task A_server_certificate_that_is_not_trusted_is_a_tls_failure_unless_the_connection_says_otherwise()
    {
        using var server = FakeApi.SelfSigned("localhost", server: true);
        await using var api = new FakeApi(server);
        using var strict = HttpConnections.Create(Connection(api, ""), Secrets());
        var failed = await Record.ExceptionAsync(() => Get(strict));
        Assert.Equal(ConnectionFailureKind.Tls, HttpFailures.Classify(failed!).Kind);

        using var lenient = HttpConnections.Create(Connection(api, "", "tls: { verify: false }\n"), Secrets());
        Assert.Equal(HttpStatusCode.OK, await Get(lenient));
    }

    [Theory]
    [InlineData("limited", ConnectionFailureKind.RateLimited, true)]
    [InlineData("boom", ConnectionFailureKind.Unavailable, true)]
    [InlineData("missing", ConnectionFailureKind.NotFound, false)]
    [InlineData("bad", ConnectionFailureKind.Syntax, false)]
    [InlineData("forbidden", ConnectionFailureKind.Permission, false)]
    public async Task A_status_is_classified_as_temporary_or_permanent(string path, ConnectionFailureKind kind, bool temporary)
    {
        await using var api = new FakeApi();
        using var connection = HttpConnections.Create(Connection(api, ""), Secrets());
        var response = await connection.Client.GetAsync(path);
        var failure = Assert.Throws<ConnectionFailureException>(() => HttpFailures.ThrowIfFailed(response)).Failure;
        Assert.Equal((kind, temporary), (failure.Kind, failure.IsTemporary));
        Assert.StartsWith("http-", failure.Code, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_system_that_is_not_there_and_one_that_is_too_slow_are_temporary()
    {
        var api = new FakeApi();
        var address = api.BaseAddress;
        await api.DisposeAsync();                                      // nothing listens there now
        var definition = ConnectionDefinitionLoader.Load($"schema: connection/2\nkind: http\nname: gone\nendpoint: {address}\ntimeouts: {{ connect_s: 15 }}\n", "c.yml", outsideKinds: true).Definition!;
        using var gone = HttpConnections.Create(definition, Secrets());
        var failed = await Record.ExceptionAsync(() => Get(gone));
        var failure = HttpFailures.Classify(failed!);
        Assert.Equal((ConnectionFailureKind.Network, true), (failure.Kind, failure.IsTemporary));

        var timeout = HttpFailures.Classify(new TaskCanceledException("x", new TimeoutException()));
        Assert.Equal((ConnectionFailureKind.Timeout, true), (timeout.Kind, timeout.IsTemporary));
        Assert.Equal(ConnectionFailureKind.Other, HttpFailures.Classify(new OperationCanceledException(), new CancellationToken(true)).Kind);
    }

    [Fact]
    public async Task No_secret_is_in_a_failure_a_message_or_a_test_result()
    {
        await using var api = new FakeApi { Expect = "api-key" };
        var seen = new List<string>();
        using var wrong = HttpConnections.Create(Connection(api, "sign_in: { method: api_key, key_secret: env:WRONG }\n"), Secrets());
        var response = await wrong.Client.GetAsync("data");
        seen.Add(Assert.Throws<ConnectionFailureException>(() => HttpFailures.ThrowIfFailed(response)).ToString());
        seen.Add(HttpFailures.Classify(response.StatusCode).PublicMessage);

        foreach (var signIn in new[]
        {
            "sign_in: { method: api_key, key_secret: env:WRONG }\n",
            "sign_in: { method: basic, user: ada, password_secret: env:WRONG }\n",
            $"sign_in:\n  method: oauth2_client_credentials\n  token_url: {api.BaseAddress}/token\n  client_id: client-1\n  client_secret_secret: env:WRONG\n",
            "sign_in: { method: api_key, key_secret: env:NOT_SET }\n",
        })
        {
            var result = await HttpConnectionTester.TestAsync(Connection(api, signIn), Secrets());
            seen.Add(result.ToString());
            seen.Add(result.Message);
        }
        var all = string.Join('\n', seen);
        foreach (var canary in new[] { FakeApi.ApiKey, FakeApi.Password, FakeApi.ClientSecret, "not-the-secret-canary" })
            Assert.DoesNotContain(canary, all, StringComparison.Ordinal);
    }
}
