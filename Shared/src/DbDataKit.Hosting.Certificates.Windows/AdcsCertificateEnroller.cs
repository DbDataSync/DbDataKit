using System.Runtime.Versioning;
using System.Security.Cryptography.X509Certificates;

namespace DbDataKit.Hosting.Certificates.Windows;

/// <summary>The CA queued the request for certificate-manager approval; it is saved and can be collected later.</summary>
public sealed class EnrollmentPendingException(string? requestId, string message) : Exception(message)
{
    public string? RequestId { get; } = requestId;
}

/// <summary>
/// <see cref="ICertificateEnroller"/> for an enterprise (AD CS) CA, so <c>CertificateMode.Enroll</c> works with
/// <see cref="CertificateProvider"/>: a named machine-store key, a CSR over the request's DNS names, submission, and
/// installation in <c>LocalMachine\My</c>. Like <see cref="AdcsEnrollment"/>, it needs a real CA to exercise and is not
/// covered by automated tests here.
/// <para>
/// <paramref name="pendingStateRoot"/> is the directory under which an enrollment that is pending approval (or whose
/// installation failed after the CA issued) is recorded, so it can be retrieved later with
/// <see cref="EnrollmentCompletionHandler"/>.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdcsCertificateEnroller(string pendingStateRoot, int validityDays = 365) : ICertificateEnroller
{
    public Task<X509Certificate2> EnrollAsync(EnrollRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.CertificateAuthority) || string.IsNullOrWhiteSpace(request.Template))
            throw new InvalidOperationException("Enroll mode needs both a CertificateAuthority and a Template.");
        if (request.DnsNames.Count == 0)
            throw new InvalidOperationException("Enroll mode needs at least one DNS name.");

        var spec = new CertificateSpec(request.DnsNames[0], request.DnsNames, validityDays, FriendlyName: null);
        var keyName = CertificateKeys.NewKeyName();
        using var privateKey = CertificateKeys.Create(keyName);
        var csrPem = CertificateBuilder.CreateSigningRequest(spec, privateKey);

        EnrollmentResult result;
        try
        {
            result = AdcsEnrollment.Submit(request.CertificateAuthority, request.Template, csrPem);
        }
        catch
        {
            CertificateKeys.Delete(keyName);
            throw;
        }

        var completion = EnrollmentCompletionHandler.Complete(pendingStateRoot, result, keyName, spec, request.CertificateAuthority);
        return completion.Outcome switch
        {
            CompletionOutcome.Installed => Task.FromResult(completion.Installed!),
            CompletionOutcome.Pending => throw new EnrollmentPendingException(completion.RequestId, completion.Message),
            _ => throw new InvalidOperationException(completion.Message),
        };
    }
}
