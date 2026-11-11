using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace DbDataKit.Delivery.Tests;

/// <summary>A message the fake server took.</summary>
public sealed record ReceivedMail(string From, IReadOnlyList<string> To, string Raw, bool OverTls, string? Login);

/// <summary>A self-signed certificate for the tests' TLS: its name is what the client must be connecting to.</summary>
public static class TestCertificates
{
    public static X509Certificate2 Make(string dnsName)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest($"CN={dnsName}", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(dnsName);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));   // its own root
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    /// <summary>The certificate's public part as a PEM file in <paramref name="folder"/>.</summary>
    public static string WritePem(X509Certificate2 certificate, string folder, string name = "trusted.pem")
    {
        var path = Path.Combine(folder, name);
        File.WriteAllText(path, certificate.ExportCertificatePem());
        return path;
    }
}

/// <summary>
/// An SMTP server in the test's own process (a <see cref="TcpListener"/> on the loopback address): EHLO, STARTTLS or implicit TLS, AUTH PLAIN and LOGIN, MAIL, RCPT, DATA, RSET, QUIT. It refuses the recipients it is told to with the
/// replies it is told to, and keeps what it received.
/// </summary>
public sealed class FakeSmtpServer : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly X509Certificate2? _certificate;
    private readonly bool _implicitTls;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public FakeSmtpServer(X509Certificate2? certificate = null, bool implicitTls = false, bool offerStartTls = true)
    {
        _certificate = certificate;
        _implicitTls = implicitTls;
        OfferStartTls = offerStartTls;
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public bool OfferStartTls { get; }

    /// <summary>The replies for recipients: an address (compared ignoring case) to the whole reply line.</summary>
    public Dictionary<string, string> RecipientReplies { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The login the server accepts (user, password); null accepts none: AUTH is refused with 535.</summary>
    public (string User, string Password)? AcceptedLogin { get; set; }

    /// <summary>A reply to give to DATA's end instead of 250 (a mailbox that is full: <c>452 4.2.2 mailbox full</c>).</summary>
    public string? EndOfDataReply { get; set; }

    public List<ReceivedMail> Received { get; } = [];

    public List<string> Commands { get; } = [];

    private async Task AcceptAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            Stream stream = client.GetStream();
            var tls = false;
            if (_implicitTls)
            {
                stream = await UpgradeAsync(stream);
                tls = true;
            }

            var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true);
            async Task Say(string line)
            {
                var bytes = Encoding.UTF8.GetBytes(line + "\r\n");
                await stream.WriteAsync(bytes);
                await stream.FlushAsync();
            }

            await Say("220 fake.test ESMTP ready");
            string? from = null;
            var to = new List<string>();
            string? login = null;
            while (await reader.ReadLineAsync() is { } line)
            {
                lock (Commands) Commands.Add(line);
                var verb = line.Split(' ')[0].ToUpperInvariant();
                switch (verb)
                {
                    case "EHLO":
                        var extensions = new List<string> { "fake.test greets you", "8BITMIME" };
                        if (OfferStartTls && !tls && _certificate is not null) extensions.Add("STARTTLS");
                        if (tls || _certificate is null) extensions.Add("AUTH PLAIN LOGIN");
                        for (var i = 0; i < extensions.Count; i++) await Say($"250{(i == extensions.Count - 1 ? " " : "-")}{extensions[i]}");
                        break;
                    case "STARTTLS":
                        await Say("220 go ahead");
                        stream = await UpgradeAsync(stream);
                        reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, true);
                        tls = true;
                        break;
                    case "AUTH":
                        var parts = line.Split(' ');
                        string? user = null, password = null;
                        if (parts[1].Equals("PLAIN", StringComparison.OrdinalIgnoreCase))
                        {
                            var fields = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2])).Split('\0');
                            (user, password) = (fields[1], fields[2]);
                        }
                        else
                        {
                            await Say("334 VXNlcm5hbWU6");
                            user = Encoding.UTF8.GetString(Convert.FromBase64String((await reader.ReadLineAsync())!));
                            await Say("334 UGFzc3dvcmQ6");
                            password = Encoding.UTF8.GetString(Convert.FromBase64String((await reader.ReadLineAsync())!));
                        }

                        if (AcceptedLogin is { } accepted && accepted.User == user && accepted.Password == password) { login = user; await Say("235 2.7.0 authenticated"); }
                        else await Say($"535 5.7.8 authentication failed for {user} with {password}");
                        break;
                    case "MAIL":
                        from = line[(line.IndexOf('<') + 1)..line.IndexOf('>')];
                        to.Clear();
                        await Say("250 2.1.0 sender ok");
                        break;
                    case "RCPT":
                        var rcpt = line[(line.IndexOf('<') + 1)..line.IndexOf('>')];
                        if (RecipientReplies.TryGetValue(rcpt, out var reply)) await Say(reply);
                        else { to.Add(rcpt); await Say("250 2.1.5 recipient ok"); }
                        break;
                    case "DATA":
                        await Say("354 end with <CRLF>.<CRLF>");
                        var raw = new StringBuilder();
                        while (await reader.ReadLineAsync() is { } data && data != ".") raw.Append(data.StartsWith("..") ? data[1..] : data).Append("\r\n");
                        if (EndOfDataReply is { } refusal) { await Say(refusal); break; }
                        lock (Received) Received.Add(new ReceivedMail(from ?? "", [.. to], raw.ToString(), tls, login));
                        await Say("250 2.0.0 queued");
                        break;
                    case "RSET":
                        await Say("250 ok");
                        break;
                    case "QUIT":
                        await Say("221 bye");
                        return;
                    default:
                        await Say("502 command not implemented");
                        break;
                }
            }
        }
        catch (Exception)
        {
            // a client that walked away (a refused certificate) is not an error of the server
        }
    }

    private async Task<SslStream> UpgradeAsync(Stream inner)
    {
        var ssl = new SslStream(inner, false);
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = _certificate, EnabledSslProtocols = SslProtocols.None });
        return ssl;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* stopped */ }
        _certificate?.Dispose();
    }
}
