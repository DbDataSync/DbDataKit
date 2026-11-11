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
        var r = ConnectionDefinitionLoader.LoadConnectionOfAnyKind($"schema: connection/2\nkind: http\nname: hook\nendpoint: https://hook.example.com\nwebhook: {{ format: signed }}\nsign_in: {{ {signIn} }}\n", "connections/hook.yml");
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
    public async Task A_template_address_is_composed_inside_the_call_and_redacted_everywhere_else()
    {
        var capture = new UrlCapture();
        var secrets = new SecretResolverChain([new EnvSecretProvider(n => n switch { "TOKEN" => "T000/B000/CANARYtoken9", "SIG" => "sig+canary/value=", _ => null })]);
        var r = ConnectionDefinitionLoader.LoadConnectionOfAnyKind("schema: connection/2\nkind: http\nname: hook\nendpoint: \"https://hooks.example.com/services/{token}?api-version=1&sig={sig}\"\nendpoint_secrets: { token: env:TOKEN, sig: env:SIG }\nwebhook: { format: slack }\n", "c.yml");
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        using var client = HttpConnections.Create(r.Definition!, secrets, new HttpConnectionOptions(capture));
        (await client.Client.GetAsync("")).Dispose();
        Assert.Equal("https://hooks.example.com/services/T000/B000/CANARYtoken9?api-version=1&sig=sig%2Bcanary%2Fvalue%3D", capture.Seen.Single());

        var failure = $"The request to {capture.Seen.Single()} failed (T000/B000/CANARYtoken9, sig+canary/value=, CANARYtoken9)";
        var shown = client.Redact(failure);
        foreach (var part in new[] { "CANARYtoken9", "sig%2Bcanary", "sig+canary", "T000/B000" }) Assert.DoesNotContain(part, shown, StringComparison.Ordinal);
        Assert.Contains("***", shown, StringComparison.Ordinal);

        // the Test result shows the template, never the composed address
        var test = await HttpConnectionTester.TestAsync(r.Definition!, secrets, new HttpConnectionOptions(capture));
        Assert.Contains("https://hooks.example.com/services/{token}", test.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARYtoken9", test.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("canary", test.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_whole_address_secret_is_called_and_shown_as_hidden()
    {
        var capture = new UrlCapture();
        var secrets = new SecretResolverChain([new EnvSecretProvider(n => n == "URL" ? "https://hook.example.com/in/CANARYwhole1234" : null)]);
        var r = ConnectionDefinitionLoader.LoadConnectionOfAnyKind("schema: connection/2\nkind: http\nname: hook\nendpoint: env:URL\nwebhook: { format: slack }\n", "c.yml");
        Assert.True(r.Ok, string.Join("\n", r.Diagnostics));
        var test = await HttpConnectionTester.TestAsync(r.Definition!, secrets, new HttpConnectionOptions(capture));
        Assert.Equal("https://hook.example.com/in/CANARYwhole1234", capture.Seen.Single());
        Assert.Contains(EndpointTemplate.Hidden, test.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("CANARYwhole1234", test.Message, StringComparison.Ordinal);
    }

    private sealed class UrlCapture : HttpMessageHandler
    {
        public List<string> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
