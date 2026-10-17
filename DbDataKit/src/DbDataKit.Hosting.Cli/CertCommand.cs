using System.Security.Cryptography.X509Certificates;
using DbDataKit.Hosting.Certificates;

namespace DbDataKit.Hosting.Cli;

/// <summary><c>cert status|ensure|check</c> over a <see cref="CertificateOptions"/>.</summary>
public sealed class CertCommand(
    CertificateOptions options,
    ICertificateSecretSource secrets,
    CertificateProvider provider,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public const string Usage = "Usage: cert <status|ensure|check>\n  status  show the certificate in use (never creates or changes anything)\n  ensure  create or rotate the managed certificate if needed\n  check   exit 1 when rotation is due or the certificate is missing";

    public int Execute(string[] args, IConsole console) => ExecuteAsync(args, console).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(string[] args, IConsole console)
    {
        switch (args.FirstOrDefault()?.ToLowerInvariant())
        {
            case "status":
            case "check":
                var cert = TryLoadExisting();
                if (cert is null)
                {
                    console.Out.WriteLine($"No certificate present for mode {options.Mode}.");
                    return ExitCodes.Failure;
                }

                using (cert)
                {
                    var s = CertificateExpiry.Evaluate(cert, _time.GetUtcNow(), options.RenewBeforeDays);
                    console.Out.WriteLine($"Subject:    {cert.Subject}");
                    console.Out.WriteLine($"Thumbprint: {s.Thumbprint}");
                    console.Out.WriteLine($"Expires:    {s.NotAfter:u} ({s.DaysRemaining} day(s)) - {s.State}");
                    return args[0].Equals("check", StringComparison.OrdinalIgnoreCase) && s.NeedsRotation ? ExitCodes.Failure : ExitCodes.Ok;
                }

            case "ensure":
                using (var created = await provider.LoadAsync(options))
                {
                    console.Out.WriteLine($"Certificate ready: {created.Thumbprint}");
                    if (options.Mode == CertificateMode.SelfSigned)
                    {
                        console.Out.WriteLine($"Public certificate for client trust: {options.PublicCertificatePath ?? Path.ChangeExtension(options.Path!, ".cer")}");
                    }

                    return ExitCodes.Ok;
                }

            default:
                console.Error.WriteLine(Usage);
                return ExitCodes.Usage;
        }
    }

    /// <summary>Read-only load: no generation, no password creation.</summary>
    private X509Certificate2? TryLoadExisting()
    {
        switch (options.Mode)
        {
            case CertificateMode.SelfSigned:
            case CertificateMode.File:
                if (options.Path is null || !File.Exists(options.Path)) return null;
                if (options.Mode == CertificateMode.File && options.Path.EndsWith(".pem", StringComparison.OrdinalIgnoreCase)) break;
                var name = options.PasswordSecretName ?? (options.Mode == CertificateMode.SelfSigned ? "tls-pfx-password" : null);
                var password = name is null ? null : secrets.GetOrCreatePassword(name, () => throw new InvalidOperationException("No stored password for the certificate."));
                return X509CertificateLoader.LoadPkcs12FromFile(options.Path, password);
        }

        try
        {
            return provider.LoadAsync(options).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException)
        {
            return null;
        }
    }
}
