using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DbDataKit.Delivery.Email;

/// <summary>How the connection to the mail server is protected.</summary>
public enum SmtpTls
{
    /// <summary>The connection starts in clear and is upgraded with STARTTLS before anything else is sent (port 587). A server that does not offer STARTTLS is refused: credentials are never sent in clear because a server did not say it could do better.</summary>
    StartTls,

    /// <summary>TLS from the first byte (SMTPS, port 465).</summary>
    Implicit,

    /// <summary>No TLS at all (a relay on the same host or a private network, port 25). Only when the settings say so.</summary>
    None,
}

/// <summary>What one SMTP conversation needs.</summary>
/// <param name="TrustedCertificate">A certificate (a private CA's, or the server's own) the chain is checked against instead of the machine's roots; null: the machine's roots. The name is checked either way.</param>
public sealed record SmtpSettings(string Host, int Port, SmtpTls Tls, string Helo, TimeSpan Timeout, string? Username = null, X509Certificate2? TrustedCertificate = null);

/// <summary>An SMTP reply: the code and the server's words (a multi-line reply joined with a space).</summary>
public sealed record SmtpReply(int Code, string Text)
{
    public override string ToString() => $"{Code} {Text}";
}

/// <summary>A refusal of the server, with the reply that said it. 4xx is temporary, 5xx is permanent.</summary>
public sealed class SmtpRefusedException(string step, SmtpReply reply) : Exception($"The server answered {reply} to {step}.")
{
    public SmtpReply Reply { get; } = reply;

    public string Step { get; } = step;

    public bool Permanent => Reply.Code >= 500;
}

/// <summary>The TLS handshake was refused: the certificate does not match the name or does not chain to a trusted root (retrying will not change that; the settings or the server must).</summary>
public sealed class SmtpTlsException(string message, Exception inner) : Exception(message, inner);

/// <summary>
/// A small SMTP client (RFC 5321) for the email channel: EHLO, STARTTLS or implicit TLS, AUTH PLAIN or LOGIN, one message to its recipients, QUIT. It is not a general mail library: it sends one prepared message per connection and stops at the first thing
/// the server refuses. No third-party mail package is pinned in the repository, so this is what the channel uses; <c>System.Net.Mail.SmtpClient</c> is not an option because it cannot do implicit TLS and says of itself that it should not be used.
/// </summary>
public static class SmtpClientLite
{
    /// <summary>Sends <paramref name="message"/> (a complete RFC 5322 text with CRLF line ends) from <paramref name="sender"/> to <paramref name="recipients"/>. Throws <see cref="SmtpRefusedException"/> for what the server answers, <see cref="SmtpTlsException"/> for a certificate it is not willing to trust, and the usual IO exceptions for the network.</summary>
    public static async Task SendAsync(SmtpSettings settings, string? password, string sender, IReadOnlyList<string> recipients, string message, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(settings.Host, settings.Port, cancellationToken).ConfigureAwait(false);
        Stream stream = tcp.GetStream();
        if (settings.Tls == SmtpTls.Implicit) stream = await UpgradeAsync(stream, settings, cancellationToken).ConfigureAwait(false);
        var session = new Session(stream);
        try
        {
            Expect(await session.ReadAsync(cancellationToken).ConfigureAwait(false), "the greeting", 220);
            var hello = await session.CommandAsync($"EHLO {settings.Helo}", cancellationToken).ConfigureAwait(false);
            Expect(hello, "EHLO", 250);
            if (settings.Tls == SmtpTls.StartTls)
            {
                if (!Extensions(hello).Contains("STARTTLS", StringComparer.OrdinalIgnoreCase))
                    throw new SmtpRefusedException("EHLO (STARTTLS is required by the channel's tls: starttls)", new SmtpReply(530, "the server does not offer STARTTLS"));
                Expect(await session.CommandAsync("STARTTLS", cancellationToken).ConfigureAwait(false), "STARTTLS", 220);
                session = new Session(await UpgradeAsync(stream, settings, cancellationToken).ConfigureAwait(false));
                hello = await session.CommandAsync($"EHLO {settings.Helo}", cancellationToken).ConfigureAwait(false);
                Expect(hello, "EHLO after STARTTLS", 250);
            }

            if (settings.Username is not null && password is not null) await AuthenticateAsync(session, settings.Username, password, Extensions(hello), cancellationToken).ConfigureAwait(false);

            Expect(await session.CommandAsync($"MAIL FROM:<{sender}>", cancellationToken).ConfigureAwait(false), "MAIL FROM", 250);
            foreach (var recipient in recipients)
                Expect(await session.CommandAsync($"RCPT TO:<{recipient}>", cancellationToken).ConfigureAwait(false), $"RCPT TO:<{recipient}>", 250, 251);
            Expect(await session.CommandAsync("DATA", cancellationToken).ConfigureAwait(false), "DATA", 354);
            await session.WriteDataAsync(message, cancellationToken).ConfigureAwait(false);
            Expect(await session.ReadAsync(cancellationToken).ConfigureAwait(false), "the end of the message", 250);
            try { await session.CommandAsync("QUIT", cancellationToken).ConfigureAwait(false); } catch (Exception e) when (e is IOException or SmtpRefusedException) { /* the message is accepted; the goodbye does not matter */ }
        }
        catch (SmtpRefusedException)
        {
            try { await session.WriteLineAsync("QUIT", CancellationToken.None).ConfigureAwait(false); } catch (Exception) { /* closing anyway */ }
            throw;
        }
    }

    private static async Task AuthenticateAsync(Session session, string user, string password, IReadOnlyList<string> extensions, CancellationToken ct)
    {
        var mechanisms = extensions.Where(e => e.StartsWith("AUTH", StringComparison.OrdinalIgnoreCase)).SelectMany(e => e.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1)).Select(m => m.ToUpperInvariant()).ToList();
        if (mechanisms.Contains("PLAIN") || mechanisms.Count == 0)
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"\0{user}\0{password}"));
            Expect(await session.CommandAsync("AUTH PLAIN " + token, ct).ConfigureAwait(false), "AUTH PLAIN", 235);
        }
        else if (mechanisms.Contains("LOGIN"))
        {
            Expect(await session.CommandAsync("AUTH LOGIN", ct).ConfigureAwait(false), "AUTH LOGIN", 334);
            Expect(await session.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(user)), ct).ConfigureAwait(false), "AUTH LOGIN", 334);
            Expect(await session.CommandAsync(Convert.ToBase64String(Encoding.UTF8.GetBytes(password)), ct).ConfigureAwait(false), "AUTH LOGIN", 235);
        }
        else
        {
            throw new SmtpRefusedException("AUTH (the channel knows PLAIN and LOGIN)", new SmtpReply(504, "the server offers " + string.Join(", ", mechanisms)));
        }
    }

    private static void Expect(SmtpReply reply, string step, params int[] codes)
    {
        if (!codes.Contains(reply.Code)) throw new SmtpRefusedException(step, reply);
    }

    private static IReadOnlyList<string> Extensions(SmtpReply hello) => hello.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Trim()).ToList();

    private static async Task<SslStream> UpgradeAsync(Stream inner, SmtpSettings settings, CancellationToken ct)
    {
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false, (_, certificate, chain, errors) => Validate(settings.TrustedCertificate, certificate, chain, errors));
        try
        {
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = settings.Host, EnabledSslProtocols = SslProtocols.None }, ct).ConfigureAwait(false);
        }
        catch (AuthenticationException e)
        {
            throw new SmtpTlsException($"The TLS handshake with {settings.Host}:{settings.Port} was refused: {e.Message}{(e.InnerException is null ? "" : " " + e.InnerException.Message)} The certificate must be for the name `{settings.Host}` and chain to a root this machine trusts" +
                                       (settings.TrustedCertificate is null ? " (or to the file named by trusted_certificate)." : "."), e);
        }

        return ssl;
    }

    private static bool Validate(X509Certificate2? trusted, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        if ((errors & (SslPolicyErrors.RemoteCertificateNameMismatch | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;   // the name is always checked
        if (trusted is null || certificate is null) return false;
        using var custom = new X509Chain();
        custom.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        custom.ChainPolicy.CustomTrustStore.Add(trusted);
        custom.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return custom.Build(new X509Certificate2(certificate));
    }

    /// <summary>One conversation: lines in, lines out, CRLF.</summary>
    private sealed class Session(Stream stream)
    {
        private readonly StreamReader _reader = new(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        private readonly Stream _stream = stream;

        public async Task WriteLineAsync(string line, CancellationToken ct)
        {
            var bytes = new UTF8Encoding(false).GetBytes(line + "\r\n");
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }

        public async Task<SmtpReply> CommandAsync(string line, CancellationToken ct)
        {
            await WriteLineAsync(line, ct).ConfigureAwait(false);
            return await ReadAsync(ct).ConfigureAwait(false);
        }

        public async Task<SmtpReply> ReadAsync(CancellationToken ct)
        {
            var lines = new List<string>();
            var code = 0;
            while (true)
            {
                var line = await _reader.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new IOException("The server closed the connection.");
                if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), out code)) throw new IOException($"The server sent something that is not an SMTP reply: '{(line.Length > 60 ? line[..60] : line)}'.");
                lines.Add(line.Length > 4 ? line[4..] : "");
                if (line.Length == 3 || line[3] != '-') break;
            }

            return new SmtpReply(code, string.Join('\n', lines));
        }

        /// <summary>The message, dot-stuffed, ended with a line of one dot.</summary>
        public async Task WriteDataAsync(string message, CancellationToken ct)
        {
            var text = message.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\n", "\r\n", StringComparison.Ordinal);
            if (text.StartsWith('.')) text = "." + text;
            text = text.Replace("\r\n.", "\r\n..", StringComparison.Ordinal);
            if (!text.EndsWith("\r\n", StringComparison.Ordinal)) text += "\r\n";
            var bytes = new UTF8Encoding(false).GetBytes(text + ".\r\n");
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }
}
