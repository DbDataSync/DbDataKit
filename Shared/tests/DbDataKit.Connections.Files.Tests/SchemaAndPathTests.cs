using DbDataKit.Connections.Files;

namespace DbDataKit.Connections.Files.Tests;

/// <summary>Plan 27a IX04, what needs no server: the connection file's rules for the file kinds, the paths under a root, the names that mean "being written", and the pinned certificate's decision.</summary>
public sealed class SchemaAndPathTests
{
    private const string Key = "SHA256:AbCdEfGhIjKlMnOpQrStUvWxYz0123456789+/AbCdE";

    private static string Sftp(string file, string signIn = "sign_in: { method: basic, user: foo, password_secret: env:PW }\n") => $"schema: connection/2\nkind: sftp\nname: files\n{file}{signIn}";

    private static string[] Problems(string yaml) => [.. ConnectionDefinitionLoader.LoadConnectionOfAnyKind(yaml, "connections/files.yml").Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.Message)];

    [Fact]
    public void A_pinned_sftp_ftps_and_scp_connection_reads_and_keeps_its_pins()
    {
        var sftp = Support.Load(Sftp($"file_server:\n  host: files.example.com\n  root: /upload\n  host_keys: [\"{Key}\"]\n"));
        Assert.Equal((ConnectionKinds.Sftp, "files.example.com", "/upload"), (sftp.KindName, sftp.FileServer!.Host, sftp.FileServer.Root));
        Assert.Equal([Key], sftp.FileServer.HostKeys);
        Assert.Equal(ConnectionKindGroup.FileStore, ConnectionKindGroups.Of(sftp));

        var scp = Support.Load($"schema: connection/2\nkind: scp\nname: files\nfile_server: {{ host: h.example.com, host_keys: [\"{Key}\"] }}\nsign_in: {{ method: ssh_key, user: robot, private_key_secret: env:KEY, passphrase_secret: env:PASS }}\n");
        Assert.Equal("env:KEY", scp.SignIn!.PrivateKeySecret);

        var ftps = Support.Load($"schema: connection/2\nkind: ftps\nname: files\nfile_server: {{ host: h.example.com, ftps_mode: implicit, certificate_sha256: [\"{new string('a', 64)}\"] }}\nsign_in: {{ method: basic, user: u, password_secret: env:PW }}\n");
        Assert.Equal("implicit", ftps.FileServer!.FtpsMode);
        Assert.Single(ftps.FileServer.CertificateSha256);
    }

    [Fact]
    public void An_ssh_server_must_be_pinned_unless_any_key_is_taken_on_purpose()
    {
        var unpinned = Problems(Sftp("file_server: { host: files.example.com }\n"));
        Assert.Contains(unpinned, p => p.Contains("host key", StringComparison.Ordinal) && p.Contains("pin it", StringComparison.Ordinal));

        var on_purpose = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(Sftp("file_server: { host: files.example.com }\ntls: { verify: false }\n"), "connections/files.yml");
        Assert.True(on_purpose.Ok);
        Assert.Contains(on_purpose.Diagnostics, d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Warning);   // and it says so
    }

    [Theory]
    [InlineData("file_server: { host: h, host_keys: [\"MD5:aa:bb\"] }\n", "SHA256:")]
    [InlineData("file_server: { host: h, root: upload, host_keys: [\"" + Key + "\"] }\n", "absolute folder")]
    [InlineData("file_server: { host: h, root: /a/../b, host_keys: [\"" + Key + "\"] }\n", "absolute folder")]
    [InlineData("file_server: { host: \"u@h\", host_keys: [\"" + Key + "\"] }\n", "file_server.host")]
    [InlineData("file_server: { host: h, certificate_sha256: [\"aa\"], host_keys: [\"" + Key + "\"] }\n", "belongs to the kind `ftps`")]
    [InlineData("endpoint: https://h.example.com\nfile_server: { host: h, host_keys: [\"" + Key + "\"] }\n", "does not belong")]
    public void A_file_server_section_that_is_not_right_is_refused_with_the_reason(string file, string expected) =>
        Assert.Contains(Problems(Sftp(file)), p => p.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void Sign_in_methods_are_the_ones_the_protocol_has_and_a_key_is_never_written_in_the_file()
    {
        var pinned = $"file_server: {{ host: h, host_keys: [\"{Key}\"] }}\n";
        Assert.Contains(Problems(Sftp(pinned, "sign_in: { method: api_key, key_secret: env:K }\n")), p => p.Contains("ssh_key", StringComparison.Ordinal));
        Assert.Contains(Problems(Sftp(pinned, "sign_in: { method: ssh_key, user: robot }\n")), p => p.Contains("private_key_secret", StringComparison.Ordinal));
        Assert.Contains(Problems(Sftp(pinned, "sign_in: { method: ssh_key, user: robot, private_key_secret: \"-----BEGIN OPENSSH PRIVATE KEY-----\" }\n")), p => p.Contains("secret reference", StringComparison.Ordinal));
        Assert.Contains(Problems(Sftp(pinned, "sign_in: { method: basic, user: u, password_secret: env:P, private_key_secret: env:K }\n")), p => p.Contains("belongs to `sign_in.method: ssh_key`", StringComparison.Ordinal));
        Assert.Contains(Problems("schema: connection/2\nkind: ftps\nname: files\nfile_server: { host: h }\nsign_in: { method: ssh_key, user: u, private_key_secret: env:K }\n"), p => p.Contains("ftps", StringComparison.Ordinal));
        Assert.Contains(Problems($"schema: connection/2\nkind: http\nname: files\nendpoint: https://a.example.com\nsign_in: {{ method: ssh_key, user: u, private_key_secret: env:K }}\n"), p => p.Contains("`sftp` and `scp`", StringComparison.Ordinal));
    }

    [Fact]
    public void A_program_that_keeps_databases_only_refuses_a_file_connection_naming_it_and_file_server_belongs_to_the_file_kinds()
    {
        var yaml = Sftp($"file_server: {{ host: h, host_keys: [\"{Key}\"] }}\n");
        var refused = ConnectionDefinitionLoader.LoadDatabaseConnection(yaml, "connections/files.yml");
        Assert.False(refused.Ok);
        Assert.Contains(refused.Diagnostics, d => d.Message.Contains("files", StringComparison.Ordinal) && d.Message.Contains("`sftp`", StringComparison.Ordinal));
        Assert.Contains(Problems("schema: connection/2\nkind: http\nname: files\nendpoint: https://a.example.com\nfile_server: { host: h }\n"), p => p.Contains("file_server", StringComparison.Ordinal));
        Assert.Contains(Problems("schema: connection/2\nname: files\nengine: postgres\naddress: { host: h }\nfile_server: { host: h }\n"), p => p.Contains("file_server", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("/upload", "/", "/upload")]
    [InlineData("/upload", "in/a.csv", "/upload/in/a.csv")]
    [InlineData("/upload", "/in/./x/../a.csv", "/upload/in/a.csv")]
    [InlineData("/", "a", "/a")]
    [InlineData("/a/b", "", "/a/b")]
    public void A_path_is_under_the_root(string root, string path, string expected) => Assert.Equal(expected, FileStorePaths.Resolve(root, path));

    [Theory]
    [InlineData("/upload", "..")]
    [InlineData("/upload", "/in/../../etc/passwd")]
    [InlineData("/", "../x")]
    public void A_path_that_leaves_the_root_is_refused_as_a_permanent_failure(string root, string path)
    {
        var refused = Assert.Throws<ConnectionFailureException>(() => FileStorePaths.Resolve(root, path));
        Assert.Equal(ConnectionFailureKind.Permission, refused.Failure.Kind);
        Assert.False(refused.Failure.IsTemporary);
    }

    [Fact]
    public void Paths_come_back_relative_to_the_root_and_split_into_folder_and_name()
    {
        Assert.Equal("/in/a.csv", FileStorePaths.Relative("/upload", "/upload/in/a.csv"));
        Assert.Equal("/", FileStorePaths.Relative("/upload", "/upload"));
        Assert.Equal("/x", FileStorePaths.Relative("/", "/x"));
        Assert.Equal(("/upload/in", "a.csv"), FileStorePaths.Split("/upload/in/a.csv"));
        Assert.Equal(("/", "a"), FileStorePaths.Split("/a"));
    }

    [Theory]
    [InlineData("feed.csv.part", true)]
    [InlineData("feed.CSV.TMP", true)]
    [InlineData("download.crdownload", true)]
    [InlineData("notes.txt~", true)]
    [InlineData("feed.csv", false)]
    [InlineData("partner.csv", false)]
    public void Names_that_mean_being_written_are_recognised(string name, bool temporary) => Assert.Equal(temporary, FileNames.IsTemporary(name));

    [Fact]
    public void A_refused_server_is_named_with_both_fingerprints_in_the_message_and_is_permanent()
    {
        var e = new PinMismatchException("host key", "files.example.com", "SHA256:offered", ["SHA256:pinned"]);
        Assert.Contains("files.example.com", e.Message, StringComparison.Ordinal);
        Assert.Contains("SHA256:offered", e.Message, StringComparison.Ordinal);
        Assert.Contains("SHA256:pinned", e.Message, StringComparison.Ordinal);
        Assert.Equal((ConnectionFailureKind.Tls, "host-key-refused", false), (e.Failure.Kind, e.Failure.Code, e.Failure.IsTemporary));
        Assert.Equal(e.Failure.Code, FileFailures.Classify(new InvalidOperationException("outer", e)).Code);
    }

    [Fact]
    public void A_certificate_is_trusted_when_pinned_and_a_pin_means_only_that_certificate()
    {
        using var certificate = Support.SelfSigned();
        var fingerprint = Fingerprints.OfCertificate(certificate.RawData);
        var chain = System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors;

        var pinned = Support.Load($"schema: connection/2\nkind: ftps\nname: files\nfile_server: {{ host: h, certificate_sha256: [\"{fingerprint.ToLowerInvariant()}\"] }}\n");
        Assert.True(FtpsFileStore.Trusts(pinned, certificate, chain, fingerprint));

        var another = Support.Load($"schema: connection/2\nkind: ftps\nname: files\nfile_server: {{ host: h, certificate_sha256: [\"{new string('0', 64)}\"] }}\n");
        Assert.False(FtpsFileStore.Trusts(another, certificate, System.Net.Security.SslPolicyErrors.None, fingerprint));   // pinned to another one: even a trusted chain is not enough

        var unpinned = Support.Load("schema: connection/2\nkind: ftps\nname: files\nfile_server: { host: h }\n");
        Assert.False(FtpsFileStore.Trusts(unpinned, certificate, chain, fingerprint));                                    // self-signed and nothing vouches for it
        Assert.True(FtpsFileStore.Trusts(unpinned, certificate, System.Net.Security.SslPolicyErrors.None, fingerprint));

        var open = Support.Load("schema: connection/2\nkind: ftps\nname: files\nfile_server: { host: h }\ntls: { verify: false }\n");
        Assert.True(FtpsFileStore.Trusts(open, certificate, chain, fingerprint));
    }
}
