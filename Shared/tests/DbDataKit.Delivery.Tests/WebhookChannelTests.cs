using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Delivery.Webhooks;
using DbDataKit.State.Tests;
using Json.Schema;

namespace DbDataKit.Delivery.Tests;

/// <summary>A request the local listener took.</summary>
public sealed record HttpSeen(string Method, string Path, string Body, Dictionary<string, string> Headers);

/// <summary>An HTTP server in the test's process (an <see cref="HttpListener"/> on the loopback address) that answers as it is told.</summary>
public sealed class LocalHttp : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _loop;

    public LocalHttp(Func<HttpSeen, (int Status, string Body, Dictionary<string, string>? Headers)>? answer = null)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        Port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        Answer = answer ?? (_ => (200, "ok", null));
        _loop = Task.Run(async () =>
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                var request = context.Request;
                var body = await new StreamReader(request.InputStream, Encoding.UTF8).ReadToEndAsync();
                var seen = new HttpSeen(request.HttpMethod, request.Url!.AbsolutePath, body, request.Headers.AllKeys.ToDictionary(k => k!, k => request.Headers[k]!, StringComparer.OrdinalIgnoreCase));
                lock (Seen) Seen.Add(seen);
                var (status, text, headers) = Answer(seen);
                if (status == 599) { await Task.Delay(5000); }
                context.Response.StatusCode = status == 599 ? 200 : status;
                foreach (var (k, v) in headers ?? []) context.Response.Headers[k] = v;
                var bytes = Encoding.UTF8.GetBytes(text);
                try { await context.Response.OutputStream.WriteAsync(bytes); context.Response.Close(); } catch (Exception) { /* the client gave up */ }
            }
        });
    }

    public int Port { get; }

    public Func<HttpSeen, (int Status, string Body, Dictionary<string, string>? Headers)> Answer { get; set; }

    public List<HttpSeen> Seen { get; } = [];

    public string Url(string path = "/hook/T000/B000/XXXXtokenXXXX") => $"http://127.0.0.1:{Port}{path}";

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* stopped */ }
    }
}

/// <summary>Plan 22a GP09: the Teams, Slack and signed webhook channels against a local HTTP listener.</summary>
public sealed class WebhookChannelTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T09:00:00Z");
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
    }

    private LocalHttp Http(Func<HttpSeen, (int, string, Dictionary<string, string>?)>? answer = null)
    {
        var http = new LocalHttp(answer);
        _disposables.Add(http);
        return http;
    }

    private static IDeliveryChannel Channel(string kind, string url, string[]? allow = null, int? timeout = null, SecretScrubber? scrubber = null, TimeProvider? time = null)
    {
        var json = new JsonObject { ["kind"] = kind, ["url"] = "secret:hooks/url" };
        if (allow is not null) json["allow_private"] = new JsonArray([.. allow.Select(a => (JsonNode?)JsonValue.Create(a))]);
        if (kind == "webhook") json["secret"] = "secret:hooks/signing";
        if (timeout is not null) json["timeout_seconds"] = timeout;
        var settings = DeliverySettings.Read(new JsonObject { ["channels"] = new JsonObject { ["hook"] = json } }, "server.yaml", DeliveryKinds.Default());
        var resolver = new MapResolver(new() { ["secret:hooks/url"] = url, ["secret:hooks/signing"] = "shared-signing-secret" });
        return new DeliveryChannels(settings, DeliveryKinds.Default(), resolver, scrubber ?? new SecretScrubber(), time ?? new StateTestClock(Now)).Find("hook")!;
    }

    private static DeliveryPayload Payload(params DeliveryAttachment[] attachments) =>
        new(Guid.Parse("0197f2c4-1111-7000-8000-000000000001"), "hook", ["ana@example.com"], "Weekly sales: <Q4> & more", "Revenue is up 4% & margin <steady>.", "<p>Revenue is up</p>", "https://console.example/views/sales?x=1&y=2", attachments, 2);

    // ---- the cards ----

    private static readonly JsonSchema TeamsSchema = JsonSchema.FromText("""
        {
          "type": "object", "required": ["type", "attachments"], "additionalProperties": false,
          "properties": {
            "type": {"const": "message"},
            "attachments": {"type": "array", "minItems": 1, "maxItems": 1, "items": {
              "type": "object", "required": ["contentType", "content"],
              "properties": {
                "contentType": {"const": "application/vnd.microsoft.card.adaptive"},
                "content": {"type": "object", "required": ["type", "version", "body"],
                  "properties": {
                    "$schema": {"const": "http://adaptivecards.io/schemas/adaptive-card.json"},
                    "type": {"const": "AdaptiveCard"}, "version": {"type": "string", "pattern": "^1\\.[0-9]$"},
                    "body": {"type": "array", "minItems": 1, "items": {"type": "object", "required": ["type", "text"], "properties": {"type": {"const": "TextBlock"}, "text": {"type": "string", "maxLength": 4000}, "wrap": {"type": "boolean"}}}},
                    "actions": {"type": "array", "items": {"type": "object", "required": ["type", "title", "url"], "properties": {"type": {"const": "Action.OpenUrl"}, "url": {"type": "string", "pattern": "^https?://"}}}}
                  }}
              }}}
          }
        }
        """);

    private static readonly JsonSchema SlackSchema = JsonSchema.FromText("""
        {
          "type": "object", "required": ["text", "blocks"],
          "properties": {
            "text": {"type": "string", "maxLength": 300},
            "blocks": {"type": "array", "minItems": 1, "maxItems": 50, "items": {"oneOf": [
              {"type": "object", "required": ["type", "text"], "properties": {"type": {"const": "header"}, "text": {"type": "object", "required": ["type", "text"], "properties": {"type": {"const": "plain_text"}, "text": {"type": "string", "maxLength": 150}}}}},
              {"type": "object", "required": ["type", "text"], "properties": {"type": {"const": "section"}, "text": {"type": "object", "required": ["type", "text"], "properties": {"type": {"const": "mrkdwn"}, "text": {"type": "string", "maxLength": 3000}}}}},
              {"type": "object", "required": ["type", "elements"], "properties": {"type": {"const": "actions"}, "elements": {"type": "array", "minItems": 1, "items": {"type": "object", "required": ["type", "text", "url"], "properties": {"type": {"const": "button"}, "url": {"type": "string"}}}}}}
            ]}}
          }
        }
        """);

    private static bool Valid(JsonSchema schema, string json) => schema.Evaluate(JsonNode.Parse(json), new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    [Fact]
    public async Task A_teams_card_is_accepted_by_the_schema_check_and_carries_the_subject_the_body_the_link_and_the_names_of_files()
    {
        var http = Http();
        var result = await Channel("teams", http.Url(), ["127.0.0.1"]).SendAsync(Payload(new DeliveryAttachment("sales.pdf", "application/pdf", [1])), default);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var seen = Assert.Single(http.Seen);
        Assert.Equal(("POST", "/hook/T000/B000/XXXXtokenXXXX"), (seen.Method, seen.Path));
        Assert.StartsWith("application/json", seen.Headers["Content-Type"]);
        Assert.True(Valid(TeamsSchema, seen.Body), seen.Body);
        var card = JsonNode.Parse(seen.Body)!["attachments"]![0]!["content"]!;
        Assert.Equal("Weekly sales: <Q4> & more", (string)card["body"]![0]!["text"]!);
        Assert.Equal("Revenue is up 4% & margin <steady>.", (string)card["body"]![1]!["text"]!);
        Assert.Contains("1 attachment not included (sales.pdf)", (string)card["body"]![2]!["text"]!);
        Assert.Equal("https://console.example/views/sales?x=1&y=2", (string)card["actions"]![0]!["url"]!);
    }

    [Fact]
    public void A_teams_card_keeps_a_long_body_under_the_limit_and_a_message_with_only_html_gets_its_text()
    {
        var long1 = TeamsChannel.Card(Payload() with { Text = new string('x', 9000) });
        Assert.True(Valid(TeamsSchema, long1.ToJsonString()));
        Assert.Equal(TeamsChannel.MaxBody, ((string)long1["attachments"]![0]!["content"]!["body"]![1]!["text"]!).Length);
        var html = TeamsChannel.Card(Payload() with { Text = null, Html = "<p>Revenue&nbsp;is <b>up</b></p>", Link = null });
        Assert.Equal("Revenue is up", (string)html["attachments"]![0]!["content"]!["body"]![1]!["text"]!);
        Assert.Null(html["attachments"]![0]!["content"]!["actions"]);
    }

    [Fact]
    public async Task A_slack_message_is_accepted_by_the_schema_check_with_the_text_escaped_the_way_slack_says()
    {
        var http = Http();
        var result = await Channel("slack", http.Url(), ["127.0.0.1"]).SendAsync(Payload(new DeliveryAttachment("a.csv", "text/csv", [1]), new DeliveryAttachment("b.csv", "text/csv", [2])), default);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var body = Assert.Single(http.Seen).Body;
        Assert.True(Valid(SlackSchema, body), body);
        var message = JsonNode.Parse(body)!;
        Assert.Equal("Weekly sales: <Q4> & more", (string)message["text"]!);
        Assert.StartsWith("Revenue is up 4% &amp; margin &lt;steady&gt;.", (string)message["blocks"]![1]!["text"]!["text"]!);
        Assert.Contains("2 attachments not included (a.csv, b.csv)", (string)message["blocks"]![1]!["text"]!["text"]!);
        Assert.Equal("https://console.example/views/sales?x=1&y=2", (string)message["blocks"]![2]!["elements"]![0]!["url"]!);
        Assert.False(Valid(SlackSchema, """{"text": "x", "blocks": [{"type": "header", "text": {"type": "plain_text", "text": "%s"}}]}""".Replace("%s", new string('h', 151))));
        var longSubject = SlackChannel.Message(Payload() with { Subject = new string('s', 400) });
        Assert.True(Valid(SlackSchema, longSubject.ToJsonString()));
    }

    // ---- the signed webhook ----

    [Fact]
    public async Task The_signed_webhook_signature_verifies_with_the_shared_secret_and_fails_after_one_changed_byte_or_a_stale_timestamp()
    {
        var http = Http();
        var clock = new StateTestClock(Now);
        var result = await Channel("webhook", http.Url("/in"), ["127.0.0.1"], time: clock).SendAsync(Payload(new DeliveryAttachment("sales.csv", "text/csv", Encoding.UTF8.GetBytes("a,b\n1,2"))), default);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var seen = Assert.Single(http.Seen);
        var header = seen.Headers[WebhookSignature.Header];
        Assert.Equal("0197f2c4-1111-7000-8000-000000000001", seen.Headers["X-DbData-Delivery"]);
        Assert.Equal("2", seen.Headers["X-DbData-Attempt"]);
        Assert.StartsWith($"t={Now.ToUnixTimeSeconds()},v1=", header);

        Assert.Equal(SignatureCheck.Valid, WebhookSignature.Verify("shared-signing-secret", header, seen.Body, Now.AddSeconds(30)));
        var changed = seen.Body[..40] + (seen.Body[40] == 'a' ? 'b' : 'a') + seen.Body[41..];
        Assert.Equal(SignatureCheck.Mismatch, WebhookSignature.Verify("shared-signing-secret", header, changed, Now));
        Assert.Equal(SignatureCheck.Mismatch, WebhookSignature.Verify("another-secret", header, seen.Body, Now));
        Assert.Equal(SignatureCheck.OutsideWindow, WebhookSignature.Verify("shared-signing-secret", header, seen.Body, Now.AddMinutes(6)));
        Assert.Equal(SignatureCheck.OutsideWindow, WebhookSignature.Verify("shared-signing-secret", header, seen.Body, Now.AddMinutes(-6)));
        Assert.Equal(SignatureCheck.Valid, WebhookSignature.Verify("shared-signing-secret", header, seen.Body, Now.AddMinutes(6), TimeSpan.FromMinutes(10)));
        Assert.Equal(SignatureCheck.Malformed, WebhookSignature.Verify("shared-signing-secret", "v1=zz", seen.Body, Now));
        Assert.Equal(SignatureCheck.Malformed, WebhookSignature.Verify("shared-signing-secret", null, seen.Body, Now));
        // a signature made over a different timestamp does not pass with this one's header: the timestamp is signed
        var forged = $"t={Now.AddMinutes(-1).ToUnixTimeSeconds()},v1={header[(header.IndexOf("v1=", StringComparison.Ordinal) + 3)..]}";
        Assert.Equal(SignatureCheck.Mismatch, WebhookSignature.Verify("shared-signing-secret", forged, seen.Body, Now));

        var document = JsonNode.Parse(seen.Body)!;
        Assert.Equal(("0197f2c4-1111-7000-8000-000000000001", "hook", "Weekly sales: <Q4> & more", 2), ((string)document["id"]!, (string)document["channel"]!, (string)document["subject"]!, (int)document["attempt"]!));
        Assert.Equal("ana@example.com", (string)document["recipients"]![0]!);
        var attachment = document["attachments"]![0]!;
        Assert.Equal("a,b\n1,2", Encoding.UTF8.GetString(Convert.FromBase64String((string)attachment["content_base64"]!)));
        Assert.Equal(64, ((string)attachment["sha256"]!).Length);
    }

    // ---- what the answers mean ----

    [Theory]
    [InlineData(200, DeliveryOutcome.Sent)]
    [InlineData(204, DeliveryOutcome.Sent)]
    [InlineData(410, DeliveryOutcome.Permanent)]
    [InlineData(404, DeliveryOutcome.Permanent)]
    [InlineData(400, DeliveryOutcome.Permanent)]
    [InlineData(403, DeliveryOutcome.Permanent)]
    [InlineData(503, DeliveryOutcome.Temporary)]
    [InlineData(500, DeliveryOutcome.Temporary)]
    [InlineData(429, DeliveryOutcome.Temporary)]
    [InlineData(408, DeliveryOutcome.Temporary)]
    public async Task The_answers_are_classified_as_the_plan_says(int status, DeliveryOutcome expected)
    {
        var http = Http(_ => (status, status == 410 ? "gone" : status == 204 ? "" : "words from the far side", status == 429 ? new() { ["Retry-After"] = "30" } : null));
        var result = await Channel("slack", http.Url(), ["127.0.0.1"]).SendAsync(Payload(), default);
        Assert.Equal(expected, result.Outcome);
        Assert.Contains($"{status}", result.Detail);
        if (status == 410) Assert.Contains("The webhook was removed at the other end", result.Detail);
        if (status == 429) Assert.Contains("Retry-After 30 seconds", result.Detail);
        if (status is 503 or 404) Assert.Contains("words from the far side", result.Detail);
        Assert.DoesNotContain("XXXXtokenXXXX", result.Detail);                // the address carries a token and is never in the words
    }

    [Fact]
    public async Task A_redirect_is_not_followed_and_a_slow_server_is_temporary()
    {
        var target = Http();
        var http = Http(_ => (302, "", new() { ["Location"] = target.Url("/elsewhere") }));
        var redirected = await Channel("teams", http.Url(), ["127.0.0.1"]).SendAsync(Payload(), default);
        Assert.Equal(DeliveryOutcome.Permanent, redirected.Outcome);
        Assert.Contains("A redirect is not followed", redirected.Detail);
        Assert.Empty(target.Seen);

        var slow = Http(_ => (599, "", null));
        var late = await Channel("teams", slow.Url(), ["127.0.0.1"], timeout: 1).SendAsync(Payload(), default);
        Assert.Equal(DeliveryOutcome.Temporary, late.Outcome);
        Assert.Contains("did not answer within 1 seconds", late.Detail);
    }

    // ---- private addresses ----

    [Fact]
    public async Task A_private_address_is_refused_without_the_allow_list_entry_and_nothing_is_sent()
    {
        var http = Http();
        var refused = await Channel("slack", http.Url()).SendAsync(Payload(), default);
        Assert.Equal(DeliveryOutcome.Permanent, refused.Outcome);
        Assert.Contains("over plain http; only https addresses are used unless allow_private lists the host", refused.Detail);
        Assert.Empty(http.Seen);

        Assert.Equal(DeliveryOutcome.Sent, (await Channel("slack", http.Url(), ["127.0.0.1"]).SendAsync(Payload(), default)).Outcome);
        Assert.Equal(DeliveryOutcome.Sent, (await Channel("slack", http.Url(), ["127.0.0.0/8"]).SendAsync(Payload(), default)).Outcome);
        Assert.Equal(2, http.Seen.Count);
        Assert.Equal(DeliveryOutcome.Permanent, (await Channel("slack", http.Url(), ["10.0.0.0/8"]).SendAsync(Payload(), default)).Outcome);
        Assert.Equal(2, http.Seen.Count);
    }

    [Fact]
    public async Task A_name_that_resolves_to_a_private_address_is_refused_where_the_connection_is_made()
    {
        // https is allowed in principle; the address the name resolves to is not
        var result = await Channel("teams", "https://localhost:9/hook/token", timeout: 5).SendAsync(Payload(), default);
        Assert.Equal(DeliveryOutcome.Permanent, result.Outcome);
        Assert.Contains("is a private address; the channel refuses it unless allow_private lists", result.Detail);
        var literal = await Channel("teams", "https://192.168.4.10/hook/token").SendAsync(Payload(), default);
        Assert.Equal(DeliveryOutcome.Permanent, literal.Outcome);
        Assert.Contains("192.168.4.10 (192.168.4.10) is a private address", literal.Detail);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.2.3.4", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("192.168.1.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("0.0.0.0", true)]
    [InlineData("224.0.0.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("93.184.216.34", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:10.0.0.1", true)]
    [InlineData("::ffff:8.8.8.8", false)]
    [InlineData("2606:4700:4700::1111", false)]
    public void The_private_ranges(string address, bool isPrivate) => Assert.Equal(isPrivate, PrivateAddresses.IsPrivate(IPAddress.Parse(address)));

    [Theory]
    [InlineData("10.1.0.0/16", "10.1.5.5", true)]
    [InlineData("10.1.0.0/16", "10.2.5.5", false)]
    [InlineData("10.1.0.0/12", "10.15.0.1", true)]
    [InlineData("10.1.0.0/12", "10.16.0.1", false)]
    [InlineData("10.0.0.7", "10.0.0.7", true)]
    [InlineData("10.0.0.7", "10.0.0.8", false)]
    [InlineData("fd00::/8", "fd12::1", true)]
    public void The_allow_list_takes_addresses_and_ranges(string entry, string address, bool allowed) => Assert.Equal(allowed, PrivateAddresses.Allows(entry, "other.example", IPAddress.Parse(address)));

    [Fact]
    public void The_allow_list_takes_a_host_name_ignoring_case()
    {
        Assert.True(PrivateAddresses.Allows("Hooks.Internal.Example", "hooks.internal.example", null));
        Assert.False(PrivateAddresses.Allows("hooks.internal.example", "evil.example", null));
    }

    // ---- the settings ----

    [Fact]
    public void The_settings_of_the_webhook_kinds_are_strict_and_the_address_is_always_a_secret_reference()
    {
        static string Refused(string channel) => Assert.Throws<DeliverySettingsException>(() => DeliverySettings.Read(JsonNode.Parse($"{{\"channels\": {{\"hook\": {channel}}}}}")!.AsObject(), "server.yaml", DeliveryKinds.Default())).Message;
        Assert.Contains("delivery.channels.hook.url is a reference to a secret", Refused("""{"kind": "slack", "url": "https://hooks.slack.com/services/T0/B0/xyz"}"""));
        Assert.Contains("delivery.channels.hook.url is required for a teams channel", Refused("""{"kind": "teams"}"""));
        Assert.Contains("delivery.channels.hook.secret is required for a webhook channel", Refused("""{"kind": "webhook", "url": "env:HOOK_URL"}"""));
        Assert.Contains("has no key `host` for a slack channel", Refused("""{"kind": "slack", "url": "env:U", "host": "x"}"""));
        var settings = DeliverySettings.Read(JsonNode.Parse("""{"channels": {"hook": {"kind": "teams", "url": "env:U", "allow_private": ["10.1.0.0/99"]}}}""")!.AsObject(), "server.yaml", DeliveryKinds.Default());
        var ex = Assert.Throws<DeliverySettingsException>(() => new DeliveryChannels(settings, DeliveryKinds.Default(), new MapResolver([]), new SecretScrubber()));
        Assert.Contains("delivery.channels.hook.allow_private: `10.1.0.0/99` is not a host name, an address or a range", ex.Message);
    }
}
