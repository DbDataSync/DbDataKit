using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Delivery.Email;
using DbDataKit.State;

namespace DbDataKit.Delivery.Tests;

/// <summary>A message as a mail client would read it: headers and parts (a tiny reader of what <see cref="MailMessageBuilder"/> writes: multipart, base64 and the RFC 2047 words).</summary>
public sealed record MimePart(Dictionary<string, string> Headers, string Body, List<MimePart> Parts)
{
    public string ContentType => Headers.GetValueOrDefault("content-type", "text/plain").Split(';')[0].Trim().ToLowerInvariant();

    public string DecodedBody => Headers.GetValueOrDefault("content-transfer-encoding") == "base64" ? Encoding.UTF8.GetString(Convert.FromBase64String(Body.Replace("\r\n", ""))) : Body;

    public byte[] Bytes => Convert.FromBase64String(Body.Replace("\r\n", ""));

    public IEnumerable<MimePart> All() => Parts.SelectMany(p => p.All()).Prepend(this);

    public static MimePart Parse(string raw)
    {
        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerText = split < 0 ? raw : raw[..split];
        var body = split < 0 ? "" : raw[(split + 4)..];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? last = null;
        foreach (var line in headerText.Split("\r\n"))
        {
            if ((line.StartsWith(' ') || line.StartsWith('\t')) && last is not null) headers[last] += " " + line.Trim();
            else { var colon = line.IndexOf(':'); last = line[..colon].ToLowerInvariant(); headers[last] = line[(colon + 1)..].Trim(); }
        }

        var part = new MimePart(headers, body, []);
        if (part.ContentType.StartsWith("multipart/", StringComparison.Ordinal))
        {
            var boundary = Regex.Match(headers["content-type"], "boundary=\"([^\"]+)\"").Groups[1].Value;
            foreach (var chunk in body.Split("--" + boundary).Skip(1))
                if (!chunk.StartsWith("--", StringComparison.Ordinal)) part.Parts.Add(Parse(chunk.TrimStart('\r', '\n').TrimEnd('\r', '\n') + (chunk.Contains("\r\n\r\n") ? "" : "\r\n\r\n")));
        }

        return part;
    }

    public static string DecodeWords(string value) => Regex.Replace(value.Replace("?= =?", "?==?"), @"=\?utf-8\?B\?([^?]+)\?=", m => Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value)), RegexOptions.IgnoreCase);
}

/// <summary>Plan 22a GP08: the email channel against an SMTP server in the test's own process.</summary>
public sealed class EmailChannelTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("dbdatakit-mail-").FullName;
    private readonly List<IDisposable> _disposables = [];

    public void Dispose()
    {
        foreach (var d in _disposables) d.Dispose();
        Directory.Delete(_folder, true);
    }

    private FakeSmtpServer Server(bool tls = true)
    {
        var server = new FakeSmtpServer(tls ? TestCertificates.Make("localhost") : null);
        _disposables.Add(server);
        return server;
    }

    private IDeliveryChannel Channel(FakeSmtpServer server, string tls = "starttls", string? trusted = null, string? password = "secret:mail/password", string? username = "robot", Dictionary<string, string>? secrets = null, SecretScrubber? scrubber = null) =>
        ChannelOn(server.Port, tls, trusted, password, username, secrets, scrubber);

    private static IDeliveryChannel ChannelOn(int port, string tls = "starttls", string? trusted = null, string? password = "secret:mail/password", string? username = "robot", Dictionary<string, string>? secrets = null, SecretScrubber? scrubber = null)
    {
        var yaml = $"schema: connection/2\nkind: smtp\nname: mail-main\nsmtp:\n  host: localhost\n  port: {port}\n  tls: {tls}\n  sender: \"DbData <noreply@example.com>\"\n" + (trusted is not null ? $"  trusted_certificate: \"{trusted.Replace('\\', '/')}\"\n" : "");
        if (username is not null && password is not null) yaml += $"sign_in: {{ method: basic, user: {username}, password_secret: \"{password}\" }}\n";
        else if (username is not null) yaml += $"sign_in: {{ method: basic, user: {username}, password_secret: \"secret:mail/absent\" }}\n";
        return TestConnections.Single("mail", TestConnections.Load(yaml), new MapResolver(secrets ?? new() { ["secret:mail/password"] = "s3cr3t-pw" }), scrubber);
    }

    private (FakeSmtpServer Server, string TrustFile) TlsServer(bool implicitTls = false, string name = "localhost", bool offerStartTls = true)
    {
        var cert = TestCertificates.Make(name);
        var server = new FakeSmtpServer(cert, implicitTls, offerStartTls);
        _disposables.Add(server);
        return (server, TestCertificates.WritePem(cert, _folder));
    }
    private static DeliveryPayload Payload(string[]? to = null, string subject = "Weekly sales", params DeliveryAttachment[] attachments) =>
        new(Guid.NewGuid(), "mail", to ?? ["ana@example.com", "bo@example.com"], subject, "Plain body\r\nline two", "<p>Html <b>body</b></p>", "https://console.example/views/sales?x=1&y=2", attachments, 1);

    [Fact]
    public async Task A_message_arrives_over_starttls_with_both_bodies_the_link_the_attachment_and_the_login_from_the_secrets_store()
    {
        var (server, trust) = TlsServer();
        server.AcceptedLogin = ("robot", "s3cr3t-pw");
        var channel = Channel(server, trusted: trust);
        var result = await channel.SendAsync(Payload(attachments: new DeliveryAttachment("sales.pdf", "application/pdf", [37, 80, 68, 70, 0, 1, 2])), default);

        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        var mail = Assert.Single(server.Received);
        Assert.True(mail.OverTls);
        Assert.Equal("robot", mail.Login);
        Assert.Equal("noreply@example.com", mail.From);
        Assert.Equal(["ana@example.com", "bo@example.com"], mail.To);
        var message = MimePart.Parse(mail.Raw);
        Assert.Equal("DbData <noreply@example.com>", message.Headers["from"]);
        Assert.Equal("ana@example.com, bo@example.com", message.Headers["to"]);
        Assert.Equal("Weekly sales", message.Headers["subject"]);
        Assert.Equal("multipart/mixed", message.ContentType);
        var text = message.All().Single(p => p.ContentType == "text/plain");
        var html = message.All().Single(p => p.ContentType == "text/html");
        Assert.StartsWith("Plain body\r\nline two", text.DecodedBody);
        Assert.EndsWith("https://console.example/views/sales?x=1&y=2", text.DecodedBody);
        Assert.Contains("<p>Html <b>body</b></p>", html.DecodedBody);
        Assert.Contains("href=\"https://console.example/views/sales?x=1&amp;y=2\"", html.DecodedBody);
        var attachment = message.All().Single(p => p.Headers.GetValueOrDefault("content-disposition", "").StartsWith("attachment"));
        Assert.Equal("application/pdf", attachment.ContentType);
        Assert.Equal(new byte[] { 37, 80, 68, 70, 0, 1, 2 }, attachment.Bytes);
        Assert.Contains("filename=\"sales.pdf\"", attachment.Headers["content-disposition"]);
        // the login was sent only after the connection was encrypted
        Assert.True(server.Commands.IndexOf("STARTTLS") < server.Commands.FindIndex(c => c.StartsWith("AUTH")));
    }

    [Fact]
    public async Task A_message_arrives_over_implicit_tls()
    {
        var (server, trust) = TlsServer(implicitTls: true);
        server.AcceptedLogin = ("robot", "s3cr3t-pw");
        var result = await Channel(server, "implicit", trust).SendAsync(Payload(["ana@example.com"]), default);
        Assert.Equal(DeliveryOutcome.Sent, result.Outcome);
        Assert.True(Assert.Single(server.Received).OverTls);
        Assert.DoesNotContain("STARTTLS", server.Commands);
    }

    [Fact]
    public async Task A_relay_without_tls_is_used_only_when_the_settings_say_so_and_a_message_with_one_body_has_one_part()
    {
        var server = Server(tls: false);
        var channel = Channel(server, "none", username: null, password: null);
        var payload = Payload(["ana@example.com"]) with { Html = null, Link = null };
        Assert.Equal(DeliveryOutcome.Sent, (await channel.SendAsync(payload, default)).Outcome);
        var mail = Assert.Single(server.Received);
        Assert.False(mail.OverTls);
        var message = MimePart.Parse(mail.Raw);
        Assert.Equal("text/plain", message.ContentType);
        Assert.Equal("Plain body\r\nline two", message.DecodedBody);
    }

    [Fact]
    public async Task A_refused_recipient_parks_the_message_with_the_servers_words_and_nothing_is_sent()
    {
        var (server, trust) = TlsServer();
        server.AcceptedLogin = ("robot", "s3cr3t-pw");
        server.RecipientReplies["bo@example.com"] = "550 5.1.1 <bo@example.com>: Recipient address rejected: User unknown";
        var result = await Channel(server, trusted: trust).SendAsync(Payload(), default);

        Assert.Equal(DeliveryOutcome.Permanent, result.Outcome);
        Assert.Contains("550 5.1.1 <bo@example.com>: Recipient address rejected: User unknown", result.Detail);
        Assert.Contains("RCPT TO:<bo@example.com>", result.Detail);
        Assert.Empty(server.Received);
    }

    [Fact]
    public async Task The_same_refusal_through_the_service_parks_the_message_and_shows_the_words()
    {
        var (server, trust) = TlsServer();
        server.AcceptedLogin = ("robot", "s3cr3t-pw");
        server.RecipientReplies["bo@example.com"] = "550 5.1.1 User unknown";
        using var h = new DeliveryHarness(StateEngineIds.Sqlite);
        var channel = Channel(server, trusted: trust);
        var service = h.Service(new ChannelSet(channel), "a");
        var id = await service.SendAsync(new DeliveryRequest("mail", "Hello", ["ana@example.com", "bo@example.com"], Text: "hi"));
        await service.RunOnceAsync();
        var row = h.Store.Find(id)!;
        Assert.Equal("dead", row.Status);
        Assert.Contains("550 5.1.1 User unknown", row.LastDetail);
    }

    [Fact]
    public async Task A_full_mailbox_at_the_end_of_the_message_is_temporary_and_a_closed_port_is_temporary()
    {
        var (server, trust) = TlsServer();
        server.AcceptedLogin = ("robot", "s3cr3t-pw");
        server.EndOfDataReply = "452 4.2.2 mailbox full";
        var full = await Channel(server, trusted: trust).SendAsync(Payload(["ana@example.com"]), default);
        Assert.Equal(DeliveryOutcome.Temporary, full.Outcome);
        Assert.Contains("452 4.2.2 mailbox full", full.Detail);

        var closed = new TcpListener(IPAddress.Loopback, 0);
        closed.Start();
        var port = ((IPEndPoint)closed.LocalEndpoint).Port;
        closed.Stop();
        var down = await ChannelOn(port, "none", username: null, password: null).SendAsync(Payload(["ana@example.com"]), default);
        Assert.Equal(DeliveryOutcome.Temporary, down.Outcome);
        Assert.Contains("failed", down.Detail);
    }

    [Fact]
    public async Task A_certificate_for_another_name_is_refused_even_when_it_is_trusted()
    {
        var (server, trust) = TlsServer(name: "mail.other.example");
        server.AcceptedLogin = ("robot", "s3cr3t-pw");
        var result = await Channel(server, trusted: trust).SendAsync(Payload(["ana@example.com"]), default);
        Assert.Equal(DeliveryOutcome.Permanent, result.Outcome);
        Assert.Contains("TLS handshake with localhost", result.Detail);
        Assert.Contains("must be for the name `localhost`", result.Detail);
        Assert.Empty(server.Received);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("AUTH") || c.StartsWith("MAIL"));
    }

    [Fact]
    public async Task A_certificate_nobody_trusts_is_refused_and_no_login_is_sent()
    {
        var (server, _) = TlsServer();
        server.AcceptedLogin = ("robot", "s3cr3t-pw");
        var result = await Channel(server).SendAsync(Payload(["ana@example.com"]), default);   // no trusted_certificate: the machine's roots do not know it
        Assert.Equal(DeliveryOutcome.Permanent, result.Outcome);
        Assert.Contains("TLS handshake", result.Detail);
        Assert.Empty(server.Received);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("AUTH"));
    }

    [Fact]
    public async Task A_server_that_does_not_offer_starttls_is_refused_for_a_starttls_channel_and_the_login_is_never_sent()
    {
        var server = Server(tls: false);
        var result = await Channel(server, "starttls").SendAsync(Payload(["ana@example.com"]), default);
        Assert.Equal(DeliveryOutcome.Permanent, result.Outcome);
        Assert.Contains("does not offer STARTTLS", result.Detail);
        Assert.DoesNotContain(server.Commands, c => c.StartsWith("AUTH"));
    }

    [Fact]
    public async Task A_wrong_password_is_refused_and_the_server_echoing_it_is_scrubbed_from_the_words()
    {
        var (server, trust) = TlsServer();
        server.AcceptedLogin = ("robot", "another-password");
        var scrubber = new SecretScrubber();
        var result = await Channel(server, trusted: trust, scrubber: scrubber).SendAsync(Payload(["ana@example.com"]), default);
        Assert.Equal(DeliveryOutcome.Permanent, result.Outcome);
        Assert.Contains("535 5.7.8 authentication failed for robot", result.Detail);
        Assert.Contains("s3cr3t-pw", result.Detail);                          // the channel's raw words hold it...
        Assert.DoesNotContain("s3cr3t-pw", scrubber.Scrub(result.Detail));    // ...and the service scrubs them before they are written
    }

    [Fact]
    public async Task A_secret_that_is_not_in_the_store_is_a_temporary_failure_naming_the_reference()
    {
        var (server, trust) = TlsServer();
        var channel = Channel(server, trusted: trust, secrets: []);
        var ex = await Assert.ThrowsAsync<ChannelUnavailableException>(() => channel.SendAsync(Payload(["ana@example.com"]), default));
        Assert.Contains("`password` of the channel 'mail' (secret:mail/password)", ex.Message);
    }

    [Fact]
    public async Task A_subject_and_a_file_name_with_other_letters_are_encoded_and_a_bad_address_or_a_line_break_is_the_messages_own_fault()
    {
        var server = Server(tls: false);
        var channel = Channel(server, "none", username: null, password: null);
        var ok = await channel.SendAsync(Payload(["ana@example.com"], "Umsatz der Woche – Zusammenführung äöü 日本語", new DeliveryAttachment("Bericht über Umsatz.pdf", "application/pdf", [1])), default);
        Assert.Equal(DeliveryOutcome.Sent, ok.Outcome);
        var message = MimePart.Parse(Assert.Single(server.Received).Raw);
        Assert.Equal("Umsatz der Woche – Zusammenführung äöü 日本語", MimePart.DecodeWords(message.Headers["subject"]));
        var disposition = message.All().Single(p => p.Headers.ContainsKey("content-disposition")).Headers["content-disposition"];
        Assert.Contains("filename*=UTF-8''Bericht%20%C3%BCber%20Umsatz.pdf", disposition);

        var bad = await channel.SendAsync(Payload(["not an address"]), default);
        Assert.Equal(DeliveryOutcome.Permanent, bad.Outcome);
        Assert.Contains("'not an address' is not an email address", bad.Detail);
        var injected = await channel.SendAsync(Payload(["ana@example.com"], "Hello\r\nBcc: spy@example.com"), default);
        Assert.Equal(DeliveryOutcome.Permanent, injected.Outcome);
        Assert.Contains("line break", injected.Detail);
        var none = await channel.SendAsync(Payload([]), default);
        Assert.Equal(DeliveryOutcome.Permanent, none.Outcome);
        Assert.Single(server.Received);
    }

    [Theory]
    [InlineData("smtp:\n  host: h\n", "sender")]
    [InlineData("smtp:\n  host: h\n  sender: a@b.example\n  tls: plain\n", "tls")]
    [InlineData("smtp:\n  host: h\n  sender: a@b.example\n  tls: none\n", "clear text")]
    public void A_smtp_connection_that_does_not_read_cannot_be_a_channel(string section, string word)
    {
        var r = DbDataKit.Connections.ConnectionDefinitionLoader.LoadConnectionOfAnyKind("schema: connection/2\nkind: smtp\nname: m\n" + section, "connections/m.yml");
        Assert.False(r.Ok);
        Assert.Contains(word, string.Join("\n", r.Diagnostics.Select(d => d.Message)), StringComparison.Ordinal);
    }

    [Fact]
    public void A_trusted_certificate_that_cannot_be_read_is_the_channels_problem_not_a_crash()
    {
        var definition = TestConnections.Load("schema: connection/2\nkind: smtp\nname: m\nsmtp:\n  host: localhost\n  sender: a@b.example\n  trusted_certificate: /no/such/file.pem\n");
        var channels = TestConnections.Channels(TestConnections.Settings("""{"mail": "m"}"""), new FixedDeliveryConnections([definition]), new MapResolver([]));
        var resolution = channels.Resolve("mail", null);
        Assert.Null(resolution.Channel);
        Assert.True(resolution.Retryable);
        Assert.Contains("trusted_certificate", resolution.Problem);
    }
}
