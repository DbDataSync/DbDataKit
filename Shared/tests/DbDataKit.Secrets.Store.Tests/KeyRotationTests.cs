using DbDataKit.Secrets.Store;
using Xunit;

namespace DbDataKit.Secrets.Store.Tests;

public class KeyRotationTests
{
    private static SecretName N(string name) => new("sync", "connection", name);

    private static async Task<(EncryptedFileSecretStore Store, KeyRing Keys, string Directory)> Seeded(TempDir t, int count = 5)
    {
        var directory = System.IO.Path.Combine(t.Path, "secrets");
        var keys = new KeyRing(System.IO.Path.Combine(directory, "keys"));
        keys.CreateKey();
        var store = new EncryptedFileSecretStore(directory, keys);
        for (var i = 0; i < count; i++) await store.SetAsync(N($"c{i}"), SecretValue.From($"value-{i}"));
        return (store, keys, directory);
    }

    private static async Task AssertAllReadAsync(EncryptedFileSecretStore store, int count, string expectedKeyId)
    {
        for (var i = 0; i < count; i++)
        {
            using var value = await store.TryResolveAsync(N($"c{i}").ToRef());
            Assert.Equal($"value-{i}", value!.RevealString());
        }
        Assert.All(await store.ListInfoAsync(), info => Assert.Equal(expectedKeyId, info.KeyId));
    }

    [Fact]
    public async Task A_rotation_moves_every_value_to_a_new_key_and_deletes_the_old_one()
    {
        using var t = new TempDir();
        var (store, keys, directory) = await Seeded(t);
        var old = keys.ActiveKeyId;

        var result = await store.RotateKeyAsync();

        Assert.NotEqual(old, result.NewKeyId);
        Assert.Equal(5, result.Reencrypted);
        Assert.Equal([old], result.RemovedKeyIds);
        Assert.False(result.Resumed);
        Assert.Equal([result.NewKeyId], keys.KeyIds());
        Assert.Equal(result.NewKeyId, keys.ActiveKeyId);
        await AssertAllReadAsync(store, 5, result.NewKeyId);
        Assert.False(store.RotationPending);
        Assert.False(File.Exists(System.IO.Path.Combine(directory, "rotation.state")));
    }

    [Fact]
    public async Task The_old_key_alone_no_longer_reads_anything_after_a_rotation()
    {
        using var t = new TempDir();
        var (store, keys, directory) = await Seeded(t, 2);
        var oldBytes = keys.GetKey(keys.ActiveKeyId);
        await store.RotateKeyAsync();
        // a store given only the old key's bytes (provided from outside) cannot read the values
        var keyFile = System.IO.Path.Combine(t.Path, "old.key");
        File.WriteAllBytes(keyFile, oldBytes);
        var withOld = new EncryptedFileSecretStore(directory, new ExternalKeyFile(keyFile));
        await Assert.ThrowsAsync<SecretStoreException>(async () => await withOld.TryResolveAsync(N("c0").ToRef()));
    }

    [Theory]
    [InlineData("key-created")]
    [InlineData("values-rewritten")]
    [InlineData("old-keys-removed")]
    public async Task A_rotation_stopped_at_any_step_still_reads_and_the_next_run_finishes_it(string stopAt)
    {
        using var t = new TempDir();
        var (store, keys, _) = await Seeded(t);

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.RotateKeyAsync(step => { if (step == stopAt) throw new OperationCanceledException("killed at " + step); }));

        // the store is readable whatever the state it was left in
        for (var i = 0; i < 5; i++)
        {
            using var value = await store.TryResolveAsync(N($"c{i}").ToRef());
            Assert.Equal($"value-{i}", value!.RevealString());
        }
        Assert.True(store.RotationPending);

        var result = await store.RotateKeyAsync();
        Assert.True(result.Resumed);
        Assert.Equal([result.NewKeyId], keys.KeyIds());
        await AssertAllReadAsync(store, 5, result.NewKeyId);
        Assert.False(store.RotationPending);
    }

    [Fact]
    public async Task A_stopped_rotation_resumed_by_a_new_process_finishes_it_with_the_same_target_key()
    {
        using var t = new TempDir();
        var (store, keys, directory) = await Seeded(t);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await store.RotateKeyAsync(step => { if (step == "values-rewritten") throw new OperationCanceledException(); }));
        var target = keys.ActiveKeyId;

        var restarted = new EncryptedFileSecretStore(directory, new KeyRing(System.IO.Path.Combine(directory, "keys")));
        var result = await restarted.RotateKeyAsync();
        Assert.Equal(target, result.NewKeyId);
        Assert.Equal(0, result.Reencrypted);                       // every value had already moved
        Assert.Single(new KeyRing(System.IO.Path.Combine(directory, "keys")).KeyIds());
    }

    [Fact]
    public async Task Two_rotations_in_a_row_each_end_on_one_key_and_keep_the_values()
    {
        using var t = new TempDir();
        var (store, keys, _) = await Seeded(t, 3);
        var first = await store.RotateKeyAsync();
        var second = await store.RotateKeyAsync();
        Assert.NotEqual(first.NewKeyId, second.NewKeyId);
        Assert.Equal([second.NewKeyId], keys.KeyIds());
        await AssertAllReadAsync(store, 3, second.NewKeyId);
    }

    [Fact]
    public async Task A_rotation_of_an_empty_store_just_changes_the_key()
    {
        using var t = new TempDir();
        var (store, keys, _) = await Seeded(t, 0);
        var old = keys.ActiveKeyId;
        var result = await store.RotateKeyAsync();
        Assert.Equal(0, result.Reencrypted);
        Assert.NotEqual(old, keys.ActiveKeyId);
    }

    [Fact]
    public async Task A_key_provided_from_outside_is_not_rotated_here()
    {
        using var t = new TempDir();
        var keyFile = System.IO.Path.Combine(t.Path, "provided.key");
        File.WriteAllBytes(keyFile, new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());
        var store = new EncryptedFileSecretStore(System.IO.Path.Combine(t.Path, "secrets"), new ExternalKeyFile(keyFile));
        var ex = await Assert.ThrowsAsync<NotSupportedException>(async () => await store.RotateKeyAsync());
        Assert.Contains("provided from outside", ex.Message);
    }

    [Fact]
    public async Task A_rotation_state_naming_a_key_that_is_gone_is_reported_with_how_to_clear_it()
    {
        using var t = new TempDir();
        var (store, _, directory) = await Seeded(t, 1);
        File.WriteAllText(System.IO.Path.Combine(directory, "rotation.state"), "0123456789ab");
        var ex = await Assert.ThrowsAsync<SecretStoreException>(async () => await store.RotateKeyAsync());
        Assert.Contains("rotation.state", ex.Message);
    }
}
