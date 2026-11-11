using System.Net.Mail;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace DbDataKit.Delivery.Email;

/// <summary>
/// The email channel (plan 22a GP08): one message becomes one mail to its recipients over SMTP with STARTTLS (587) or implicit TLS (465), with the sender address of the channel and the login from the secrets store.
/// <list type="bullet">
/// <item>A recipient the server refuses with 5xx parks the message with the server's own words ("550 5.1.1 no such user"); 4xx is temporary. Nothing is sent when any recipient is refused (the conversation stops before DATA).</item>
/// <item>A certificate that does not match the server name, or does not chain to a trusted root, is refused: a permanent failure that says which (<c>trusted_certificate</c> names a private CA's file; the name is checked anyway). There is no setting that turns the check off.</item>
/// <item>Credentials are sent only over TLS, unless <c>tls: none</c> is set on purpose; <c>password</c> is a secret reference resolved for each send.</item>
/// <item>An address that is not one, or a line break in the subject, is the message's own fault: permanent. Network errors and timeouts are temporary.</item>
/// </list>
/// </summary>
public sealed class EmailChannel : IDeliveryChannel
{
    public const string KindName = "email";

    private readonly ChannelContext _context;
    private readonly MailAddress _from;
    private readonly SmtpSettings _smtp;

    public EmailChannel(ChannelContext context)
    {
        _context = context;
        var config = context.Config;
        _from = MailMessageBuilder.Address(config.Text("from") ?? throw new DeliverySettingsException($"delivery.channels.{config.Name}.from is required."));
        var tls = (config.Text("tls") ?? "starttls") switch
        {
            "starttls" => SmtpTls.StartTls,
            "implicit" => SmtpTls.Implicit,
            "none" => SmtpTls.None,
            var other => throw new DeliverySettingsException($"delivery.channels.{config.Name}.tls is `starttls`, `implicit` or `none`, not `{other}`."),
        };
        var port = config.Whole("port") ?? tls switch { SmtpTls.Implicit => 465, SmtpTls.StartTls => 587, _ => 25 };
        X509Certificate2? trusted = null;
        if (config.Text("trusted_certificate") is { } file)
        {
            try { trusted = X509CertificateLoader.LoadCertificateFromFile(file); }
            catch (Exception e) when (e is IOException or System.Security.Cryptography.CryptographicException)
            {
                throw new DeliverySettingsException($"delivery.channels.{config.Name}.trusted_certificate: {file} is not a certificate file that can be read ({e.Message}).");
            }
        }

        _smtp = new SmtpSettings(config.Text("host")!, port, tls, config.Text("helo") ?? "dbdatakit", TimeSpan.FromSeconds(config.Whole("timeout_seconds") ?? 30), config.Text("username"), trusted);
    }

    public string Name => _context.Config.Name;

    public string Kind => KindName;

    public int? LimitPerMinute => _context.Config.LimitPerMinute;

    /// <summary>The channel kind for <see cref="ChannelKinds"/>.</summary>
    public static ChannelKind Describe { get; } = new(KindName,
    [
        new ChannelKey("host", Required: true, Description: "The mail server's name; the certificate must be for it."),
        new ChannelKey("port", ChannelKeyType.Whole, Description: "587 for starttls, 465 for implicit, 25 for none."),
        new ChannelKey("tls", Description: "starttls (default), implicit or none."),
        new ChannelKey("from", Required: true, Description: "The sender address: ana@example.com or Name <ana@example.com>."),
        new ChannelKey("username"),
        new ChannelKey("password", ChannelKeyType.Secret, Description: "A reference to a secret."),
        new ChannelKey("helo", Description: "The name the client gives in EHLO (default dbdatakit)."),
        new ChannelKey("timeout_seconds", ChannelKeyType.Whole),
        new ChannelKey("trusted_certificate", Description: "A certificate file (PEM or DER) the server's chain is checked against instead of the machine's roots."),
    ], c => new EmailChannel(c), "Mail over SMTP.");

    public async Task<DeliveryResult> SendAsync(DeliveryPayload payload, CancellationToken cancellationToken)
    {
        string text;
        List<string> recipients;
        try
        {
            text = MailMessageBuilder.Build(payload, _from, _context.Time.GetUtcNow(), _from.Host);
            recipients = [.. payload.Recipients.Select(r => MailMessageBuilder.Address(r).Address)];
        }
        catch (MailFormatException e)
        {
            return DeliveryResult.Permanent(e.Message);
        }

        var password = _smtp.Username is null ? null : await _context.SecretAsync("password", cancellationToken).ConfigureAwait(false);
        if (_smtp.Username is not null && password is null) return DeliveryResult.Permanent($"The channel '{Name}' has a username and no password: set `password` to a secret reference.");

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_smtp.Timeout);
        try
        {
            await SmtpClientLite.SendAsync(_smtp, password, _from.Address, recipients, text, deadline.Token).ConfigureAwait(false);
            return DeliveryResult.Sent($"250 accepted by {_smtp.Host} for {recipients.Count} recipient{(recipients.Count == 1 ? "" : "s")}");
        }
        catch (SmtpRefusedException e)
        {
            return e.Permanent ? DeliveryResult.Permanent(e.Reply + $" (in answer to {e.Step})") : DeliveryResult.Temporary(e.Reply + $" (in answer to {e.Step})");
        }
        catch (SmtpTlsException e)
        {
            return DeliveryResult.Permanent(e.Message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DeliveryResult.Temporary($"The mail server {_smtp.Host}:{_smtp.Port} did not finish within {_smtp.Timeout.TotalSeconds:0} seconds.");
        }
        catch (Exception e) when (e is IOException or SocketException)
        {
            return DeliveryResult.Temporary($"The connection to {_smtp.Host}:{_smtp.Port} failed: {e.Message}");
        }
    }
}
