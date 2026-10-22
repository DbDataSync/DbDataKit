using DbDataKit.Secrets.Store;
using Xunit;

namespace DbDataKit.Secrets.Store.Tests;

public class SecretExportTests
{
    private const string Passphrase = "correct horse battery staple";
    private static SecretName N(string name, string product = "sync") => new(product, "connection", name);

    private static async Task<EncryptedFileSecretStore> Store(TempDir t, string folder, params (SecretName Name, string Value)[] values)
    {
        var directory = System.IO.Path.Combine(t.Path, folder);
        var keys = new KeyRing(System.IO.Path.Combine(directory, "keys"));
        keys.CreateKey();
        var store = new EncryptedFileSecretStore(directory, keys);
        foreach (var (name, value) in values) await store.SetAsync(name, SecretValue.From(value));
        return store;
    }

    [Fact]
    public async Task An_export_imports_into_a_store_with_a_different_key_and_every_value_reads_back()
    {
        using var t = new TempDir();
        var source = await Store(t, "a", (N("one"), "first"), (N("two"), "pässwörd 日本"), (N("proxy-one", "proxy"), "third"));
        var path = System.IO.Path.Combine(t.Path, "backup", "secrets.export.yaml");
        Assert.Equal(3, await source.ExportAsync(path, SecretValue.From(Passphrase)));

        var target = await Store(t, "b");
        var result = await target.ImportAsync(path, SecretValue.From(Passphrase));
        Assert.Equal(new SecretImportResult(3, 0, 0), result);
        foreach (var (name, expected) in new[] { (N("one"), "first"), (N("two"), "pässwörd 日本"), (N("proxy-one", "proxy"), "third") })
        {
            using var value = await target.TryResolveAsync(name.ToRef());
            Assert.Equal(expected, value!.RevealString());
        }
        // the two stores really have different keys
        Assert.NotEqual((await source.ListInfoAsync())[0].KeyId, (await target.ListInfoAsync())[0].KeyId);
    }

    [Fact]
    public async Task The_export_file_holds_no_plaintext_no_store_key_and_the_documented_parameters()
    {
        using var t = new TempDir();
        var source = await Store(t, "a", (N("one"), "S3NT1NEL-value"));
        var path = System.IO.Path.Combine(t.Path, "x.yaml");
        await source.ExportAsync(path, SecretValue.From(Passphrase));
        var text = File.ReadAllText(path);
        Assert.DoesNotContain("S3NT1NEL-value", text);
        Assert.DoesNotContain(Passphrase, text);
        Assert.Contains("format: dbdata-secrets-export", text);
        Assert.Contains("kdf: pbkdf2-sha256", text);
        Assert.Contains("iterations: 600000", text);
        Assert.Contains("dbdata:sync:connection:one", text);
        Assert.DoesNotContain((await source.ListInfoAsync())[0].KeyId, text);       // the store's key id is not there
    }

    [Fact]
    public async Task Two_exports_of_the_same_secrets_differ()
    {
        using var t = new TempDir();
        var source = await Store(t, "a", (N("one"), "same"));
        await source.ExportAsync(System.IO.Path.Combine(t.Path, "1.yaml"), SecretValue.From(Passphrase));
        await source.ExportAsync(System.IO.Path.Combine(t.Path, "2.yaml"), SecretValue.From(Passphrase));
        Assert.NotEqual(File.ReadAllText(System.IO.Path.Combine(t.Path, "1.yaml")), File.ReadAllText(System.IO.Path.Combine(t.Path, "2.yaml")));
    }

    [Fact]
    public async Task A_wrong_passphrase_fails_and_stores_nothing()
    {
        using var t = new TempDir();
        var source = await Store(t, "a", (N("one"), "1"), (N("two"), "2"));
        var path = System.IO.Path.Combine(t.Path, "x.yaml");
        await source.ExportAsync(path, SecretValue.From(Passphrase));
        var target = await Store(t, "b");
        var ex = await Assert.ThrowsAsync<SecretStoreException>(async () => await target.ImportAsync(path, SecretValue.From("another passphrase entirely")));
        Assert.Contains("passphrase", ex.Message);
        Assert.Empty(await target.ListInfoAsync());
    }

    [Fact]
    public async Task A_changed_value_in_the_file_fails_the_whole_import_before_anything_is_stored()
    {
        using var t = new TempDir();
        var source = await Store(t, "a", (N("aaa"), "1"), (N("zzz"), "2"));
        var path = System.IO.Path.Combine(t.Path, "x.yaml");
        await source.ExportAsync(path, SecretValue.From(Passphrase));
        var lines = File.ReadAllLines(path);
        var last = Array.FindLastIndex(lines, l => l.TrimStart().StartsWith("value:", StringComparison.Ordinal));
        var bytes = Convert.FromBase64String(lines[last].Split("value:")[1].Trim().Trim('"', '\''));
        bytes[^1] ^= 1;
        lines[last] = lines[last].Split("value:")[0] + "value: " + Convert.ToBase64String(bytes);
        File.WriteAllLines(path, lines);

        var target = await Store(t, "b");
        await Assert.ThrowsAsync<SecretStoreException>(async () => await target.ImportAsync(path, SecretValue.From(Passphrase)));
        Assert.Empty(await target.ListInfoAsync());                 // the first, intact value was not stored either
    }

    [Fact]
    public async Task A_name_that_is_already_there_is_kept_unless_overwrite_is_asked()
    {
        using var t = new TempDir();
        var source = await Store(t, "a", (N("one"), "from-export"), (N("two"), "also"));
        var path = System.IO.Path.Combine(t.Path, "x.yaml");
        await source.ExportAsync(path, SecretValue.From(Passphrase));
        var target = await Store(t, "b", (N("one"), "already-here"));

        Assert.Equal(new SecretImportResult(1, 1, 0), await target.ImportAsync(path, SecretValue.From(Passphrase)));
        using (var kept = await target.TryResolveAsync(N("one").ToRef())) Assert.Equal("already-here", kept!.RevealString());

        Assert.Equal(new SecretImportResult(0, 0, 2), await target.ImportAsync(path, SecretValue.From(Passphrase), overwrite: true));
        using (var replaced = await target.TryResolveAsync(N("one").ToRef())) Assert.Equal("from-export", replaced!.RevealString());
    }

    [Fact]
    public async Task A_prefix_exports_only_those_names()
    {
        using var t = new TempDir();
        var source = await Store(t, "a", (N("one"), "1"), (N("p", "proxy"), "2"));
        var path = System.IO.Path.Combine(t.Path, "x.yaml");
        Assert.Equal(1, await source.ExportAsync(path, SecretValue.From(Passphrase), prefix: "dbdata:proxy:"));
        var target = await Store(t, "b");
        await target.ImportAsync(path, SecretValue.From(Passphrase));
        Assert.Equal([N("p", "proxy").ToString()], (await target.ListInfoAsync()).Select(i => i.Name.ToString()).ToArray());
    }

    [Fact]
    public async Task A_short_passphrase_is_refused_both_ways()
    {
        using var t = new TempDir();
        var source = await Store(t, "a", (N("one"), "1"));
        await Assert.ThrowsAsync<ArgumentException>(async () => await source.ExportAsync(System.IO.Path.Combine(t.Path, "x.yaml"), SecretValue.From("short")));
        await Assert.ThrowsAsync<ArgumentException>(async () => await source.ImportAsync(System.IO.Path.Combine(t.Path, "x.yaml"), SecretValue.From("short")));
    }

    [Fact]
    public async Task A_file_that_is_not_an_export_or_claims_a_weak_derivation_is_refused_by_name()
    {
        using var t = new TempDir();
        var source = await Store(t, "a", (N("one"), "1"));
        var path = System.IO.Path.Combine(t.Path, "x.yaml");
        await source.ExportAsync(path, SecretValue.From(Passphrase));
        var target = await Store(t, "b");

        var weak = System.IO.Path.Combine(t.Path, "weak.yaml");
        File.WriteAllText(weak, File.ReadAllText(path).Replace("iterations: 600000", "iterations: 1000"));
        Assert.Contains("1000", (await Assert.ThrowsAsync<SecretStoreException>(async () => await target.ImportAsync(weak, SecretValue.From(Passphrase)))).Message);

        var other = System.IO.Path.Combine(t.Path, "other.yaml");
        File.WriteAllText(other, "format: something-else\nversion: 1\n");
        Assert.Contains("not a secret export", (await Assert.ThrowsAsync<SecretStoreException>(async () => await target.ImportAsync(other, SecretValue.From(Passphrase)))).Message);

        var missing = System.IO.Path.Combine(t.Path, "missing.yaml");
        await Assert.ThrowsAsync<FileNotFoundException>(async () => await target.ImportAsync(missing, SecretValue.From(Passphrase)));
    }

    [Fact]
    public async Task On_Unix_the_export_file_is_created_0600()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes");
        using var t = new TempDir();
        var source = await Store(t, "a", (N("one"), "1"));
        var path = System.IO.Path.Combine(t.Path, "x.yaml");
        await source.ExportAsync(path, SecretValue.From(Passphrase));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}
