using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using DbData.Security.Acl;
using Xunit;

namespace DbData.Secrets.Windows.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("dbdata-secrets-").FullName;
    public void Dispose() { try { Directory.Delete(Path, true); } catch (IOException) { } }
}

public class AclHelperWindowsTests
{
    private static readonly string[] Allowed = ["CurrentUser", "SYSTEM", "Administrators"];

    [Fact]
    public void Restrict_File_ThenVerify_InSync()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "a.pfx");
        File.WriteAllText(f, "x");
        Assert.False(AclHelper.Verify(f, Allowed).InSync); // inherited ACEs initially

        AclHelper.Restrict(f, Allowed);
        var report = AclHelper.Verify(f, Allowed);
        Assert.True(report.InSync, string.Join("; ", report.Drift.Select(d => d.Description)));
        Assert.Equal("x", File.ReadAllText(f)); // current user kept access
    }

    [Fact]
    public void Verify_ReportsExtraGrant_AndMissingGrant()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "k");
        File.WriteAllText(f, "x");
        AclHelper.Restrict(f, Allowed);

        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var info = new FileInfo(f);
        var sec = info.GetAccessControl();
        sec.AddAccessRule(new FileSystemAccessRule(everyone, FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(sec);

        var report = AclHelper.Verify(f, Allowed);
        Assert.False(report.InSync);
        Assert.Contains(report.Drift, d => d.Description.StartsWith("unexpected grant to", StringComparison.Ordinal));

        var missing = AclHelper.Verify(f, [.. Allowed, "S-1-5-19"]);
        Assert.Contains(missing.Drift, d => d.Description.StartsWith("missing grant for", StringComparison.Ordinal));
    }

    [Fact]
    public void Restrict_Directory_Propagates_ToChildren_AndRecursiveVerifyPasses()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var t = new TempDir();
        var d = Path.Combine(t.Path, "secrets");
        AclHelper.EnsureRestrictedDirectory(d, Allowed);
        AclHelper.WriteAllBytesRestricted(Path.Combine(d, "one"), "pw"u8, Allowed);
        File.WriteAllText(Path.Combine(d, "two"), "inherits");

        var report = AclHelper.Verify(d, Allowed, recurse: true);
        Assert.True(report.InSync, string.Join("; ", report.Drift.Select(x => x.Description)));
        Assert.Equal("pw", File.ReadAllText(Path.Combine(d, "one")));
    }

    [Fact]
    public void ResolveIdentity_Aliases_AndSid()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        Assert.Equal("S-1-5-18", AclHelper.ResolveIdentity("system").Value);
        Assert.Equal("S-1-5-32-544", AclHelper.ResolveIdentity("Administrators").Value);
        Assert.Equal(WindowsIdentity.GetCurrent().User, AclHelper.ResolveIdentity("CurrentUser"));
        Assert.Equal("S-1-5-19", AclHelper.ResolveIdentity("S-1-5-19").Value);
    }

    [Fact]
    public void Restrict_Missing_Throws_AndEmptyIdentitiesRejected()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var t = new TempDir();
        Assert.Throws<FileNotFoundException>(() => AclHelper.Restrict(Path.Combine(t.Path, "nope"), Allowed));
        var f = Path.Combine(t.Path, "f");
        File.WriteAllText(f, "");
        Assert.Throws<ArgumentException>(() => AclHelper.Restrict(f, []));
        Assert.False(AclHelper.Verify(Path.Combine(t.Path, "nope"), Allowed).InSync);
    }
}

public class AclHelperUnixTests
{
    [Fact]
    public void Restrict_SetsMode0600_And0700_AndVerifyDetectsDrift()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only");
        using var t = new TempDir();
        var f = Path.Combine(t.Path, "k");
        File.WriteAllText(f, "x");
        File.SetUnixFileMode(f, (UnixFileMode)Convert.ToInt32("644", 8));
        Assert.False(AclHelper.Verify(f, []).InSync);
        AclHelper.Restrict(f, []);
        Assert.Equal((UnixFileMode)Convert.ToInt32("600", 8), File.GetUnixFileMode(f));
        Assert.True(AclHelper.Verify(f, []).InSync);

        AclHelper.EnsureRestrictedDirectory(Path.Combine(t.Path, "d"), []);
        Assert.Equal((UnixFileMode)Convert.ToInt32("700", 8), File.GetUnixFileMode(Path.Combine(t.Path, "d")));
        AclHelper.WriteAllBytesRestricted(Path.Combine(t.Path, "d", "s"), "pw"u8, []);
        Assert.True(AclHelper.Verify(Path.Combine(t.Path, "d"), [], recurse: true).InSync);
    }
}

public class DpapiTests
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("dbdata-test");

    [Fact]
    public async Task Store_RoundTrip_List_Delete_AndFileIsNotPlaintext()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var t = new TempDir();
        var store = new DpapiSecretStore(t.Path, Entropy, DataProtectionScope.CurrentUser, ["CurrentUser"]);
        var name = SecretName.Parse("dbdata:proxy:datasource:erp");
        using (var v = SecretValue.From("s3cr3t-value"))
            await store.SetAsync(name, v);

        var raw = File.ReadAllBytes(Directory.GetFiles(t.Path)[0]);
        Assert.DoesNotContain("s3cr3t-value", Encoding.UTF8.GetString(raw));
        Assert.True(AclHelper.Verify(t.Path, ["CurrentUser"], recurse: true).InSync);

        using var got = await store.TryResolveAsync(name.ToRef());
        Assert.Equal("s3cr3t-value", got!.RevealString());

        await store.SetAsync(SecretName.Parse("dbdata:build:connection:wh:write"), SecretValue.From("b"));
        var all = new List<SecretName>();
        await foreach (var n in store.ListAsync("dbdata:proxy:")) all.Add(n);
        Assert.Equal([name], all);

        await store.DeleteAsync(name);
        Assert.Null(await store.TryResolveAsync(name.ToRef()));
        await store.DeleteAsync(name); // idempotent
    }

    [Fact]
    public async Task Inline_Ref_RoundTrip_AndWrongEntropyFails()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var t = new TempDir();
        using var secret = SecretValue.From("inline-pw");
        var reference = DpapiProtector.ToInlineRef(secret, Entropy, DataProtectionScope.CurrentUser);
        Assert.StartsWith("dpapi:", reference);
        Assert.DoesNotContain("inline-pw", reference);

        var store = new DpapiSecretStore(t.Path, Entropy, DataProtectionScope.CurrentUser);
        using var got = await store.TryResolveAsync(SecretRef.Parse(reference));
        Assert.Equal("inline-pw", got!.RevealString());

        var other = new DpapiSecretStore(t.Path, "different"u8.ToArray(), DataProtectionScope.CurrentUser);
        await Assert.ThrowsAsync<CryptographicException>(async () => await other.TryResolveAsync(SecretRef.Parse(reference)));
    }

    [Fact]
    public void MachineScope_Works_AndCanHandle_Rules()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        var blob = DpapiProtector.Protect("m".AsSpan(), Entropy, DataProtectionScope.LocalMachine);
        using var v = DpapiProtector.Unprotect(blob, Entropy, DataProtectionScope.LocalMachine);
        Assert.Equal("m", v.RevealString());

        var store = new DpapiSecretStore(Path.GetTempPath(), Entropy);
        Assert.True(store.CanHandle(SecretRef.Parse("dpapi:AAAA")));
        Assert.True(store.CanHandle(SecretRef.Parse("secret:dbdata:a:b:c")));
        Assert.False(store.CanHandle(SecretRef.Parse("secret:not-a-name")));
        Assert.False(store.CanHandle(SecretRef.Parse("env:X")));
    }
}
