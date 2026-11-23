using DbDataKit.Secrets;
using DbDataKit.State.Api;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server;

/// <summary>
/// The key the console signs the versions it deploys with: an Ed25519 key of its own, kept in the console's secret store beside the key that signs its
/// tokens (<see cref="ConsoleKeyStore"/>) and apart from it, so a group's trust in what the console publishes and a server's trust in who the console says
/// is calling are two keys that can be revoked or replaced one without the other. The first call makes the key; every later call reads the same one. The
/// seed is encrypted like any other secret and is never in a file, a log, an answer or the audit; only the 32-byte public half leaves the console.
/// Design: docs/install.md#the-consoles-publisher-key
/// </summary>
public sealed class ConsolePublisherKeyStore(ISecretStore store, ISecretProvider provider)
{
    public static readonly SecretName Name = new("server", "identity", "publisher-key");

    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConsoleSigningKey? _known;

    /// <summary>The console's publisher key, made the first time.</summary>
    public async Task<ConsoleSigningKey> GetOrCreateAsync(CancellationToken ct = default)
    {
        if (_known is { } known) return known;
        await _gate.WaitAsync(ct);
        try
        {
            if (_known is { } again) return again;
            if (await ReadAsync(ct) is { } existing) return _known = existing;
            var made = ConsoleSigningKey.Generate();
            // awaited inside the using: the value is disposed when this block ends, and a store that yields before it seals the value would find it gone
            using (var value = SecretValue.From(Url.Encode(made.ExportSeed()))) await store.SetAsync(Name, value, ct);
            // two processes can race to make the key: whichever the store kept is the one both use
            return _known = await ReadAsync(ct) ?? made;
        }
        finally { _gate.Release(); }
    }

    private async Task<ConsoleSigningKey?> ReadAsync(CancellationToken ct)
    {
        using var secret = await provider.TryResolveAsync(Name.ToRef(), ct);
        if (secret is null || secret.IsEmpty) return null;
        var seed = Url.Decode(secret.RevealString());
        if (seed is not { Length: ConsoleSigningKey.SeedLength })
            throw new InvalidOperationException($"The console's publisher key ({Name}) in the secret store is damaged. Remove it with 'secret remove' to make a new one; each group then trusts the new key at the console's next pass over its servers, and an administrator revokes the old one on the group's Publishers page.");
        return ConsoleSigningKey.FromSeed(seed);
    }
}
