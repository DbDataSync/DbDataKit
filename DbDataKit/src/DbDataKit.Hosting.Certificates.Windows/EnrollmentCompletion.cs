using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace DbDataKit.Hosting.Certificates.Windows;

public enum CompletionOutcome
{
    /// <summary>The certificate is in the store and its pending record, if any, is gone.</summary>
    Installed,

    /// <summary>The CA needs approval first; the request id and key are saved for <c>retrieve</c>.</summary>
    Pending,

    /// <summary>The CA denied or failed the request; the key was deleted. Nothing to retry.</summary>
    Rejected,

    /// <summary>The CA issued the certificate but installing it failed. The request id and key are
    /// saved, so <c>retrieve</c> finishes the job.</summary>
    InstallFailed,
}

/// <param name="Message">Ready to show: the CA's text, or for <see cref="CompletionOutcome.InstallFailed"/> the
/// failure plus the exact command that recovers it.</param>
/// <param name="Installed">Set only for <see cref="CompletionOutcome.Installed"/>.</param>
public sealed record EnrollmentCompletion(
    CompletionOutcome Outcome, string Message, string? RequestId, X509Certificate2? Installed);

/// <summary>
/// What happens once a CA has answered an enrollment — shared by the CLI and the Admin screen, and by
/// <c>enroll</c>, <c>renew</c> and <c>retrieve</c> alike, so none of them has a recovery story the
/// others lack.
/// <para>
/// **The request id is saved before anything is installed**, not only when the CA answers "pending". An
/// issued certificate is only a CA's answer until the store holds it; if installing it throws, the
/// request id is the one thing that can still get it back, and the CA will not issue it a second time
/// for free. The record is removed only once the certificate is installed and key access granted. The
/// named key is <em>not</em> deleted on success: it is the installed certificate's private key.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class EnrollmentCompletionHandler
{
    /// <param name="location">Always <c>LocalMachine</c> outside tests; a test passes <c>CurrentUser</c> so it needs no
    /// elevation, which also selects a user-scoped key.</param>
    /// <param name="grantKeyAccess">Run after install, inside the same recovery scope — a failure to grant
    /// leaves the record in place, and re-running <c>retrieve</c> re-adds the same certificate harmlessly.</param>
    public static EnrollmentCompletion Complete(
        string repoRoot, EnrollmentResult result, string keyName, CertificateSpec spec, string caConfig,
        Action<X509Certificate2>? grantKeyAccess = null, StoreLocation location = StoreLocation.LocalMachine)
    {
        switch (result.Outcome)
        {
            case EnrollmentOutcome.Issued:
                return CompleteIssued(repoRoot, result, keyName, spec, caConfig, grantKeyAccess, location);

            case EnrollmentOutcome.Pending:
                PendingEnrollmentStore.Save(repoRoot, Record(result, keyName, spec, caConfig));
                return new EnrollmentCompletion(CompletionOutcome.Pending, result.Message, result.RequestId, null);

            default:
                CertificateKeys.Delete(keyName, location == StoreLocation.LocalMachine);
                return new EnrollmentCompletion(CompletionOutcome.Rejected, result.Message, result.RequestId, null);
        }
    }

    private static EnrollmentCompletion CompleteIssued(
        string repoRoot, EnrollmentResult result, string keyName, CertificateSpec spec, string caConfig,
        Action<X509Certificate2>? grantKeyAccess, StoreLocation location)
    {
        var requestId = result.RequestId ?? "";
        if (requestId.Length > 0)
            PendingEnrollmentStore.Save(repoRoot, Record(result, keyName, spec, caConfig));

        try
        {
            using var key = CertificateKeys.Open(keyName, location == StoreLocation.LocalMachine)
                ?? throw new InvalidOperationException(
                    $"The private key for this enrollment ('{keyName}') could not be reopened.");

            using var withKey = result.Certificate!.CopyWithPrivateKey(key);
            if (spec.FriendlyName is { } friendlyName)
                withKey.FriendlyName = friendlyName;

            var installed = CertificateStore.Install(withKey, location);
            grantKeyAccess?.Invoke(installed);

            PendingEnrollmentStore.Remove(repoRoot, requestId);
            return new EnrollmentCompletion(CompletionOutcome.Installed, result.Message, requestId, installed);
        }
        catch (Exception ex)
        {
            var recovery = requestId.Length > 0
                ? $" The CA issued the certificate (request id {requestId}); finish installing it with " +
                  $"'dbdatasync config cert retrieve --request-id {requestId}'."
                : " The CA issued the certificate, but returned no request id to retrieve it by.";

            return new EnrollmentCompletion(
                CompletionOutcome.InstallFailed, $"Installing the issued certificate failed: {ex.Message}{recovery}",
                requestId.Length > 0 ? requestId : null, null);
        }
    }

    private static PendingEnrollment Record(EnrollmentResult result, string keyName, CertificateSpec spec, string caConfig) =>
        new(result.RequestId!, keyName, spec.SubjectCommonName, spec.DnsNames, caConfig, DateTimeOffset.UtcNow);
}
