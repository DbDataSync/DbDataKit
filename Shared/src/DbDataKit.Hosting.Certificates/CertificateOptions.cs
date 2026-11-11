using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace DbDataKit.Hosting.Certificates;

public enum CertificateMode
{
    /// <summary>PFX, or PEM certificate plus key file.</summary>
    File,

    /// <summary>Windows certificate store (LocalMachine\My by default), by thumbprint or subject.</summary>
    Store,

    /// <summary>Enterprise CA enrolment (ADCS). Interface only here; implemented by the host product.</summary>
    Enroll,

    /// <summary>Managed self-signed certificate, regenerated before expiry.</summary>
    SelfSigned,
}

public sealed class CertificateOptions
{
    public CertificateMode Mode { get; set; } = CertificateMode.SelfSigned;

    // File
    public string? Path { get; set; }

    /// <summary>PEM private key path when <see cref="Path"/> is a PEM certificate.</summary>
    public string? KeyPath { get; set; }

    /// <summary>Name under which the PFX password is stored in the secret source (File and SelfSigned).</summary>
    public string? PasswordSecretName { get; set; }

    // Store
    public string? Thumbprint { get; set; }

    public string? SubjectName { get; set; }

    public StoreLocation StoreLocation { get; set; } = StoreLocation.LocalMachine;

    public StoreName StoreName { get; set; } = StoreName.My;

    // Enroll
    public string? Template { get; set; }

    public string? CertificateAuthority { get; set; }

    // SelfSigned
    public IList<string> AdditionalHostNames { get; } = [];

    public int ValidityDays { get; set; } = 365;

    /// <summary>Regenerate (SelfSigned) / warn (all modes) when fewer than this many days remain.</summary>
    public int RenewBeforeDays { get; set; } = 30;

    /// <summary>Where the public <c>.cer</c> for client trust is written (SelfSigned). Defaults next to the PFX.</summary>
    public string? PublicCertificatePath { get; set; }
}

/// <summary>
/// The injected secret callback. The PFX password is generated randomly, stored through this (Plan 02 secret
/// store in products) and read back on the next start. Returning null or empty is a bug and is rejected.
/// </summary>
public interface ICertificateSecretSource
{
    /// <summary>Return the stored password for <paramref name="name"/>, or create (via <paramref name="generate"/>), persist and return it.</summary>
    string GetOrCreatePassword(string name, Func<string> generate);
}

public sealed class DelegateSecretSource(Func<string, Func<string>, string> callback) : ICertificateSecretSource
{
    public string GetOrCreatePassword(string name, Func<string> generate) => callback(name, generate);
}

/// <summary>Non-persistent source for tests and one-shot tools.</summary>
public sealed class InMemorySecretSource : ICertificateSecretSource
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    public string GetOrCreatePassword(string name, Func<string> generate)
    {
        if (!Values.TryGetValue(name, out var v))
        {
            Values[name] = v = generate();
        }

        return v;
    }
}

public static class PfxPassword
{
    /// <summary>32 random bytes, base64url (no padding); about 256 bits.</summary>
    public static string Generate() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static string Resolve(ICertificateSecretSource source, string name)
    {
        var value = source.GetOrCreatePassword(name, Generate);
        if (string.IsNullOrEmpty(value))
        {
            throw new InvalidOperationException($"The secret source returned no password for '{name}'; an empty PFX password is not allowed.");
        }

        return value;
    }
}

public sealed record EnrollRequest(string? Template, string? CertificateAuthority, IReadOnlyList<string> DnsNames);

/// <summary>ADCS (or any CA) enrolment. Implemented by the product (Sync has one); not provided here.</summary>
public interface ICertificateEnroller
{
    Task<X509Certificate2> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken);
}
