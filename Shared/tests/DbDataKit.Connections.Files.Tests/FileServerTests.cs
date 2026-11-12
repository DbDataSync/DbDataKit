using DbDataKit.Connections.Files;

namespace DbDataKit.Connections.Files.Tests;

/// <summary>
/// Plan 27a IX04 against real servers in containers (<c>testenv/testenv.sh up sftp ftps</c>: OpenSSH in atmoz/sftp, pure-ftpd with TLS): list, read, write; a partial file is not picked up; a host key or certificate that is not the pinned one is refused naming it; a wrong password is a
/// permanent login failure; the Test says it in words. Skipped, saying what to start, when the variables are not set (CI sets DBDATAKIT_TEST_REQUIRE_FILE_SERVERS).
/// </summary>
public sealed class FileServerTests
{
    private static ConnectionDefinition SftpConnection(string kind, string hostKeys, string signIn, string extra = "")
    {
        var host = Support.Var("DBDATAKIT_TEST_SFTP_HOST");
        return Support.Load($"schema: connection/2\nkind: {kind}\nname: files\nfile_server:\n  host: {host}\n  port: {Support.Var("DBDATAKIT_TEST_SFTP_PORT")}\n  root: {Support.Var("DBDATAKIT_TEST_SFTP_ROOT")}\n  host_keys: [{hostKeys}]\n{signIn}{extra}");
    }

    private static Dictionary<string, string> SftpSecrets() => new()
    {
        ["PW"] = Support.Var("DBDATAKIT_TEST_SFTP_PASSWORD"), ["WRONG"] = "not-the-password-canary-81f2",
        ["KEY"] = File.ReadAllText(Support.Var("DBDATAKIT_TEST_SFTP_KEY_FILE")),
    };

    private static string Password => "sign_in: { method: basic, user: foo, password_secret: env:PW }\n";

    /// <summary>The fingerprint the server shows, as a person would confirm and pin it.</summary>
    private static async Task<string> ShownKey()
    {
        var probe = SftpConnection("sftp", "", Password, "tls: { verify: false }\n");
        return await FileStores.ProbeAsync(probe) ?? throw new InvalidOperationException("the SFTP server showed no host key");
    }

    [Fact]
    public async Task Sftp_with_a_password_and_with_a_key_lists_reads_writes_and_never_shows_a_partial_file()
    {
        var pinned = $"\"{await ShownKey()}\"";
        var secrets = Support.Secrets(SftpSecrets());
        var folder = "/" + Guid.NewGuid().ToString("N")[..8];

        await using var store = await FileStores.OpenAsync(SftpConnection("sftp", pinned, Password), secrets);
        await using var observer = await FileStores.OpenAsync(SftpConnection("sftp", pinned, Password), secrets);
        await Support.ContractAsync(store, observer, folder);

        // the same with the private key instead of the password
        await using var withKey = await FileStores.OpenAsync(SftpConnection("sftp", pinned, "sign_in: { method: ssh_key, user: foo, private_key_secret: env:KEY }\n"), secrets);
        await Support.ContractAsync(withKey, observer, folder);
    }

    [Fact]
    public async Task A_host_key_that_is_not_the_pinned_one_is_refused_naming_the_server_and_both_keys_and_nothing_is_sent()
    {
        var shown = await ShownKey();
        const string other = "SHA256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
        var connection = SftpConnection("sftp", $"\"{other}\"", Password);
        var refused = await Assert.ThrowsAsync<PinMismatchException>(() => FileStores.OpenAsync(connection, Support.Secrets(SftpSecrets())));
        Assert.Contains(shown, refused.Message, StringComparison.Ordinal);
        Assert.Contains(other, refused.Message, StringComparison.Ordinal);
        Assert.Contains(Support.Var("DBDATAKIT_TEST_SFTP_HOST"), refused.Message, StringComparison.Ordinal);
        Assert.False(refused.Failure.IsTemporary);

        // the Test says the same in words and does not try the password
        var test = await FileStoreTester.TestAsync(connection, Support.Secrets(SftpSecrets()));
        Assert.False(test.Ok);
        Assert.Contains(shown, test.Message, StringComparison.Ordinal);
        Assert.Equal(ConnectionFailureKind.Tls, test.Kind);
        Assert.False(test.Temporary);
        Assert.DoesNotContain(SftpSecrets()["PW"], test.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_wrong_password_is_a_permanent_login_failure_and_a_missing_secret_is_named()
    {
        var pinned = $"\"{await ShownKey()}\"";
        var wrong = SftpConnection("sftp", pinned, "sign_in: { method: basic, user: foo, password_secret: env:WRONG }\n");
        var failure = await Assert.ThrowsAsync<ConnectionFailureException>(() => FileStores.OpenAsync(wrong, Support.Secrets(SftpSecrets())));
        Assert.Equal(ConnectionFailureKind.Login, failure.Failure.Kind);
        Assert.False(failure.Failure.IsTemporary);

        var test = await FileStoreTester.TestAsync(wrong, Support.Secrets(SftpSecrets()));
        Assert.False(test.Ok);
        Assert.Equal("rejected", test.SignIn);
        Assert.DoesNotContain("not-the-password-canary-81f2", test.Message, StringComparison.Ordinal);

        var unset = await FileStoreTester.TestAsync(SftpConnection("sftp", pinned, "sign_in: { method: basic, user: foo, password_secret: env:NOT_THERE }\n"), Support.Secrets(SftpSecrets()));
        Assert.Contains("env:NOT_THERE", unset.Message, StringComparison.Ordinal);
        Assert.Null(unset.Reached);
    }

    [Fact]
    public async Task Test_of_a_working_pinned_connection_lists_the_root_and_without_a_pin_names_the_key_to_pin()
    {
        var shown = await ShownKey();
        var ok = await FileStoreTester.TestAsync(SftpConnection("sftp", $"\"{shown}\"", Password), Support.Secrets(SftpSecrets()));
        Assert.True(ok.Ok, ok.Message);
        Assert.Contains("listed the root", ok.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pin it", ok.Message, StringComparison.Ordinal);

        var open = await FileStoreTester.TestAsync(SftpConnection("sftp", "", Password, "tls: { verify: false }\n"), Support.Secrets(SftpSecrets()));
        Assert.True(open.Ok, open.Message);
        Assert.Contains(shown, open.Message, StringComparison.Ordinal);
        Assert.Contains("pin it", open.Message, StringComparison.Ordinal);
    }

    /// <summary>The OpenSSH server that allows commands (the chrooted SFTP one refuses `scp` and `mv`).</summary>
    private static ConnectionDefinition SshConnection(string kind, string hostKeys, string signIn = "sign_in: { method: basic, user: foo, password_secret: env:PW }\n", string extra = "") =>
        Support.Load($"schema: connection/2\nkind: {kind}\nname: files\nfile_server:\n  host: {Support.Var("DBDATAKIT_TEST_SSH_HOST")}\n  port: {Support.Var("DBDATAKIT_TEST_SSH_PORT")}\n  root: {Support.Var("DBDATAKIT_TEST_SSH_ROOT")}\n  host_keys: [{hostKeys}]\n{signIn}{extra}");

    [Fact]
    public async Task Scp_writes_through_a_part_file_reads_and_deletes_but_cannot_list()
    {
        var secrets = Support.Secrets(new Dictionary<string, string> { ["PW"] = Support.Var("DBDATAKIT_TEST_SSH_PASSWORD") });
        var pinned = $"\"{await FileStores.ProbeAsync(SshConnection("scp", "", extra: "tls: { verify: false }\n"))}\"";
        await using var scp = await FileStores.OpenAsync(SshConnection("scp", pinned), secrets);
        var unsupported = await Assert.ThrowsAsync<ConnectionFailureException>(() => scp.ListAsync("/"));
        Assert.Equal(ConnectionFailureKind.Unsupported, unsupported.Failure.Kind);

        var name = $"/scp-{Guid.NewGuid():N}.txt";
        await scp.WriteAsync(name, Support.Bytes("by scp"));
        Assert.Equal("by scp", await Support.TextAsync(scp, name));
        // the same file is there for an SFTP reader, whole, and no .part is left
        await using var sftp = await FileStores.OpenAsync(SshConnection("sftp", pinned), secrets);
        var listed = await sftp.ListAsync("/", includeTemporary: true);
        Assert.Contains(listed, e => e.Path == name && e.Size == 6);
        Assert.DoesNotContain(listed, e => e.Path == name + ".part");
        await scp.DeleteAsync(name);
        var missing = await Assert.ThrowsAsync<ConnectionFailureException>(() => scp.OpenReadAsync(name));
        Assert.False(missing.Failure.IsTemporary);

        var test = await FileStoreTester.TestAsync(SshConnection("scp", pinned), secrets);
        Assert.True(test.Ok, test.Message);
        Assert.Contains("cannot list", test.Message, StringComparison.Ordinal);
    }

    // ---- FTPS ----

    private static ConnectionDefinition FtpsConnection(string certificateSha256, string signIn, string extra = "") =>
        Support.Load($"schema: connection/2\nkind: ftps\nname: files\nfile_server:\n  host: {Support.Var("DBDATAKIT_TEST_FTPS_HOST")}\n  port: {Support.Var("DBDATAKIT_TEST_FTPS_PORT")}\n  certificate_sha256: [{certificateSha256}]\n{signIn}{extra}");

    private static string FtpsPassword => "sign_in: { method: basic, user: foo, password_secret: env:PW }\n";

    private static Dictionary<string, string> FtpsSecrets() => new() { ["PW"] = Support.Var("DBDATAKIT_TEST_FTPS_PASSWORD"), ["WRONG"] = "not-the-ftps-password-canary-5d" };

    [Fact]
    public async Task Ftps_pinned_by_certificate_lists_reads_writes_and_never_shows_a_partial_file()
    {
        var pin = Support.Var("DBDATAKIT_TEST_FTPS_CERT_SHA256");
        var secrets = Support.Secrets(FtpsSecrets());
        var folder = "/" + Guid.NewGuid().ToString("N")[..8];
        await using var store = await FileStores.OpenAsync(FtpsConnection($"\"{pin}\"", FtpsPassword), secrets);
        await using var observer = await FileStores.OpenAsync(FtpsConnection($"\"{pin}\"", FtpsPassword), secrets);
        await Support.ContractAsync(store, observer, folder);
    }

    [Fact]
    public async Task A_certificate_that_is_not_pinned_or_trusted_is_refused_naming_its_fingerprint()
    {
        var shown = Support.Var("DBDATAKIT_TEST_FTPS_CERT_SHA256");
        var secrets = Support.Secrets(FtpsSecrets());

        // pinned to another certificate
        var other = new string('A', 64);
        var wrongPin = await Assert.ThrowsAsync<PinMismatchException>(() => FileStores.OpenAsync(FtpsConnection($"\"{other}\"", FtpsPassword), secrets));
        Assert.Contains(shown, wrongPin.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(other, wrongPin.Message, StringComparison.Ordinal);
        Assert.False(wrongPin.Failure.IsTemporary);

        // not pinned, and nothing vouches for a certificate the server made for itself
        var unpinned = await Assert.ThrowsAsync<PinMismatchException>(() => FileStores.OpenAsync(FtpsConnection("", FtpsPassword), secrets));
        Assert.Contains(shown, unpinned.Message, StringComparison.OrdinalIgnoreCase);

        // and the Test says so in words, without trying the password
        var test = await FileStoreTester.TestAsync(FtpsConnection($"\"{other}\"", FtpsPassword), secrets);
        Assert.False(test.Ok);
        Assert.Contains(shown, test.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(FtpsSecrets()["PW"], test.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ftps_wrong_password_is_a_permanent_login_failure_and_probe_and_test_name_the_certificate_to_pin()
    {
        var shown = Support.Var("DBDATAKIT_TEST_FTPS_CERT_SHA256");
        var secrets = Support.Secrets(FtpsSecrets());
        var wrong = FtpsConnection($"\"{shown}\"", "sign_in: { method: basic, user: foo, password_secret: env:WRONG }\n");
        var failure = await Assert.ThrowsAsync<ConnectionFailureException>(() => FileStores.OpenAsync(wrong, secrets));
        Assert.Equal(ConnectionFailureKind.Login, failure.Failure.Kind);
        Assert.False(failure.Failure.IsTemporary);

        Assert.Equal(shown, await FileStores.ProbeAsync(FtpsConnection("", FtpsPassword)), ignoreCase: true);
        var open = await FileStoreTester.TestAsync(FtpsConnection("", FtpsPassword, "tls: { verify: false }\n"), secrets);
        Assert.True(open.Ok, open.Message);
        Assert.Contains("certificate_sha256", open.Message, StringComparison.Ordinal);
        Assert.Contains(shown, open.Message, StringComparison.OrdinalIgnoreCase);
    }
}
