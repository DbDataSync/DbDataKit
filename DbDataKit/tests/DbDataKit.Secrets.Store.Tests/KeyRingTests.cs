using System.Security.Cryptography;
using System.Text;
using DbDataKit.Secrets.Store;
using Xunit;

namespace DbDataKit.Secrets.Store.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("dbdata-keys-").FullName;
    public void Dispose()
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                foreach (var entry in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(entry, Directory.Exists(entry) ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute : UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Directory.Delete(Path, true);
        }
        catch (IOException) { }
    }
}

public class KeyRingTests
{
    [Fact]
    public void A_key_is_random_256_bits_and_reads_back_from_a_new_ring_over_the_same_directory()
    {
        using var t = new TempDir();
        var directory = System.IO.Path.Combine(t.Path, "keys");
        var ring = new KeyRing(directory);
        var id = ring.CreateKey();
        var key = ring.GetKey(id);
        Assert.Equal(32, key.Length);
        Assert.NotEqual(new byte[32], key);
        Assert.Equal(key, new KeyRing(directory).GetKey(id));
        Assert.Equal(id, new KeyRing(directory).ActiveKeyId);
        Assert.Equal([id], ring.KeyIds());
    }

    [Fact]
    public void Two_keys_differ_and_the_first_stays_active_until_another_is_activated()
    {
        using var t = new TempDir();
        var ring = new KeyRing(System.IO.Path.Combine(t.Path, "keys"));
        var first = ring.CreateKey();
        var second = ring.CreateKey();
        Assert.NotEqual(first, second);
        Assert.NotEqual(ring.GetKey(first), ring.GetKey(second));
        Assert.Equal(first, ring.ActiveKeyId);
        ring.Activate(second);
        Assert.Equal(second, ring.ActiveKeyId);
        Assert.Equal(2, ring.KeyIds().Count);
    }

    [Fact]
    public void The_active_key_cannot_be_deleted_but_an_old_one_can()
    {
        using var t = new TempDir();
        var ring = new KeyRing(System.IO.Path.Combine(t.Path, "keys"));
        var first = ring.CreateKey();
        var second = ring.CreateKey(activate: true);
        Assert.Throws<InvalidOperationException>(() => ring.DeleteKey(second));
        ring.DeleteKey(first);
        Assert.Equal([second], ring.KeyIds());
        Assert.Throws<KeyNotFoundException>(() => ring.GetKey(first));
    }

    [Fact]
    public void A_key_id_that_is_a_path_is_refused()
    {
        using var t = new TempDir();
        var ring = new KeyRing(System.IO.Path.Combine(t.Path, "keys"));
        ring.CreateKey();
        Assert.Throws<ArgumentException>(() => ring.GetKey("../../etc/passwd"));
        Assert.Throws<ArgumentException>(() => ring.GetKey(""));
    }

    [Fact]
    public void The_key_file_holds_the_magic_and_no_other_files_are_left_behind()
    {
        using var t = new TempDir();
        var directory = System.IO.Path.Combine(t.Path, "keys");
        var id = new KeyRing(directory).CreateKey();
        Assert.StartsWith("DBKEY", Encoding.ASCII.GetString(File.ReadAllBytes(System.IO.Path.Combine(directory, id + ".key")), 0, 5));
        Assert.Equal([id + ".key", "active"], Directory.EnumerateFiles(directory).Select(System.IO.Path.GetFileName).Order().ToArray());
    }

    [Fact]
    public void A_file_that_is_not_a_key_is_refused_by_name()
    {
        using var t = new TempDir();
        var directory = System.IO.Path.Combine(t.Path, "keys");
        var ring = new KeyRing(directory);
        var id = ring.CreateKey();
        File.WriteAllText(System.IO.Path.Combine(directory, id + ".key"), "not a key");
        var ex = Assert.Throws<InvalidDataException>(() => ring.GetKey(id));
        Assert.Contains(id + ".key", ex.Message);
    }

    [Fact]
    public void A_key_written_by_another_kind_of_protection_is_not_read_as_a_raw_one()
    {
        using var t = new TempDir();
        var directory = System.IO.Path.Combine(t.Path, "keys");
        var ring = new KeyRing(directory);
        var id = ring.CreateKey();
        var path = System.IO.Path.Combine(directory, id + ".key");
        var bytes = File.ReadAllBytes(path);
        bytes[6] = 2;                                   // kind: DPAPI for the machine
        File.WriteAllBytes(path, bytes);
        var ex = Assert.Throws<InvalidDataException>(() => ring.GetKey(id));
        Assert.Contains("kind 2", ex.Message);
    }

    [Fact]
    public void On_Unix_the_directory_is_0700_and_every_file_0600()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes");
        using var t = new TempDir();
        var directory = System.IO.Path.Combine(t.Path, "keys");
        new KeyRing(directory).CreateKey();
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        foreach (var file in Directory.EnumerateFiles(directory))
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
    }

    [Theory]
    [InlineData(UnixFileMode.GroupRead)]
    [InlineData(UnixFileMode.OtherRead)]
    [InlineData(UnixFileMode.OtherExecute)]
    [InlineData(UnixFileMode.GroupWrite)]
    public void On_Unix_a_key_file_that_others_can_reach_is_refused_with_the_fix(UnixFileMode extra)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes");
        using var t = new TempDir();
        var directory = System.IO.Path.Combine(t.Path, "keys");
        var ring = new KeyRing(directory);
        var id = ring.CreateKey();
        var path = System.IO.Path.Combine(directory, id + ".key");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | extra);
        var ex = Assert.Throws<KeyPermissionException>(() => ring.GetKey(id));
        Assert.Equal(path, ex.Path);
        Assert.Contains("chmod 600", ex.Message);
        Assert.Throws<KeyPermissionException>(() => ring.VerifyAll());
    }

    [Fact]
    public void On_Unix_a_key_directory_that_others_can_enter_is_refused()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes");
        using var t = new TempDir();
        var directory = System.IO.Path.Combine(t.Path, "keys");
        var ring = new KeyRing(directory);
        var id = ring.CreateKey();
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherExecute);
        var ex = Assert.Throws<KeyPermissionException>(() => ring.GetKey(id));
        Assert.Contains("chmod 700", ex.Message);
    }

    [Fact]
    public void On_Unix_the_file_is_owned_by_the_current_user()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix ownership");
        using var t = new TempDir();
        var owned = UnixOwnership.IsOwnedByCurrentUser(t.Path);
        Assert.SkipWhen(owned is null, "stat and id are not available");
        Assert.True(owned);
        // a path that someone else owns: /etc/hostname-like roots are root's (the test runs as a user; as root there is no other owner to find)
        Assert.SkipWhen(Environment.UserName == "root", "running as root");
        Assert.False(UnixOwnership.IsOwnedByCurrentUser("/"));
    }
}

public class ExternalKeyFileTests
{
    private static readonly byte[] Key = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void A_raw_key_a_base64_key_and_a_hex_key_are_the_same_key_with_the_same_id()
    {
        using var t = new TempDir();
        var raw = System.IO.Path.Combine(t.Path, "raw"); File.WriteAllBytes(raw, Key);
        var b64 = System.IO.Path.Combine(t.Path, "b64"); File.WriteAllText(b64, Convert.ToBase64String(Key) + "\n");
        var hex = System.IO.Path.Combine(t.Path, "hex"); File.WriteAllText(hex, Convert.ToHexString(Key).ToLowerInvariant() + "\r\n");
        var sources = new[] { raw, b64, hex }.Select(p => new ExternalKeyFile(p)).ToArray();
        Assert.All(sources, s => Assert.Equal(sources[0].ActiveKeyId, s.ActiveKeyId));
        Assert.All(sources, s => Assert.Equal(Key, s.GetKey(s.ActiveKeyId)));
        Assert.StartsWith("ext-", sources[0].ActiveKeyId);
    }

    [Fact]
    public void A_key_of_the_wrong_length_is_refused_and_a_missing_file_is_named()
    {
        using var t = new TempDir();
        var short_ = System.IO.Path.Combine(t.Path, "short"); File.WriteAllBytes(short_, new byte[16]);
        Assert.Contains("256-bit", Assert.Throws<InvalidDataException>(() => new ExternalKeyFile(short_)).Message);
        var missing = System.IO.Path.Combine(t.Path, "nope");
        Assert.Contains(missing, Assert.Throws<FileNotFoundException>(() => new ExternalKeyFile(missing)).Message);
    }

    [Fact]
    public void Asking_for_another_key_than_the_one_in_the_file_says_it_is_a_different_key()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "k"); File.WriteAllBytes(path, Key);
        var source = new ExternalKeyFile(path);
        var ex = Assert.Throws<KeyNotFoundException>(() => source.GetKey("ext-000000000000"));
        Assert.Contains("not the one these secrets were written with", ex.Message);
    }

    [Fact]
    public void A_file_that_changes_to_another_key_is_noticed()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "k"); File.WriteAllBytes(path, Key);
        var source = new ExternalKeyFile(path);
        var id = source.ActiveKeyId;
        File.WriteAllBytes(path, RandomNumberGenerator.GetBytes(32));
        Assert.Throws<KeyNotFoundException>(() => source.GetKey(id));
    }
}

public class SecretKeysTests
{
    [Fact]
    public void The_first_use_of_a_data_directory_makes_the_ring_and_a_later_use_finds_the_same_key()
    {
        using var t = new TempDir();
        var first = SecretKeys.Open(t.Path, environment: _ => null);
        var again = SecretKeys.Open(t.Path, environment: _ => null);
        Assert.Equal(first.ActiveKeyId, again.ActiveKeyId);
        Assert.True(Directory.Exists(System.IO.Path.Combine(t.Path, "secrets", "keys")));
    }

    [Fact]
    public void A_key_file_in_the_options_or_the_environment_takes_the_place_of_the_ring_and_nothing_is_created()
    {
        using var t = new TempDir();
        var path = System.IO.Path.Combine(t.Path, "provided.key"); File.WriteAllBytes(path, new byte[32].Select((_, i) => (byte)(i + 7)).ToArray());
        var data = System.IO.Path.Combine(t.Path, "data"); Directory.CreateDirectory(data);

        var fromOptions = SecretKeys.Open(data, new SecretKeyOptions { KeyFile = path }, environment: _ => null);
        var fromEnvironment = SecretKeys.Open(data, null, environment: name => name == SecretKeyOptions.KeyFileVariable ? path : null);
        Assert.IsType<ExternalKeyFile>(fromOptions);
        Assert.Equal(fromOptions.ActiveKeyId, fromEnvironment.ActiveKeyId);
        Assert.False(Directory.Exists(System.IO.Path.Combine(data, "secrets")));
    }

    [Fact]
    public void The_option_wins_over_the_environment()
    {
        using var t = new TempDir();
        var a = System.IO.Path.Combine(t.Path, "a"); File.WriteAllBytes(a, new byte[32].Select((_, i) => (byte)i).ToArray());
        var b = System.IO.Path.Combine(t.Path, "b"); File.WriteAllBytes(b, new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());
        var source = SecretKeys.Open(t.Path, new SecretKeyOptions { KeyFile = a }, environment: _ => b);
        Assert.Equal(new ExternalKeyFile(a).ActiveKeyId, source.ActiveKeyId);
    }

    [Fact]
    public void Opening_refuses_a_ring_whose_key_others_can_read()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes");
        using var t = new TempDir();
        var source = (KeyRing)SecretKeys.Open(t.Path, environment: _ => null);
        var file = Directory.EnumerateFiles(source.DirectoryPath, "*.key").Single();
        File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        Assert.Throws<KeyPermissionException>(() => SecretKeys.Open(t.Path, environment: _ => null));
    }
}
