using System.Security.Cryptography.X509Certificates;

namespace DbDataKit.Hosting.Certificates;

/// <summary>Loads (or creates) the server certificate for the configured mode.</summary>
public sealed class CertificateProvider(
    ICertificateSecretSource secrets,
    ICertificateEnroller? enroller = null,
    TimeProvider? time = null,
    Action<string>? restrictFileAccess = null,
    Func<string, string?>? fqdnLookup = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<X509Certificate2> LoadAsync(CertificateOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return options.Mode switch
        {
            CertificateMode.File => LoadFile(options),
            CertificateMode.Store => LoadStore(options),
            CertificateMode.Enroll => await EnrollAsync(options, cancellationToken).ConfigureAwait(false),
            CertificateMode.SelfSigned => LoadOrCreateSelfSigned(options),
            _ => throw new ArgumentOutOfRangeException(nameof(options)),
        };
    }

    private X509Certificate2 LoadFile(CertificateOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Path)) throw new InvalidOperationException("Certificate File mode needs a Path.");
        var ext = System.IO.Path.GetExtension(o.Path).ToLowerInvariant();
        if (ext is ".pfx" or ".p12")
        {
            var password = o.PasswordSecretName is null ? null : PfxPassword.Resolve(secrets, o.PasswordSecretName);
            return X509CertificateLoader.LoadPkcs12FromFile(o.Path, password);
        }

        if (o.KeyPath is null) throw new InvalidOperationException("A PEM certificate needs KeyPath.");
        var password2 = o.PasswordSecretName is null ? null : PfxPassword.Resolve(secrets, o.PasswordSecretName);
        using var pem = password2 is null
            ? X509Certificate2.CreateFromPemFile(o.Path, o.KeyPath)
            : X509Certificate2.CreateFromEncryptedPemFile(o.Path, password2, o.KeyPath);
        // Windows (SChannel) needs the key in a persisted form; the round trip gives that on every OS.
        return X509CertificateLoader.LoadPkcs12(pem.Export(X509ContentType.Pfx, ""), "");
    }

    private static X509Certificate2 LoadStore(CertificateOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Thumbprint) && string.IsNullOrWhiteSpace(o.SubjectName))
        {
            throw new InvalidOperationException("Certificate Store mode needs a Thumbprint or SubjectName.");
        }

        using var store = new X509Store(o.StoreName, o.StoreLocation);
        store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);
        // validOnly=false: chain trust is not our concern (self-signed/enterprise roots); dates are checked here.
        var matches = !string.IsNullOrWhiteSpace(o.Thumbprint)
            ? store.Certificates.Find(X509FindType.FindByThumbprint, NormalizeThumbprint(o.Thumbprint), validOnly: false)
            : store.Certificates.Find(X509FindType.FindBySubjectName, o.SubjectName!, validOnly: false);
        var now = DateTime.Now;
        var best = matches.Cast<X509Certificate2>()
            .Where(c => c.HasPrivateKey && c.NotBefore <= now && now < c.NotAfter)
            .OrderByDescending(c => c.NotAfter).FirstOrDefault()
            ?? throw new InvalidOperationException("No valid certificate with a private key matched in the store.");
        return best;
    }

    public static string NormalizeThumbprint(string t) =>
        new string(t.Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

    private async Task<X509Certificate2> EnrollAsync(CertificateOptions o, CancellationToken ct)
    {
        if (enroller is null) throw new NotSupportedException("Enroll mode needs an ICertificateEnroller supplied by the product.");
        var names = SelfSignedCertificateGenerator.Discover(o.AdditionalHostNames, _time, o.ValidityDays, fqdnLookup).DnsNames;
        return await enroller.EnrollAsync(new EnrollRequest(o.Template, o.CertificateAuthority, names), ct).ConfigureAwait(false);
    }

    private X509Certificate2 LoadOrCreateSelfSigned(CertificateOptions o)
    {
        if (string.IsNullOrWhiteSpace(o.Path)) throw new InvalidOperationException("SelfSigned mode needs a Path for the managed PFX.");
        var secretName = o.PasswordSecretName ?? "tls-pfx-password";
        var password = PfxPassword.Resolve(secrets, secretName);
        var request = SelfSignedCertificateGenerator.Discover(o.AdditionalHostNames, _time, o.ValidityDays, fqdnLookup);

        if (File.Exists(o.Path))
        {
            try
            {
                var existing = X509CertificateLoader.LoadPkcs12FromFile(o.Path, password);
                var status = CertificateExpiry.Evaluate(existing, _time.GetUtcNow(), o.RenewBeforeDays);
                if (!status.NeedsRotation && CoversNames(existing, request))
                {
                    return existing;
                }

                existing.Dispose();
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                // Unreadable or password changed: replace it.
            }
        }

        var created = SelfSignedCertificateGenerator.Create(request);
        Persist(created, o, password);
        return created;
    }

    /// <summary>
    /// Replaces the managed self-signed certificate now, whatever its remaining validity (<c>cert renew</c>).
    /// The new PFX uses the stored password and goes through the same restrict-then-move write as automatic rotation.
    /// </summary>
    public X509Certificate2 RenewSelfSigned(CertificateOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Mode != CertificateMode.SelfSigned) throw new NotSupportedException("Only managed self-signed certificates can be renewed here.");
        if (string.IsNullOrWhiteSpace(options.Path)) throw new InvalidOperationException("SelfSigned mode needs a Path for the managed PFX.");
        var password = PfxPassword.Resolve(secrets, options.PasswordSecretName ?? "tls-pfx-password");
        var created = SelfSignedCertificateGenerator.Create(
            SelfSignedCertificateGenerator.Discover(options.AdditionalHostNames, _time, options.ValidityDays, fqdnLookup));
        Persist(created, options, password);
        return created;
    }

    private static bool CoversNames(X509Certificate2 cert, SelfSignedRequest wanted)
    {
        var (dns, ips) = SelfSignedCertificateGenerator.ReadSans(cert);
        return wanted.DnsNames.All(dns.Contains) && wanted.IpAddresses.All(ips.Contains);
    }

    private void Persist(X509Certificate2 cert, CertificateOptions o, string password)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(o.Path!))!;
        Directory.CreateDirectory(dir);

        // Write to a temp file in the same directory, restrict it, then move into place.
        var temp = System.IO.Path.Combine(dir, $".{Guid.NewGuid():N}.tmp");
        File.WriteAllBytes(temp, cert.Export(X509ContentType.Pfx, password));
        restrictFileAccess?.Invoke(temp);
        File.Move(temp, o.Path!, overwrite: true);

        var cerPath = o.PublicCertificatePath ?? System.IO.Path.ChangeExtension(o.Path!, ".cer");
        File.WriteAllBytes(cerPath, cert.Export(X509ContentType.Cert));
    }
}
