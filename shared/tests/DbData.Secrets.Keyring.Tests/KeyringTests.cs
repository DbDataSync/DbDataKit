using Xunit;

namespace DbData.Secrets.Keyring.Tests;

public class KeyringTests
{
    private sealed class FakeBackend : IKeyringBackend
    {
        public readonly Dictionary<(string, string), string> Items = [];
        public Task<string?> GetAsync(string s, string n, CancellationToken ct) => Task.FromResult(Items.GetValueOrDefault((s, n)));
        public Task SetAsync(string s, string n, string v, CancellationToken ct) { Items[(s, n)] = v; return Task.CompletedTask; }
        public Task DeleteAsync(string s, string n, CancellationToken ct) { Items.Remove((s, n)); return Task.CompletedTask; }
        public async IAsyncEnumerable<string> ListAsync(string s, string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            foreach (var (svc, n) in Items.Keys) if (svc == s && n.StartsWith(prefix, StringComparison.Ordinal)) yield return n;
        }
    }

    [Fact]
    public async Task Resolves_KeyringRef_AndSecretRef_ViaDefaultService()
    {
        var b = new FakeBackend();
        b.Items[("svc", "item")] = "v1";
        b.Items[("dbdata", "dbdata:sync:connection:erp")] = "v2";
        var p = new KeyringSecretProvider(b);
        using (var v = await p.TryResolveAsync(SecretRef.Parse("keyring:svc/item"))) Assert.Equal("v1", v!.RevealString());
        using (var v = await p.TryResolveAsync(SecretRef.Parse("secret:dbdata:sync:connection:erp"))) Assert.Equal("v2", v!.RevealString());
        Assert.Null(await p.TryResolveAsync(SecretRef.Parse("keyring:svc/none")));
    }

    [Fact]
    public async Task Store_SetListDelete_UsesNamesOnly()
    {
        var p = new KeyringSecretProvider(new FakeBackend());
        var n = SecretName.Parse("dbdata:proxy:datasource:erp");
        await p.SetAsync(n, SecretValue.From("pw"));
        var names = new List<SecretName>();
        await foreach (var x in p.ListAsync("dbdata:proxy")) names.Add(x);
        Assert.Equal([n], names);
        await p.DeleteAsync(n);
        Assert.Null(await p.TryResolveAsync(n.ToRef()));
    }

    [Fact]
    public async Task WithoutBackend_IsInert_ButWritesThrowClearly()
    {
        var p = new KeyringSecretProvider();
        Assert.False(p.IsAvailable);
        Assert.False(p.CanHandle(SecretRef.Parse("keyring:a/b")));
        await Assert.ThrowsAsync<PlatformNotSupportedException>(() => p.SetAsync(SecretName.Parse("dbdata:a:b:c"), SecretValue.From("x")));
    }
}
