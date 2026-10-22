using System.Text;
using DbDataKit.Secrets.Store;
using Xunit;

namespace DbDataKit.Secrets.Store.Tests;

public class EncryptedFileSecretStoreTests
{
    private const string Sentinel = "S3NT1NEL-p@ss/w0rd=#1";
    private static readonly SecretName Erp = new("proxy", "datasource", "erp", "password");
    private static readonly SecretName Crm = new("proxy", "datasource", "crm", "password");

    private static (EncryptedFileSecretStore Store, KeyRing Keys, string Directory) Make(TempDir t)
    {
        var directory = System.IO.Path.Combine(t.Path, "secrets");
        var keys = new KeyRing(System.IO.Path.Combine(directory, "keys"));
        keys.CreateKey();
        return (new EncryptedFileSecretStore(directory, keys), keys, directory);
    }

    [Fact]
    public async Task A_value_round_trips_and_the_file_holds_only_ciphertext()
    {
        using var t = new TempDir();
        var (store, _, directory) = Make(t);
        await store.SetAsync(Erp, SecretValue.From(Sentinel));

        using var value = await store.TryResolveAsync(Erp.ToRef());
        Assert.Equal(Sentinel, value!.RevealString());
        var file = File.ReadAllText(System.IO.Path.Combine(directory, "secrets.yaml"));
        Assert.DoesNotContain(Sentinel, file);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(Sentinel)), file);
        Assert.Contains("dbdata:proxy:datasource:erp:password", file);
    }

    [Fact]
    public async Task A_second_store_over_the_same_files_reads_it_back_and_a_missing_name_is_null()
    {
        using var t = new TempDir();
        var (store, keys, directory) = Make(t);
        await store.SetAsync(Erp, SecretValue.From("one"));
        var again = new EncryptedFileSecretStore(directory, new KeyRing(System.IO.Path.Combine(directory, "keys")));
        using var value = await again.TryResolveAsync(Erp.ToRef());
        Assert.Equal("one", value!.RevealString());
        Assert.Null(await again.TryResolveAsync(Crm.ToRef()));
        Assert.NotNull(keys);
    }

    [Fact]
    public async Task Setting_a_name_again_replaces_its_value_with_a_new_nonce()
    {
        using var t = new TempDir();
        var (store, _, directory) = Make(t);
        await store.SetAsync(Erp, SecretValue.From("same"));
        var first = File.ReadAllText(System.IO.Path.Combine(directory, "secrets.yaml"));
        await store.SetAsync(Erp, SecretValue.From("same"));
        var second = File.ReadAllText(System.IO.Path.Combine(directory, "secrets.yaml"));
        Assert.NotEqual(first, second);                                    // the same value, a different ciphertext
        using var value = await store.TryResolveAsync(Erp.ToRef());
        Assert.Equal("same", value!.RevealString());
        Assert.Single(await store.ListInfoAsync());
    }

    [Fact]
    public async Task An_empty_value_and_one_with_non_ASCII_text_round_trip()
    {
        using var t = new TempDir();
        var (store, _, _) = Make(t);
        await store.SetAsync(Erp, SecretValue.From(""));
        await store.SetAsync(Crm, SecretValue.From("pässwörd-日本語-🔑"));
        using var empty = await store.TryResolveAsync(Erp.ToRef());
        using var unicode = await store.TryResolveAsync(Crm.ToRef());
        Assert.Equal("", empty!.RevealString());
        Assert.Equal("pässwörd-日本語-🔑", unicode!.RevealString());
    }

    [Fact]
    public async Task A_changed_byte_in_a_value_fails_to_decrypt_and_names_the_secret_but_not_the_value()
    {
        using var t = new TempDir();
        var (store, _, directory) = Make(t);
        await store.SetAsync(Erp, SecretValue.From(Sentinel));
        var path = System.IO.Path.Combine(directory, "secrets.yaml");
        var lines = File.ReadAllLines(path);
        var index = Array.FindIndex(lines, l => l.TrimStart().StartsWith("value:", StringComparison.Ordinal));
        var encoded = lines[index].Split("value:")[1].Trim().Trim('"', '\'');
        var bytes = Convert.FromBase64String(encoded);
        bytes[^1] ^= 0x01;                                                   // the last byte of the tag
        lines[index] = lines[index].Split("value:")[0] + "value: " + Convert.ToBase64String(bytes);
        File.WriteAllLines(path, lines);

        var ex = await Assert.ThrowsAsync<SecretStoreException>(async () => await store.TryResolveAsync(Erp.ToRef()));
        Assert.Contains(Erp.ToString(), ex.Message);
        Assert.DoesNotContain(Sentinel, ex.Message);
    }

    [Fact]
    public async Task A_value_moved_to_another_name_fails_to_decrypt()
    {
        using var t = new TempDir();
        var (store, _, directory) = Make(t);
        await store.SetAsync(Erp, SecretValue.From("erp-secret"));
        await store.SetAsync(Crm, SecretValue.From("crm-secret"));
        var path = System.IO.Path.Combine(directory, "secrets.yaml");
        var lines = File.ReadAllLines(path);
        string ValueOf(string name) => lines[Array.FindIndex(lines, l => l.Contains(name, StringComparison.Ordinal))..].First(l => l.TrimStart().StartsWith("value:", StringComparison.Ordinal));
        var erpLine = ValueOf("erp");
        var crmLine = ValueOf("crm");
        File.WriteAllText(path, File.ReadAllText(path).Replace(crmLine, "§").Replace(erpLine, crmLine).Replace("§", erpLine));     // swap the two ciphertexts

        await Assert.ThrowsAsync<SecretStoreException>(async () => await store.TryResolveAsync(Erp.ToRef()));
        await Assert.ThrowsAsync<SecretStoreException>(async () => await store.TryResolveAsync(Crm.ToRef()));
    }

    [Fact]
    public async Task A_value_written_with_another_key_names_the_key_it_needs()
    {
        using var t = new TempDir();
        var (store, keys, directory) = Make(t);
        await store.SetAsync(Erp, SecretValue.From("x"));
        var other = new KeyRing(System.IO.Path.Combine(t.Path, "other-keys"));
        other.CreateKey();
        var wrong = new EncryptedFileSecretStore(directory, other);
        var ex = await Assert.ThrowsAsync<SecretStoreException>(async () => await wrong.TryResolveAsync(Erp.ToRef()));
        Assert.Contains(keys.ActiveKeyId, ex.Message);
    }

    [Fact]
    public async Task Concurrent_writers_in_two_stores_lose_nothing_and_leave_a_readable_file()
    {
        using var t = new TempDir();
        var (a, _, directory) = Make(t);
        var b = new EncryptedFileSecretStore(directory, new KeyRing(System.IO.Path.Combine(directory, "keys")));
        const int each = 40;
        var writes = Enumerable.Range(0, each).Select(i => a.SetAsync(new SecretName("sync", "connection", $"a{i}"), SecretValue.From($"a-value-{i}")))
            .Concat(Enumerable.Range(0, each).Select(i => b.SetAsync(new SecretName("sync", "connection", $"b{i}"), SecretValue.From($"b-value-{i}"))));
        await Task.WhenAll(writes);

        var names = await a.ListInfoAsync();
        Assert.Equal(each * 2, names.Count);
        for (var i = 0; i < each; i++)
        {
            using var va = await b.TryResolveAsync(new SecretName("sync", "connection", $"a{i}").ToRef());
            using var vb = await a.TryResolveAsync(new SecretName("sync", "connection", $"b{i}").ToRef());
            Assert.Equal($"a-value-{i}", va!.RevealString());
            Assert.Equal($"b-value-{i}", vb!.RevealString());
        }
        Assert.Equal(["secrets.yaml"], Directory.EnumerateFiles(directory).Select(System.IO.Path.GetFileName).Where(f => f!.StartsWith("secrets.")).Order().ToArray());
    }

    [Fact]
    public async Task Delete_removes_one_name_and_list_filters_by_prefix_and_gives_names_only()
    {
        using var t = new TempDir();
        var (store, _, _) = Make(t);
        await store.SetAsync(Erp, SecretValue.From("1"));
        await store.SetAsync(Crm, SecretValue.From("2"));
        await store.SetAsync(new SecretName("sync", "connection", "wh"), SecretValue.From("3"));

        var proxy = new List<SecretName>();
        await foreach (var name in store.ListAsync("dbdata:proxy:")) proxy.Add(name);
        Assert.Equal([Crm, Erp], proxy.OrderBy(n => n.ToString(), StringComparer.Ordinal).ToList());

        await store.DeleteAsync(Erp);
        Assert.False(await store.ExistsAsync(Erp));
        Assert.True(await store.ExistsAsync(Crm));
        await store.DeleteAsync(Erp);                                         // deleting what is not there is not an error
    }

    [Fact]
    public async Task It_answers_only_secret_references_and_the_resolver_chain_can_use_it()
    {
        using var t = new TempDir();
        var (store, _, _) = Make(t);
        Assert.True(store.CanHandle(Erp.ToRef()));
        Assert.False(store.CanHandle(new SecretRef(SecretRefKind.Env, "HOME")));
        Assert.False(store.CanHandle(new SecretRef(SecretRefKind.Secret, "not-a-name")));
        await store.SetAsync(Erp, SecretValue.From("via-chain"));
        var chain = new SecretResolverChain([store]);
        using var value = await chain.ResolveAsync(SecretRef.Parse("secret:" + Erp));
        Assert.Equal("via-chain", value.RevealString());
    }

    [Fact]
    public async Task A_file_that_is_not_a_store_is_named_and_a_name_that_is_not_a_secret_name_is_refused()
    {
        using var t = new TempDir();
        var (store, _, directory) = Make(t);
        File.WriteAllText(System.IO.Path.Combine(directory, "secrets.yaml"), "this: is: not: valid: yaml: [");
        var ex = await Assert.ThrowsAsync<SecretStoreException>(async () => await store.TryResolveAsync(Erp.ToRef()));
        Assert.Contains("secrets.yaml", ex.Message);

        File.WriteAllText(System.IO.Path.Combine(directory, "secrets.yaml"), "version: 1\nsecrets:\n  '../../etc/passwd':\n    v: 1\n    key: x\n    value: eA==\n");
        var ex2 = await Assert.ThrowsAsync<SecretStoreException>(async () => await store.TryResolveAsync(Erp.ToRef()));
        Assert.Contains("not a secret name", ex2.Message);
    }

    [Fact]
    public async Task On_Unix_the_store_directory_and_file_are_restricted()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix modes");
        using var t = new TempDir();
        var (store, _, directory) = Make(t);
        await store.SetAsync(Erp, SecretValue.From("x"));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(directory));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(System.IO.Path.Combine(directory, "secrets.yaml")));
    }

    [Fact]
    public async Task An_old_value_still_reads_after_the_active_key_changes_and_a_new_one_uses_the_new_key()
    {
        using var t = new TempDir();
        var (store, keys, _) = Make(t);
        var oldKey = keys.ActiveKeyId;
        await store.SetAsync(Erp, SecretValue.From("written-with-old"));
        var newKey = keys.CreateKey(activate: true);
        await store.SetAsync(Crm, SecretValue.From("written-with-new"));

        var info = (await store.ListInfoAsync()).ToDictionary(i => i.Name.ToString(), i => i.KeyId);
        Assert.Equal(oldKey, info[Erp.ToString()]);
        Assert.Equal(newKey, info[Crm.ToString()]);
        using var a = await store.TryResolveAsync(Erp.ToRef());
        using var b = await store.TryResolveAsync(Crm.ToRef());
        Assert.Equal("written-with-old", a!.RevealString());
        Assert.Equal("written-with-new", b!.RevealString());
    }
}
