using System.Security.Cryptography;
using System.Text;
using DbDataKit.Secrets.Store;
using Xunit;

namespace DbDataKit.Secrets.Windows.Tests;

public class DpapiKeyProtectorTests
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("dbdata-test-product");

    [Fact]
    public void A_key_protected_for_the_machine_round_trips_through_a_key_ring()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var t = new TempDir();
        var ring = new KeyRing(Path.Combine(t.Path, "keys"), new DpapiKeyProtector(Entropy));
        var id = ring.CreateKey();
        var key = ring.GetKey(id);
        Assert.Equal(KeyRing.KeyLength, key.Length);

        // a second ring over the same directory, as after a restart
        Assert.Equal(key, new KeyRing(Path.Combine(t.Path, "keys"), new DpapiKeyProtector(Entropy)).GetKey(id));
        // and the file holds no raw key
        Assert.False(File.ReadAllBytes(Path.Combine(t.Path, "keys", id + ".key")).AsSpan().IndexOf(key) >= 0);
    }

    [Fact]
    public void Other_entropy_cannot_unprotect_the_key()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var t = new TempDir();
        var id = new KeyRing(Path.Combine(t.Path, "keys"), new DpapiKeyProtector(Entropy)).CreateKey();
        var other = new KeyRing(Path.Combine(t.Path, "keys"), new DpapiKeyProtector(Encoding.UTF8.GetBytes("another-product")));
        Assert.ThrowsAny<CryptographicException>(() => other.GetKey(id));
    }

    [Fact]
    public void A_key_made_by_a_restricted_file_is_not_read_as_a_DPAPI_one()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var t = new TempDir();
        var id = new KeyRing(Path.Combine(t.Path, "keys"), new RestrictedFileKeyProtector()).CreateKey();
        var ex = Assert.Throws<InvalidDataException>(() => new KeyRing(Path.Combine(t.Path, "keys"), new DpapiKeyProtector(Entropy)).GetKey(id));
        Assert.Contains("DPAPI", ex.Message);
    }

    [Fact]
    public void The_key_directory_is_restricted_on_creation_and_a_loosened_one_is_refused()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only");
        using var t = new TempDir();
        var directory = Path.Combine(t.Path, "keys");
        var ring = new KeyRing(directory, new DpapiKeyProtector(Entropy));
        var id = ring.CreateKey();
        ring.VerifyAll();

        // inherit the parent's access list again: the directory is no longer restricted
        var info = new DirectoryInfo(directory);
        var security = info.GetAccessControl();
        security.SetAccessRuleProtection(isProtected: false, preserveInheritance: true);
        info.SetAccessControl(security);
        Assert.Throws<KeyPermissionException>(() => ring.GetKey(id));
    }
}
