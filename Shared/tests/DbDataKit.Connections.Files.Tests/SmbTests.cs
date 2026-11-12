using DbDataKit.Connections.Files;

namespace DbDataKit.Connections.Files.Tests;

/// <summary>
/// Plan 27a IX05 on what a Linux host can prove: the connection file's rules, the UNC address, the failure classification of what Windows reports (a failed logon or Kerberos ticket is a rejected login), the whole store behaviour (list, read, write, a partial file never picked up) over a local
/// folder, and that on a host that is not Windows the connection says so in a classified failure. <b>Only a Windows host in a domain proves</b> the real thing: the Kerberos ticket, the share, SMB signing.
/// </summary>
public sealed class SmbTests
{
    private const string Smb = "schema: connection/2\nkind: smb\nname: fs\nfile_server:\n  host: fs01.corp.example.com\n  share: data\n  root: /incoming/partner\nsign_in: { method: kerberos }\n";

    private static string[] Problems(string yaml) => [.. ConnectionDefinitionLoader.LoadConnectionOfAnyKind(yaml, "connections/fs.yml").Diagnostics.Where(d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Error).Select(d => d.Message)];

    [Fact]
    public void An_smb_connection_names_the_share_signs_in_with_kerberos_and_has_the_unc_address()
    {
        var d = Support.Load(Smb);
        Assert.Equal(ConnectionKindGroup.FileStore, ConnectionKindGroups.Of(d));
        Assert.Equal(@"\\fs01.corp.example.com\data\incoming\partner", SmbFileStore.UncPath(d));
        Assert.Equal(@"\\fs01.corp.example.com\data", SmbFileStore.UncPath(Support.Load(Smb.Replace("  root: /incoming/partner\n", ""))));
    }

    [Theory]
    [InlineData("  host: fs01\n  root: /x\n", "names the share")]
    [InlineData("  host: fs01\n  share: a/b\n", "names the share")]
    [InlineData("  host: fs01\n  share: data\n  port: 4455\n", "port 445")]
    [InlineData("  host: fs01\n  share: data\n  host_keys: [\"SHA256:AbCdEfGhIjKlMnOpQrStUvWxYz0123456789+/AbCdE\"]\n", "do not belong to an SMB")]
    public void A_file_server_section_of_a_share_that_is_not_right_is_refused(string section, string expected) =>
        Assert.Contains(Problems($"schema: connection/2\nkind: smb\nname: fs\nfile_server:\n{section}sign_in: {{ method: kerberos }}\n"), p => p.Contains(expected, StringComparison.Ordinal));

    [Fact]
    public void Only_kerberos_signs_in_to_a_share_and_kerberos_signs_in_to_nothing_else()
    {
        Assert.Contains(Problems(Smb.Replace("{ method: kerberos }", "{ method: basic, user: u, password_secret: env:P }")), p => p.Contains("kerberos", StringComparison.Ordinal));
        Assert.Contains(Problems(Smb.Replace("{ method: kerberos }", "{ method: kerberos, user: u }")), p => p.Contains("does not belong", StringComparison.Ordinal));
        const string key = "SHA256:AbCdEfGhIjKlMnOpQrStUvWxYz0123456789+/AbCdE";
        Assert.Contains(Problems($"schema: connection/2\nkind: sftp\nname: fs\nfile_server: {{ host: h, host_keys: [\"{key}\"] }}\nsign_in: {{ method: kerberos }}\n"), p => p.Contains("signs in to the kind `smb`", StringComparison.Ordinal));
        Assert.Contains(Problems("schema: connection/2\nkind: ftps\nname: fs\nfile_server: { host: h }\nsign_in: { method: kerberos }\n"), p => p.Contains("signs in to the kind `smb`", StringComparison.Ordinal));
    }

    [Fact]
    public void An_address_instead_of_a_name_is_allowed_with_a_warning_that_windows_will_use_ntlm()
    {
        var result = ConnectionDefinitionLoader.LoadConnectionOfAnyKind(Smb.Replace("fs01.corp.example.com", "10.0.0.5"), "connections/fs.yml");
        Assert.True(result.Ok);
        Assert.Contains(result.Diagnostics, d => d.Severity == DbDataKit.Config.Yaml.ConfigSeverity.Warning && d.Message.Contains("NTLM", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0x8007052E, ConnectionFailureKind.Login, "smb-logon")]       // ERROR_LOGON_FAILURE
    [InlineData(0x8007051F, ConnectionFailureKind.Login, "smb-logon")]       // ERROR_NO_LOGON_SERVERS (1311): no domain controller to ask for a ticket
    [InlineData(0x80070057, ConnectionFailureKind.Other, "smb-io")]          // an error nothing here knows (invalid parameter)
    [InlineData(0x8009030E, ConnectionFailureKind.Login, "smb-kerberos")]    // SEC_E_NO_CREDENTIALS: no ticket to be had
    [InlineData(0x8009030C, ConnectionFailureKind.Login, "smb-kerberos")]    // SEC_E_LOGON_DENIED
    [InlineData(0x80090322, ConnectionFailureKind.Login, "smb-kerberos")]    // SEC_E_WRONG_PRINCIPAL: a ticket for another name
    [InlineData(0x80090324, ConnectionFailureKind.Login, "smb-clock")]       // SEC_E_TIME_SKEW
    [InlineData(0x80070035, ConnectionFailureKind.Network, "smb-network")]   // ERROR_BAD_NETPATH (53)
    [InlineData(0x80070043, ConnectionFailureKind.Network, "smb-network")]   // ERROR_BAD_NET_NAME (67): no such share
    [InlineData(0x800705B4, ConnectionFailureKind.Timeout, "smb-timeout")]   // ERROR_TIMEOUT (1460)
    [InlineData(0x80070002, ConnectionFailureKind.NotFound, "smb-not-found")]
    [InlineData(0x80070005, ConnectionFailureKind.Permission, "smb-access-denied")]
    public void What_windows_reports_is_classified_and_a_failed_kerberos_ticket_is_a_rejected_login(uint hresult, ConnectionFailureKind kind, string code)
    {
        var failure = SmbFailures.Classify(new IOException("\\\\fs01\\data\\secret-path.csv", unchecked((int)hresult)));
        Assert.Equal((kind, code), (failure.Kind, failure.Code));
        Assert.DoesNotContain("secret-path", failure.PublicMessage, StringComparison.Ordinal);   // the path is not in the message
        Assert.Equal(kind is ConnectionFailureKind.Network or ConnectionFailureKind.Timeout, failure.IsTemporary);
    }

    [Fact]
    public void An_unauthorised_access_and_a_missing_file_are_permanent()
    {
        Assert.Equal(ConnectionFailureKind.Permission, SmbFailures.Classify(new UnauthorizedAccessException()).Kind);
        Assert.Equal(ConnectionFailureKind.NotFound, SmbFailures.Classify(new FileNotFoundException()).Kind);
        Assert.False(SmbFailures.Classify(new DirectoryNotFoundException()).IsTemporary);
    }

    [Fact]
    public async Task On_a_host_that_is_not_windows_the_connection_says_so_in_a_classified_failure_and_the_test_in_words()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "this host is Windows: the real share is the test (see the plan row)");
        var d = Support.Load(Smb);
        Assert.False(SmbFileStore.IsSupported);
        var failure = await Assert.ThrowsAsync<ConnectionFailureException>(() => FileStores.OpenAsync(d, Support.Secrets(new Dictionary<string, string>())));
        Assert.Equal((ConnectionFailureKind.Unsupported, SmbFileStore.NeedsWindows, false), (failure.Failure.Kind, failure.Failure.Code, failure.Failure.IsTemporary));

        var test = await FileStoreTester.TestAsync(d, Support.Secrets(new Dictionary<string, string>()));
        Assert.False(test.Ok);
        Assert.Equal(ConnectionFailureKind.Unsupported, test.Kind);
        Assert.Contains("Windows", test.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_store_behind_a_share_lists_reads_writes_replaces_deletes_and_never_shows_a_partial_file()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ddk-smb-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(folder);
        try
        {
            await using var store = new PathFileStore(folder);
            await using var observer = new PathFileStore(folder);
            await Support.ContractAsync(store, observer, "/in/partner");

            // a folder is listed as one, and the root of the share is the folder the connection names
            await store.WriteAsync("/in/partner/a.csv", Support.Bytes("a"));
            var root = await store.ListAsync("/");
            Assert.Equal(("in", true), (root[0].Name, root[0].IsDirectory));
            Assert.Contains(await store.ListAsync("/in/partner"), e => e.Path == "/in/partner/a.csv");
            Assert.True(Directory.Exists(Path.Combine(folder, "in", "partner")));
        }
        finally { Directory.Delete(folder, true); }
    }
}
