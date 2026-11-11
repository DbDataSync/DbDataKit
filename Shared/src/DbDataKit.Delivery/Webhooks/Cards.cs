using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Delivery.Webhooks;

/// <summary>The Teams channel (plan 22a GP09): the message as an Adaptive Card in the envelope an incoming webhook (a Workflows "post to a channel when a webhook request is received") takes: <c>{"type":"message","attachments":[{"contentType":"application/vnd.microsoft.card.adaptive","content":{...}}]}</c>. The title is the subject, the body is the text (plain; Teams renders what it can), the link is an <c>Action.OpenUrl</c> button. A webhook cannot carry a file, so attachments are named in the card and the link opens the thing they came from.</summary>
public sealed class TeamsChannel(ChannelContext context) : WebhookChannel(context, KindName)
{
    public const string KindName = "teams";

    /// <summary>What a card keeps of a body (Teams refuses a message of about 28 KB; the card is cut well before that).</summary>
    public const int MaxBody = 4000;

    public static ChannelKind Describe { get; } = new(KindName, CommonKeys, c => new TeamsChannel(c), "A Teams incoming webhook (Adaptive Card).");

    protected override WebhookRequest Build(DeliveryPayload payload) => new(Card(payload).ToJsonString(), "application/json");

    public static JsonObject Card(DeliveryPayload payload)
    {
        var body = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = payload.Subject, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true },
        };
        var text = payload.Text ?? Strip(payload.Html);
        if (!string.IsNullOrWhiteSpace(text)) body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = Clip(text, MaxBody), ["wrap"] = true });
        if (payload.Attachments.Count > 0)
            body.Add(new JsonObject
            {
                ["type"] = "TextBlock", ["wrap"] = true, ["isSubtle"] = true,
                ["text"] = $"{payload.Attachments.Count} attachment{(payload.Attachments.Count == 1 ? "" : "s")} not included ({string.Join(", ", payload.Attachments.Select(a => a.Name))}): a webhook cannot carry files.",
            });
        var content = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
        };
        if (payload.Link is { } link) content["actions"] = new JsonArray(new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = "Open", ["url"] = link });
        return new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray(new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["contentUrl"] = null, ["content"] = content }),
        };
    }

    internal static string Clip(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";

    internal static string Strip(string? html) => html is null ? "" : System.Text.RegularExpressions.Regex.Replace(System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ")), @"\s+", " ").Trim();
}

/// <summary>The Slack channel (plan 22a GP09): Block Kit in the payload an incoming webhook takes: <c>text</c> (the notification's fallback), a <c>header</c> block with the subject (150 characters at most), a <c>section</c> with the body as <c>mrkdwn</c> (3000 at most, <c>&amp; &lt; &gt;</c> escaped as Slack says) and an <c>actions</c> block with a button for the link. Files are named, not sent.</summary>
public sealed class SlackChannel(ChannelContext context) : WebhookChannel(context, KindName)
{
    public const string KindName = "slack";

    public static ChannelKind Describe { get; } = new(KindName, CommonKeys, c => new SlackChannel(c), "A Slack incoming webhook (Block Kit).");

    protected override WebhookRequest Build(DeliveryPayload payload) => new(Message(payload).ToJsonString(), "application/json");

    public static JsonObject Message(DeliveryPayload payload)
    {
        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "header", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = TeamsChannel.Clip(payload.Subject, 150) } },
        };
        var text = Escape(payload.Text ?? TeamsChannel.Strip(payload.Html));
        if (payload.Attachments.Count > 0)
            text += $"{(string.IsNullOrWhiteSpace(text) ? "" : "\n\n")}_{payload.Attachments.Count} attachment{(payload.Attachments.Count == 1 ? "" : "s")} not included ({Escape(string.Join(", ", payload.Attachments.Select(a => a.Name)))}): a webhook cannot carry files._";
        if (!string.IsNullOrWhiteSpace(text)) blocks.Add(new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = TeamsChannel.Clip(text, 3000) } });
        if (payload.Link is { } link)
            blocks.Add(new JsonObject { ["type"] = "actions", ["elements"] = new JsonArray(new JsonObject { ["type"] = "button", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = "Open" }, ["url"] = link }) });
        return new JsonObject { ["text"] = TeamsChannel.Clip(payload.Subject, 300), ["blocks"] = blocks };
    }

    private static string Escape(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
}

/// <summary>What <see cref="WebhookSignature.Verify"/> found.</summary>
public enum SignatureCheck
{
    Valid,

    /// <summary>The header is missing a part or one is not what it should be.</summary>
    Malformed,

    /// <summary>The timestamp is outside the replay window (an old request replayed, or a clock far off).</summary>
    OutsideWindow,

    /// <summary>The signature does not match the body and the secret: a byte changed, or another secret.</summary>
    Mismatch,
}

/// <summary>
/// The signature of the generic webhook (plan 22a GP09, QG7): the header <c>X-DbData-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>, where <c>v1</c> is HMAC-SHA256 with the shared secret of the text <c>&lt;t&gt;.&lt;body&gt;</c> (the body exactly as sent). A receiver
/// checks the signature in constant time and that <c>t</c> is within a window (<see cref="DefaultWindow"/>, five minutes) of its own clock, which is what stops a request being replayed later.
/// </summary>
public static class WebhookSignature
{
    public const string Header = "X-DbData-Signature";

    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, string body, DateTimeOffset at)
    {
        var t = at.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Hex(Mac(secret, t, body))}";
    }

    public static SignatureCheck Verify(string secret, string? header, string body, DateTimeOffset now, TimeSpan? window = null)
    {
        if (header is null) return SignatureCheck.Malformed;
        string? t = null, v1 = null;
        foreach (var part in header.Split(','))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq < 0) return SignatureCheck.Malformed;
            switch (part[..eq].Trim())
            {
                case "t": t = part[(eq + 1)..].Trim(); break;
                case "v1": v1 = part[(eq + 1)..].Trim(); break;
            }
        }

        if (t is null || v1 is null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) return SignatureCheck.Malformed;
        byte[] given;
        try { given = Convert.FromHexString(v1); } catch (FormatException) { return SignatureCheck.Malformed; }
        if (!CryptographicOperations.FixedTimeEquals(given, Mac(secret, t, body))) return SignatureCheck.Mismatch;
        var age = now - DateTimeOffset.FromUnixTimeSeconds(seconds);
        return age.Duration() > (window ?? DefaultWindow) ? SignatureCheck.OutsideWindow : SignatureCheck.Valid;
    }

    private static byte[] Mac(string secret, string t, string body) => HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(t + "." + body));

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}

/// <summary>
/// The signed generic webhook (plan 22a GP09): a JSON document (<c>id, channel, subject, text, html, link, recipients, attempt, sent_at, attachments</c>) POSTed with the header <c>X-DbData-Signature</c> (<see cref="WebhookSignature"/>), <c>X-DbData-Delivery</c> (the message id,
/// the receiver's idempotency key: a retry carries the same id) and <c>X-DbData-Attempt</c>. An attachment is listed with its name, type, size and SHA-256 and carries its bytes as <c>content_base64</c> up to 2 MiB.
/// </summary>
public sealed class SignedWebhookChannel(ChannelContext context) : WebhookChannel(context, KindName)
{
    public const string KindName = "webhook";

    public const int MaxInlineBytes = 2 * 1024 * 1024;

    public static ChannelKind Describe { get; } = new(KindName, [.. CommonKeys, new ChannelKey("secret", ChannelKeyType.Secret, Required: true, Description: "A reference to the secret shared with the receiver; it signs every request.")],
        c => new SignedWebhookChannel(c), "A generic webhook, signed with HMAC-SHA256 and a timestamp.");

    /// <summary>The document sent for a message.</summary>
    public static JsonObject Document(DeliveryPayload payload, DateTimeOffset sentAt)
    {
        var attachments = new JsonArray();
        foreach (var a in payload.Attachments)
        {
            var item = new JsonObject { ["name"] = a.Name, ["media_type"] = a.MediaType, ["size"] = a.Content.Length, ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(a.Content)) };
            if (a.Content.Length <= MaxInlineBytes) item["content_base64"] = Convert.ToBase64String(a.Content);
            attachments.Add(item);
        }

        return new JsonObject
        {
            ["id"] = payload.MessageId.ToString("D"), ["channel"] = payload.Channel, ["subject"] = payload.Subject, ["text"] = payload.Text, ["html"] = payload.Html, ["link"] = payload.Link,
            ["recipients"] = new JsonArray([.. payload.Recipients.Select(r => (JsonNode?)JsonValue.Create(r))]), ["attempt"] = payload.Attempt, ["sent_at"] = sentAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture), ["attachments"] = attachments,
        };
    }

    protected override async ValueTask<WebhookRequest> BuildAsync(DeliveryPayload payload, CancellationToken cancellationToken)
    {
        var secret = await Context.SecretAsync("secret", cancellationToken).ConfigureAwait(false) ?? throw new ChannelUnavailableException($"The channel '{Name}' has no `secret` to sign with.");
        var now = Context.Time.GetUtcNow();
        var body = Document(payload, now).ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        return new WebhookRequest(body, "application/json", new Dictionary<string, string>
        {
            [WebhookSignature.Header] = WebhookSignature.Sign(secret, body, now),
            ["X-DbData-Delivery"] = payload.MessageId.ToString("D"),
            ["X-DbData-Attempt"] = payload.Attempt.ToString(CultureInfo.InvariantCulture),
        });
    }
}
