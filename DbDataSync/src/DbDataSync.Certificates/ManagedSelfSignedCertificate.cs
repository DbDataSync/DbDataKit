using DbDataKit.Hosting.Certificates.Windows;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Security.Acl;

namespace DbDataSync.Certificates;

/// <summary>
/// Phase 130 — tier 2's cross-platform self-signed certificate: generation, storage, and the renewal
/// decision, shared by <c>dbdatasync cert new-self-signed</c>'s non-Windows branch (the CLI),
/// <c>SelfSignedCertificateService</c> (the daily renewal check), and <c>CertificateCheck</c> (the
/// marker that distinguishes a certificate this codebase manages from one bound by accident).
/// <para>
/// **One well-known path, deliberately.** Everything here keys off <see cref="PfxPath"/> — the same
/// file every caller reads, writes, or compares <c>Kestrel:Certificates:Default:Path</c> against — so
/// there is exactly one place "is this the managed certificate" can ever disagree with itself.
/// </para>
/// <para>
/// Generation, rotation, and the password are the shared <c>DbDataKit.Hosting.Certificates</c> ones: the PFX is
/// encrypted with a random password kept beside it in <see cref="PasswordPath"/> (DPAPI on Windows, mode 0600
/// elsewhere), and both files are restricted to the service identities. The password never enters configuration
/// files, which are committed to git; the host adds it to Kestrel's configuration in memory at start.
/// </para>
/// </summary>
public static class ManagedSelfSignedCertificate
{
    private const string RelativeDirectory = "tls";
    private const string FileName = "dbdatasync.pfx";

    /// <summary>Renew when fewer than this many days remain (the shared default).</summary>
    public const int RenewBeforeDays = 30;

    /// <summary>Lifetime of a certificate when there is no earlier one to copy it from.</summary>
    public const int DefaultValidityDays = 397;

    public const string ServiceName = "DbDataSync";

    /// <summary>Full, normalized path to the managed certificate for a given repo root. Every caller
    /// passes whatever repo root it already resolved (a CLI's <c>--repo</c>, the API's
    /// <c>ApiOptions.RepoRoot</c>) through this rather than building the path itself, so a difference in
    /// path separators or a trailing slash can never make two callers disagree about whether a
    /// configured <c>Path</c> is this one.</summary>
    public static string PfxPath(string repoRoot) =>
        Path.GetFullPath(Path.Combine(repoRoot, RelativeDirectory, FileName));

    /// <summary>Where the PFX's random password is kept — next to the PFX, inside the git-ignored directory.</summary>
    public static string PasswordPath(string repoRoot) => PfxPath(repoRoot) + ".pwd";

    /// <summary>The identities allowed to read the key files: the person and tools administering the machine, plus
    /// the account the installed service runs as when that is something else.</summary>
    public static IReadOnlyList<string> KeyFileIdentities()
    {
        var identities = new List<string> { "CurrentUser", "SYSTEM", "Administrators" };
        if (OperatingSystem.IsWindows())
        {
            var account = InstalledServiceAccount.Resolve(ServiceName);
            if (!string.IsNullOrWhiteSpace(account) && !identities.Contains(account, StringComparer.OrdinalIgnoreCase))
                identities.Add(account);
        }

        return identities;
    }

    private static ProtectedPasswordFile PasswordStore(string repoRoot) =>
        new(PasswordPath(repoRoot), KeyFileIdentities());

    /// <summary>The PFX password for <paramref name="path"/> when it is the managed certificate and its password
    /// file exists; otherwise null (any other path, or a file from before the password was introduced).</summary>
    public static string? PasswordFor(string repoRoot, string path) =>
        string.Equals(Path.GetFullPath(path), PfxPath(repoRoot), StringComparison.OrdinalIgnoreCase)
        && PasswordStore(repoRoot).TryRead(out var password)
            ? password
            : null;

    private static CertificateProvider Provider(string repoRoot, TimeProvider? time)
    {
        var identities = KeyFileIdentities();
        return new CertificateProvider(PasswordStore(repoRoot), time: time, restrictFileAccess: file => AclHelper.Restrict(file, identities));
    }

    private static CertificateOptions Options(string repoRoot, string host, int validityDays)
    {
        var options = new CertificateOptions
        {
            Mode = CertificateMode.SelfSigned,
            Path = PfxPath(repoRoot),
            ValidityDays = validityDays,
            RenewBeforeDays = RenewBeforeDays,
        };
        options.AdditionalHostNames.Add(host);
        return options;
    }

    /// <summary>Replaces the managed certificate now, whatever its remaining validity. SANs are the machine's
    /// names, <c>localhost</c>, loopback and <paramref name="host"/> (the console URL's host).</summary>
    public static X509Certificate2 Renew(string repoRoot, string host, int validityDays, TimeProvider? time = null)
    {
        EnsureDirectory(repoRoot);
        var certificate = Provider(repoRoot, time).RenewSelfSigned(Options(repoRoot, host, validityDays));
        EnsureGitignored(repoRoot);
        return certificate;
    }

    /// <summary>The managed certificate, replaced first when it is within <see cref="RenewBeforeDays"/> of
    /// expiring, no longer covers <paramref name="host"/>, or cannot be opened with the stored password (for
    /// example a file written before that password existed).</summary>
    public static X509Certificate2 LoadOrRenew(string repoRoot, string host, int validityDays, out bool renewed, TimeProvider? time = null)
    {
        var path = PfxPath(repoRoot);
        var before = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
        EnsureDirectory(repoRoot);
        var certificate = Provider(repoRoot, time).LoadAsync(Options(repoRoot, host, validityDays)).GetAwaiter().GetResult();
        renewed = before is null || File.GetLastWriteTimeUtc(path) != before;
        if (renewed)
            EnsureGitignored(repoRoot);
        return certificate;
    }

    /// <summary>
    /// A certificate with the managed shape that is not stored anywhere: SANs are <paramref name="host"/> plus
    /// <c>localhost</c>, made by the shared generator.
    /// </summary>
    public static X509Certificate2 Generate(string host, int validityDays)
    {
        var dnsNames = new List<string> { host };
        if (!dnsNames.Contains("localhost", StringComparer.OrdinalIgnoreCase))
            dnsNames.Add("localhost");

        var now = DateTimeOffset.UtcNow;
        return SelfSignedCertificateGenerator.Create(new SelfSignedRequest(
            $"CN={host}", dnsNames, [], now.AddMinutes(-5), now.AddDays(validityDays)));
    }

    /// <summary>
    /// Stores <paramref name="certificate"/> as the managed certificate: encrypted with the random password
    /// (created on first use), restricted to the service identities, and the containing directory git-ignored the
    /// first time it's used — the same lazy, first-use pattern <see cref="PendingEnrollmentStore"/> already
    /// established for a file this codebase deliberately never commits.
    /// </summary>
    public static void Write(string repoRoot, X509Certificate2 certificate)
    {
        EnsureDirectory(repoRoot);
        var password = PasswordStore(repoRoot).GetOrCreatePassword("pfx", PfxPassword.Generate);
        AclHelper.WriteAllBytesRestricted(PfxPath(repoRoot), certificate.Export(X509ContentType.Pfx, password), KeyFileIdentities());
        EnsureGitignored(repoRoot);
    }

    private static void EnsureDirectory(string repoRoot) =>
        AclHelper.EnsureRestrictedDirectory(Path.GetDirectoryName(PfxPath(repoRoot))!, KeyFileIdentities());

    /// <summary>
    /// The repo root is a git repository (<c>ServeCommand.Prepare</c>), and this directory is
    /// deliberately never committed to it — a private key has no business in version control. Appended
    /// once, the first time a managed certificate is actually written, rather than by
    /// <c>ServeCommand.Prepare</c> up front — a repo that never generates one never needs the line.
    /// </summary>
    private static void EnsureGitignored(string repoRoot)
    {
        const string entry = $"{RelativeDirectory}/";
        var path = Path.Combine(repoRoot, ".gitignore");
        var existing = File.Exists(path) ? File.ReadAllLines(path) : [];
        if (existing.Contains(entry))
            return;

        using var writer = new StreamWriter(path, append: true);
        if (existing.Length > 0 && !string.IsNullOrEmpty(existing[^1]))
            writer.WriteLine();
        writer.WriteLine(entry);
    }
}
