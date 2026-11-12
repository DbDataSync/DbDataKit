using System.Globalization;
using System.Net;
using System.Net.Mail;
using System.Text;

namespace DbDataKit.Delivery.Email;

/// <summary>The message cannot be made into mail (an address that is not one, a line break in the subject): the message's own fault, so a permanent failure.</summary>
public sealed class MailFormatException(string message) : Exception(message);

/// <summary>
/// Makes the RFC 5322 text of a <see cref="DeliveryPayload"/>: headers (From, To, Subject, Date, Message-Id, MIME-Version), then one part, or <c>multipart/alternative</c> for the two bodies, inside <c>multipart/mixed</c> when there are attachments. An attachment with a content id (and an HTML body) is an inline picture: <c>multipart/related</c> around the body, the picture after it with <c>Content-ID</c> and <c>Content-Disposition: inline</c>.
/// Every text part is UTF-8 in base64, so no line is too long and no byte needs a rule; a non-ASCII subject or file name is encoded as the RFCs say. A link is added to both bodies (as a line of text and as an anchor).
/// </summary>
public static class MailMessageBuilder
{
    public static string Build(DeliveryPayload payload, MailAddress from, DateTimeOffset now, string hostForIds)
    {
        if (payload.Recipients.Count == 0) throw new MailFormatException("The message has no recipient, and an email needs at least one.");
        var recipients = payload.Recipients.Select(Address).ToList();
        var subject = SingleLine(payload.Subject, "subject");
        var text = payload.Text;
        var html = payload.Html;
        if (payload.Link is { } link)
        {
            text = (text is null ? "" : text + "\r\n\r\n") + link;
            if (html is not null) html += $"<p><a href=\"{WebUtility.HtmlEncode(link)}\">{WebUtility.HtmlEncode(link)}</a></p>";
        }

        if (text is null && html is null) text = payload.Subject;

        var sb = new StringBuilder();
        sb.Append("From: ").Append(Mailbox(from)).Append("\r\n");
        sb.Append("To: ").Append(string.Join(", ", recipients.Select(Mailbox))).Append("\r\n");
        sb.Append("Subject: ").Append(EncodeHeader(subject)).Append("\r\n");
        sb.Append("Date: ").Append(now.UtcDateTime.ToString("ddd, dd MMM yyyy HH:mm:ss 'GMT'", CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Message-ID: <").Append(payload.MessageId.ToString("N")).Append('@').Append(hostForIds).Append(">\r\n");
        sb.Append("MIME-Version: 1.0\r\n");

        // a picture with a content id is part of the body (multipart/related: the HTML names it as cid:), the rest are downloads (multipart/mixed)
        var inline = payload.Attachments.Where(a => a.ContentId is not null && html is not null).ToList();
        var downloads = payload.Attachments.Except(inline).ToList();
        var mixed = downloads.Count > 0 ? "=_mixed_" + Guid.NewGuid().ToString("N") : null;
        if (mixed is not null)
        {
            sb.Append($"Content-Type: multipart/mixed; boundary=\"{mixed}\"\r\n\r\n");
            sb.Append($"--{mixed}\r\n");
            AppendBodyWithInline(sb, text, html, inline);
            foreach (var attachment in downloads)
            {
                sb.Append($"--{mixed}\r\n");
                var name = SingleLine(attachment.Name, "attachment name");
                sb.Append($"Content-Type: {SingleLine(attachment.MediaType, "media type")}; name=\"{AsciiName(name)}\"\r\n");
                sb.Append("Content-Transfer-Encoding: base64\r\n");
                sb.Append($"Content-Disposition: attachment; filename=\"{AsciiName(name)}\"{(IsAscii(name) ? "" : "; filename*=UTF-8''" + Uri.EscapeDataString(name))}\r\n\r\n");
                sb.Append(Base64(attachment.Content)).Append("\r\n");
            }

            sb.Append($"--{mixed}--\r\n");
        }
        else
        {
            AppendBodyWithInline(sb, text, html, inline);
        }

        return sb.ToString();
    }

    /// <summary>The body, and with inline pictures a <c>multipart/related</c> around it: the body first, then each picture with its <c>Content-ID</c>.</summary>
    private static void AppendBodyWithInline(StringBuilder sb, string? text, string? html, List<DeliveryAttachment> inline)
    {
        if (inline.Count == 0) { AppendBody(sb, text, html); return; }
        var related = "=_related_" + Guid.NewGuid().ToString("N");
        sb.Append($"Content-Type: multipart/related; type=\"{(text is not null ? "multipart/alternative" : "text/html")}\"; boundary=\"{related}\"\r\n\r\n");
        sb.Append($"--{related}\r\n");
        AppendBody(sb, text, html);
        foreach (var picture in inline)
        {
            var id = SingleLine(picture.ContentId!, "content id");
            sb.Append($"--{related}\r\n");
            sb.Append($"Content-Type: {SingleLine(picture.MediaType, "media type")}; name=\"{AsciiName(picture.Name)}\"\r\n");
            sb.Append("Content-Transfer-Encoding: base64\r\n");
            sb.Append($"Content-ID: <{id}>\r\n");
            sb.Append($"Content-Disposition: inline; filename=\"{AsciiName(picture.Name)}\"\r\n\r\n");
            sb.Append(Base64(picture.Content)).Append("\r\n");
        }

        sb.Append($"--{related}--\r\n");
    }

    /// <summary>The body part(s) with their own Content-Type header; when it opens a message (no mixed wrapper) the headers are the message's.</summary>
    private static void AppendBody(StringBuilder sb, string? text, string? html)
    {
        if (text is not null && html is not null)
        {
            var alt = "=_alt_" + Guid.NewGuid().ToString("N");
            sb.Append($"Content-Type: multipart/alternative; boundary=\"{alt}\"\r\n\r\n");
            sb.Append($"--{alt}\r\n");
            Part(sb, "text/plain", text);
            sb.Append($"--{alt}\r\n");
            Part(sb, "text/html", html);
            sb.Append($"--{alt}--\r\n");
        }
        else
        {
            Part(sb, text is not null ? "text/plain" : "text/html", text ?? html!);
        }
    }

    private static void Part(StringBuilder sb, string type, string body)
    {
        sb.Append($"Content-Type: {type}; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n");
        sb.Append(Base64(Encoding.UTF8.GetBytes(body))).Append("\r\n");
    }

    private static string Base64(byte[] bytes)
    {
        var all = Convert.ToBase64String(bytes);
        var sb = new StringBuilder();
        for (var i = 0; i < all.Length; i += 76) sb.Append(all, i, Math.Min(76, all.Length - i)).Append("\r\n");
        return sb.ToString().TrimEnd('\r', '\n');
    }

    public static MailAddress Address(string text)
    {
        SingleLine(text, "address");
        try
        {
            var address = new MailAddress(text);
            if (!address.Host.Contains('.', StringComparison.Ordinal) && !string.Equals(address.Host, "localhost", StringComparison.OrdinalIgnoreCase)) throw new FormatException("the host part has no dot");
            return address;
        }
        catch (FormatException e)
        {
            throw new MailFormatException($"'{text}' is not an email address: {e.Message}.");
        }
    }

    private static string Mailbox(MailAddress a) => string.IsNullOrEmpty(a.DisplayName) ? a.Address : $"{EncodeHeader(a.DisplayName)} <{a.Address}>";

    private static string SingleLine(string text, string what)
    {
        if (text.Contains('\r', StringComparison.Ordinal) || text.Contains('\n', StringComparison.Ordinal)) throw new MailFormatException($"The {what} holds a line break, which a mail header cannot.");
        return text;
    }

    private static bool IsAscii(string text) => text.All(c => c is >= ' ' and <= '~');

    private static string AsciiName(string name) => new([.. name.Select(c => c is >= ' ' and <= '~' && c != '"' && c != '\\' ? c : '_')]);

    /// <summary>A header value as RFC 2047 says: plain when it is ASCII, else base64 encoded words of at most 45 bytes each.</summary>
    public static string EncodeHeader(string value)
    {
        if (IsAscii(value)) return value;
        var words = new List<string>();
        var current = new StringBuilder();
        foreach (var element in StringElements(value))
        {
            if (Encoding.UTF8.GetByteCount(current.ToString() + element) > 45 && current.Length > 0)
            {
                words.Add(Word(current.ToString()));
                current.Clear();
            }

            current.Append(element);
        }

        if (current.Length > 0) words.Add(Word(current.ToString()));
        return string.Join("\r\n ", words);
    }

    private static string Word(string s) => "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";

    private static IEnumerable<string> StringElements(string value)
    {
        var e = StringInfo.GetTextElementEnumerator(value);
        while (e.MoveNext()) yield return (string)e.Current;
    }
}
