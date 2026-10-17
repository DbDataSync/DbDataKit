using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using CertificateExpiry = DbDataKit.Hosting.Certificates.CertificateExpiry;
using DbDataKit.Hosting.Cli;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Tls;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// <c>DbDataProxy cert show|ensure|renew|check [--name SERVICE]</c>: the shared certificate command pointed at the
/// proxy's configured certificate. <c>show</c> and <c>check</c> never change anything.
/// </summary>
public static class CertCli
{
    public static int Run(string[] args, ProxyOptions options, string dataDirectory, IConsole console, TimeProvider? time = null)
    {
        var rest = new List<string>();
        string serviceName = ProxyHost.ServiceName;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--name" && i + 1 < args.Length) serviceName = args[++i];
            else if (args[i].StartsWith("--name=", StringComparison.Ordinal)) serviceName = args[i]["--name=".Length..];
            else rest.Add(args[i]);
        }

        var tls = options.Tls;
        string verb = rest.FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (tls.Mode == TlsMode.None)
        {
            console.Out.WriteLine("TLS is disabled (Proxy:Tls:Mode is None); there is no certificate.");
            return verb is "check" ? ExitCodes.Ok : ExitCodes.Failure;
        }

        if (tls.Mode == TlsMode.File)
            return FileMode(rest, tls, console, time);

        var setup = CertificateProvider.CreateSetup(tls, dataDirectory, serviceName, time);
        var command = new CertCommand(setup.Options, setup.Secrets, setup.Provider, time);

        switch (verb)
        {
            case "show" or "status" or "check":
                // A PFX from an earlier version has an empty password and no password file yet: show it as it is.
                if (IsUnprotected(setup, out var legacy))
                {
                    using (legacy)
                    {
                        Describe(legacy!, console, time);
                        console.Out.WriteLine("Note: this certificate file has an empty password. It is re-encrypted with a random protected password at the next start or 'cert ensure'.");
                        return verb == "check" && CertificateProvider.NeedsRotation(legacy!, time) ? ExitCodes.Failure : ExitCodes.Ok;
                    }
                }

                return command.Execute(rest.ToArray(), console);

            case "ensure" or "renew":
                CertificateProvider.MigrateUnprotectedPfx(setup, NullLogger.Instance, ProxyAcl.KeyFileIdentities(serviceName));
                string? directory = Path.GetDirectoryName(Path.GetFullPath(setup.PfxPath));
                if (directory is not null && !Directory.Exists(directory))
                    DbDataKit.Security.Acl.AclHelper.EnsureRestrictedDirectory(directory, ProxyAcl.KeyFileIdentities(serviceName));
                int rc = command.Execute(rest.ToArray(), console);
                if (rc == 0) console.Out.WriteLine("A running proxy picks up the new certificate within hours, or at its next start.");
                return rc;

            default:
                console.Error.WriteLine(CertCommand.Usage);
                return ExitCodes.Usage;
        }
    }

    private static int FileMode(List<string> args, TlsOptions tls, IConsole console, TimeProvider? time)
    {
        if (string.IsNullOrWhiteSpace(tls.CertificatePath))
        {
            console.Error.WriteLine("Proxy:Tls:CertificatePath is required when Proxy:Tls:Mode is File.");
            return ExitCodes.Failure;
        }

        string verb = args.FirstOrDefault()?.ToLowerInvariant() ?? "";
        if (verb is "ensure" or "renew")
        {
            console.Error.WriteLine("The certificate is supplied from a file (Proxy:Tls:Mode is File); replace that file to change it.");
            return ExitCodes.Usage;
        }

        if (verb is not ("show" or "status" or "check"))
        {
            console.Error.WriteLine(CertCommand.Usage);
            return ExitCodes.Usage;
        }

        try
        {
            using var cert = X509CertificateLoader.LoadPkcs12FromFile(tls.CertificatePath, tls.CertificatePassword, X509KeyStorageFlags.EphemeralKeySet);
            Describe(cert, console, time);
            return verb == "check" && CertificateProvider.NeedsRotation(cert, time) ? ExitCodes.Failure : ExitCodes.Ok;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException)
        {
            console.Out.WriteLine($"Cannot open {tls.CertificatePath}: {ex.Message}");
            return ExitCodes.Failure;
        }
    }

    private static bool IsUnprotected(CertificateProvider.SelfSignedSetup setup, out X509Certificate2? legacy)
    {
        legacy = null;
        if (setup.PasswordConfigured || !File.Exists(setup.PfxPath) || File.Exists(setup.PasswordFile)) return false;
        try
        {
            legacy = X509CertificateLoader.LoadPkcs12FromFile(setup.PfxPath, null, X509KeyStorageFlags.EphemeralKeySet);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static void Describe(X509Certificate2 cert, IConsole console, TimeProvider? time)
    {
        var status = CertificateExpiry.Evaluate(cert, (time ?? TimeProvider.System).GetUtcNow(), CertificateProvider.RenewBeforeDays);
        console.Out.WriteLine($"Subject:    {cert.Subject}");
        console.Out.WriteLine($"Thumbprint: {status.Thumbprint}");
        console.Out.WriteLine($"Expires:    {status.NotAfter:u} ({status.DaysRemaining} day(s)) - {status.State}");
    }
}
