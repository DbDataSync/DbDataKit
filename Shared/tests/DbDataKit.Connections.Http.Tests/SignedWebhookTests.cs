using System.Net;
using DbDataKit.Secrets;
using Microsoft.Extensions.Time.Testing;

namespace DbDataKit.Connections.Http.Tests;

/// <summary>Plan 22a GP18 fold: <c>sign_in.method: signed_webhook</c> signs the body of each request with HMAC-SHA256 and a timestamp; the secret is read for the request and is in no header but as the signature.</summary>
public sealed class SignedWebhookTests
{
    private const string Secret = "signing-secret-canary-31ac";

    private sealed class Capture : HttpMessageHandler
    {
        public List<(string Header, string Body)> Seen { get; } = [];

        public string HeaderName { get; init; } = SignedWebhook.DefaultHeader;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is { } c ? await c.ReadAsStringAsync(cancellationToken) : "";
            Seen.Add((request.Headers.TryGetValues(HeaderName, out var v) ? string.Join(",", v) : "", body));
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static ConnectionDefinition Connection(string signIn)
    {
        var r = ConnectionDefinitionLoader.Load($"schema: connection/2\nkind: http\nname: hook\nendpoint: https://hook.example.com\nwebhook: {{ format: signed }}\nsign_in: {{ {signIn} }}\n", "connections/hook.yml", outsideKinds: true);
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        return r.Definition!;
    }

    private static ISecretResolver Secrets() => new SecretResolverChain([new EnvSecretProvider(n => n == "SIGNING" ? Secret : null)]);

    [Fact]
    public async Task The_body_is_signed_with_the_clock_and_the_signature_verifies_only_for_that_body()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 11, 11, 10, 0, 0, TimeSpan.Zero));
        var capture = new Capture();
        using var client = HttpConnections.Create(Connection("method: signed_webhook, key_secret: env:SIGNING"), Secrets(), new HttpConnectionOptions(capture, clock));
        using var content = new StringContent("{\"a\":1}");
        (await client.Client.PostAsync("", content)).Dispose();
        var (header, body) = Assert.Single(capture.Seen);
        Assert.Equal("{\"a\":1}", body);
        Assert.Equal(SignedWebhook.Sign(Secret, body, clock.GetUtcNow()), header);
        Assert.StartsWith($"t={clock.GetUtcNow().ToUnixTimeSeconds()},v1=", header, StringComparison.Ordinal);
        Assert.NotEqual(SignedWebhook.Sign(Secret, "{\"a\":2}", clock.GetUtcNow()), header);
        Assert.DoesNotContain(Secret, header, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_header_name_can_be_chosen_and_an_unset_secret_is_a_classified_login_failure()
    {
        var capture = new Capture { HeaderName = "X-Hook" };
        using var client = HttpConnections.Create(Connection("method: signed_webhook, key_secret: env:SIGNING, name: X-Hook"), Secrets(), new HttpConnectionOptions(capture));
        using var content = new StringContent("x");
        (await client.Client.PostAsync("", content)).Dispose();
        Assert.StartsWith("t=", Assert.Single(capture.Seen).Header, StringComparison.Ordinal);

        using var unset = HttpConnections.Create(Connection("method: signed_webhook, key_secret: env:MISSING"), Secrets(), new HttpConnectionOptions(new Capture()));
        using var again = new StringContent("x");
        var e = await Assert.ThrowsAsync<SecretNotSetException>(() => unset.Client.PostAsync("", again));
        Assert.Equal(ConnectionFailureKind.Login, e.Failure.Kind);
    }

    [Fact]
    public void A_webhook_whose_address_is_a_secret_has_no_http_client_of_its_own()
    {
        var r = ConnectionDefinitionLoader.Load("schema: connection/2\nkind: http\nname: hook\nendpoint_secret: env:URL\nwebhook: { format: slack }\n", "c.yml", outsideKinds: true);
        Assert.True(r.Ok);
        var e = Assert.Throws<ArgumentException>(() => HttpConnections.Create(r.Definition!, Secrets()));
        Assert.Contains("endpoint_secret", e.Message, StringComparison.Ordinal);
    }
}
