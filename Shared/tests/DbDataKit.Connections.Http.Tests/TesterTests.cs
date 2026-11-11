using DbDataKit.Secrets;

namespace DbDataKit.Connections.Http.Tests;

/// <summary>Plan 27a IX02: the <b>Test</b> of a connection of kind http says whether the system was reached and whether it accepted the sign-in, in words, without a value.</summary>
public sealed class TesterTests
{
    private static ISecretResolver Secrets(string key = FakeApi.ApiKey) =>
        new SecretResolverChain([new EnvSecretProvider(name => name switch { "API_KEY" => key, "CLIENT_SECRET" => FakeApi.ClientSecret, _ => null })]);

    private static ConnectionDefinition Connection(string endpoint, string signIn, string extra = "") =>
        ConnectionDefinitionLoader.Load($"schema: connection/2\nkind: http\nname: crm\nendpoint: {endpoint}\n{signIn}{extra}", "connections/crm.yml", outsideKinds: true).Require();

    [Fact]
    public async Task A_reachable_system_that_accepts_the_sign_in_is_ok_and_says_so()
    {
        await using var api = new FakeApi { Expect = "api-key" };
        var result = await HttpConnectionTester.TestAsync(Connection(api.BaseAddress, "sign_in: { method: api_key, key_secret: env:API_KEY }\n", "test_path: /data\n"), Secrets());

        Assert.True(result.Ok);
        Assert.Equal((true, "accepted", 200, false), (result.Reached, result.SignIn, result.Status, result.Temporary));
        Assert.Contains("accepted the sign-in", result.Message, StringComparison.Ordinal);
        Assert.Equal("/data", api.Requests.Single().Path);
    }

    [Fact]
    public async Task A_system_that_refuses_the_sign_in_is_reached_and_rejected_and_is_permanent()
    {
        await using var api = new FakeApi { Expect = "api-key" };
        var result = await HttpConnectionTester.TestAsync(Connection(api.BaseAddress, "sign_in: { method: api_key, key_secret: env:API_KEY }\n", "test_path: /data\n"), Secrets("a-wrong-key"));

        Assert.False(result.Ok);
        Assert.Equal((true, "rejected", 401, ConnectionFailureKind.Login, false), (result.Reached, result.SignIn, result.Status, result.Kind, result.Temporary));
        Assert.Contains("did not accept the sign-in (401)", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("a-wrong-key", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuth_with_a_wrong_secret_is_rejected_at_the_token_address_and_the_endpoint_is_not_tried()
    {
        await using var api = new FakeApi { Expect = "bearer" };
        var wrong = new SecretResolverChain([new EnvSecretProvider(_ => "wrong-secret-canary")]);
        var result = await HttpConnectionTester.TestAsync(Connection(api.BaseAddress, $"sign_in:\n  method: oauth2_client_credentials\n  token_url: {api.BaseAddress}/token\n  client_id: client-1\n  client_secret_secret: env:CLIENT_SECRET\n", "test_path: /data\n"), wrong);

        Assert.False(result.Ok);
        Assert.Equal(("rejected", ConnectionFailureKind.Login), (result.SignIn, result.Kind));
        Assert.Contains("client id and its secret", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(api.Requests, r => r.Path == "/data");
        Assert.DoesNotContain("wrong-secret-canary", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OAuth_with_the_right_secret_gets_a_token_and_reads_the_endpoint()
    {
        await using var api = new FakeApi { Expect = "bearer" };
        var result = await HttpConnectionTester.TestAsync(Connection(api.BaseAddress, $"sign_in:\n  method: oauth2_client_credentials\n  token_url: {api.BaseAddress}/token\n  client_id: client-1\n  client_secret_secret: env:CLIENT_SECRET\n", "test_path: /data\n"), Secrets());
        Assert.True(result.Ok);
        Assert.Equal("accepted", result.SignIn);
        Assert.Equal(1, api.TokenCalls);
    }

    [Fact]
    public async Task A_secret_that_is_not_set_says_which_reference_and_nothing_is_sent()
    {
        await using var api = new FakeApi { Expect = "api-key" };
        var none = new SecretResolverChain([new EnvSecretProvider(_ => null)]);
        var result = await HttpConnectionTester.TestAsync(Connection(api.BaseAddress, "sign_in: { method: api_key, key_secret: env:API_KEY }\n"), none);

        Assert.False(result.Ok);
        Assert.Null(result.Reached);
        Assert.Equal("not checked", result.SignIn);
        Assert.Contains("`env:API_KEY` is not set", result.Message, StringComparison.Ordinal);
        Assert.Empty(api.Requests);
    }

    [Fact]
    public async Task A_system_that_is_not_there_is_unreachable_and_temporary()
    {
        var api = new FakeApi();
        var address = api.BaseAddress;
        await api.DisposeAsync();
        var result = await HttpConnectionTester.TestAsync(Connection(address, "", "timeouts: { connect_s: 15 }\n"), Secrets());

        Assert.False(result.Ok);
        Assert.Equal((false, ConnectionFailureKind.Network, true), (result.Reached, result.Kind, result.Temporary));
        Assert.Contains("could not be reached", result.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/missing", true, 404)]
    [InlineData("/limited", false, 429)]
    [InlineData("/boom", false, 500)]
    [InlineData("/forbidden", false, 403)]
    public async Task Other_answers_are_told_in_words(string path, bool ok, int status)
    {
        await using var api = new FakeApi();
        var result = await HttpConnectionTester.TestAsync(Connection(api.BaseAddress, "", $"test_path: {path}\n"), Secrets());
        Assert.Equal((ok, status), (result.Ok, result.Status));
        Assert.Contains(status.ToString(), result.Message, StringComparison.Ordinal);
        if (status == 404) Assert.Contains("test_path", result.Message, StringComparison.Ordinal);
        if (status is 429 or 500) Assert.True(result.Temporary);
    }

    [Fact]
    public async Task A_server_certificate_that_is_not_trusted_is_told_with_the_way_out()
    {
        using var server = FakeApi.SelfSigned("localhost", server: true);
        await using var api = new FakeApi(server);
        var result = await HttpConnectionTester.TestAsync(Connection(api.BaseAddress, ""), Secrets());
        Assert.False(result.Ok);
        Assert.Equal(ConnectionFailureKind.Tls, result.Kind);
        Assert.Contains("tls.ca_file", result.Message, StringComparison.Ordinal);
    }
}
