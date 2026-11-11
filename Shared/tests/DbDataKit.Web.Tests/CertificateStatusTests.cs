using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Web.Trust;

namespace DbDataKit.Web.Tests;

/// <summary>UI10: what <c>GET /api/v1/certificate</c> of a server says about the certificate it presents, in the shape of the Gateway's route.</summary>
public sealed class CertificateStatusTests
{
    private static X509Certificate2 Make(string subject, DateTimeOffset notBefore, DateTimeOffset notAfter, params string[] names)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        foreach (var name in names) san.AddDnsName(name);
        request.CertificateExtensions.Add(san.Build());
        return request.CreateSelfSigned(notBefore, notAfter);
    }

    [Fact]
    public void A_certificate_is_described_with_its_subject_days_left_and_names_and_never_its_key()
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        using var certificate = Make("CN=build.example", now.AddDays(-10), now.AddDays(100).AddHours(3), "build.example", "build");

        var description = CertificateStatusEndpoint.Describe(certificate, "self-signed", now);

        Assert.True((bool)description["present"]!);
        Assert.Equal("self-signed", (string)description["mode"]!);
        Assert.Equal("CN=build.example", (string)description["subject"]!);
        Assert.Equal(100, (int)description["days_left"]!);
        Assert.False((bool)description["needs_renewal"]!);
        Assert.Equal(30, (int)description["renew_before_days"]!);
        Assert.True((bool)description["self_signed"]!);
        Assert.Equal(certificate.Thumbprint, (string)description["thumbprint"]!);
        Assert.Equal(["build.example", "build"], description["names"]!.AsArray().Select(n => (string)n!));
        Assert.DoesNotContain("PRIVATE", description.ToJsonString());
    }

    [Theory]
    [InlineData(31, false)]
    [InlineData(30, true)]
    [InlineData(1, true)]
    [InlineData(-3, true)]
    public void A_certificate_inside_its_renewal_window_or_past_its_end_needs_renewal_and_says_how_many_days_are_left(int days, bool renew)
    {
        var now = new DateTimeOffset(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);
        using var certificate = Make("CN=x", now.AddDays(-400), now.AddDays(days).AddMinutes(5), "x");

        var description = CertificateStatusEndpoint.Describe(certificate, "self-signed", now);

        Assert.Equal(days, (int)description["days_left"]!);
        Assert.Equal(renew, (bool)description["needs_renewal"]!);
    }

    [Fact]
    public void A_server_with_no_certificate_says_so_and_nothing_more()
    {
        var description = CertificateStatusEndpoint.Describe(null, "self-signed", DateTimeOffset.UtcNow);

        Assert.False((bool)description["present"]!);
        Assert.Null(description["days_left"]);
        Assert.Null(description["subject"]);
    }
}
