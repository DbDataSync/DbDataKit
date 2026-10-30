using DbDataSync.Certificates;
using DbDataSync.Core.Config;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// A first start over https makes the managed self-signed certificate (plan 13a, L08), as the other servers of the suite do; plain http, and a certificate that is configured already, are left alone, and a certificate that
/// cannot be made fails with a sentence that names the file.
/// </summary>
public sealed class ServeCommandCertificateTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-serve-certificate-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private static string? NoEnvironment(string name) => null;

    [Fact]
    public void Https_with_no_certificate_makes_the_managed_one_and_binds_it()
    {
        ServeCommand.Prepare(_root);

        Assert.Null(ServeCommand.EnsureCertificate(_root, "https://0.0.0.0:8443", NoEnvironment));

        Assert.True(File.Exists(ManagedSelfSignedCertificate.PfxPath(_root)));
        var config = DbDataSyncConfigFile.Read(_root);
        Assert.Equal(ManagedSelfSignedCertificate.PfxPath(_root), config["Kestrel:Certificates:Default:Path"]);
        Assert.Equal("true", config["Kestrel:Certificates:Default:AllowInvalid"]);
    }

    [Fact]
    public void A_second_start_keeps_the_certificate_it_made()
    {
        ServeCommand.Prepare(_root);
        Assert.Null(ServeCommand.EnsureCertificate(_root, "https://0.0.0.0:8443", NoEnvironment));
        var first = File.ReadAllBytes(ManagedSelfSignedCertificate.PfxPath(_root));

        Assert.Null(ServeCommand.EnsureCertificate(_root, "https://0.0.0.0:8443", NoEnvironment));

        Assert.Equal(first, File.ReadAllBytes(ManagedSelfSignedCertificate.PfxPath(_root)));
    }

    [Fact]
    public void Plain_http_makes_no_certificate()
    {
        ServeCommand.Prepare(_root);

        Assert.Null(ServeCommand.EnsureCertificate(_root, "http://0.0.0.0:8080", NoEnvironment));

        Assert.False(File.Exists(ManagedSelfSignedCertificate.PfxPath(_root)));
    }

    [Fact]
    public void A_certificate_named_in_the_environment_or_the_file_is_left_alone()
    {
        ServeCommand.Prepare(_root);
        Assert.Null(ServeCommand.EnsureCertificate(_root, "https://0.0.0.0:8443", name => name == "Kestrel__Certificates__Default__Path" ? "/certs/mine.pfx" : null));
        Assert.False(File.Exists(ManagedSelfSignedCertificate.PfxPath(_root)));

        DbDataSyncConfigFile.SetValue(_root, "Kestrel:Certificates:Default", "Path", "/certs/other.pfx");
        Assert.Null(ServeCommand.EnsureCertificate(_root, "https://0.0.0.0:8443", NoEnvironment));
        Assert.False(File.Exists(ManagedSelfSignedCertificate.PfxPath(_root)));
    }

    [NonWindowsFact]
    public void A_certificate_that_cannot_be_made_fails_with_a_message_naming_the_file()
    {
        ServeCommand.Prepare(_root);
        // a file where the directory has to be
        File.WriteAllText(Path.Combine(_root, "tls"), "in the way");

        var message = ServeCommand.EnsureCertificate(_root, "https://0.0.0.0:8443", NoEnvironment);

        Assert.NotNull(message);
        Assert.Contains(ManagedSelfSignedCertificate.PfxPath(_root), message);
        Assert.Contains("https://0.0.0.0:8443", message);
    }
}
