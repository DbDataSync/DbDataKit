using System.Security.Cryptography;
using DbDataKit.Secrets;

namespace DbDataKit.Web.Trust;

/// <summary>Who a console is to the servers it manages: its id (the issuer of its tokens) and the key that signs them.</summary>
public sealed record ConsoleIdentity(string Id, ConsoleSigningKey Key)
{
    public ConsoleTokenIssuer Issuer(TimeProvider? clock = null) => new(Id, Key, clock);
}

/// <summary>
/// Keeps a console's identity in the secret store (plan 13, W12; <c>DbDataKit.Secrets</c>), where the signing seed is encrypted like any other secret and never appears in a config file or a log. The first call
/// makes the identity; every later call, in any process that shares the store, reads the same one.
/// </summary>
public sealed class ConsoleKeyStore(ISecretStore store, ISecretProvider provider)
{
    public static readonly SecretName Name = new("kit", "console", "signing-key");

    public async Task<ConsoleIdentity> GetOrCreateAsync(CancellationToken ct = default)
    {
        if (await ReadAsync(ct) is { } existing) return existing;
        var key = ConsoleSigningKey.Generate();
        var identity = new ConsoleIdentity("console-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), key);
        await WriteAsync(identity, ct);
        // two processes can race to make the first identity: whichever the store kept is the one both use
        return await ReadAsync(ct) ?? identity;
    }

    /// <summary>Replaces the key and keeps the id (key rotation, T07).</summary>
    public Task WriteAsync(ConsoleIdentity identity, CancellationToken ct = default)
    {
        using var value = SecretValue.From(identity.Id + ":" + Base64Url.Encode(identity.Key.ExportSeed()));
        return store.SetAsync(Name, value, ct);
    }

    private async Task<ConsoleIdentity?> ReadAsync(CancellationToken ct)
    {
        using var secret = await provider.TryResolveAsync(Name.ToRef(), ct);
        if (secret is null || secret.IsEmpty) return null;
        var text = secret.RevealString();
        var split = text.IndexOf(':');
        var seed = split > 0 ? Base64Url.Decode(text[(split + 1)..]) : null;
        if (seed is not { Length: ConsoleSigningKey.SeedLength })
            throw new InvalidOperationException($"The console's signing key ({Name}) in the secret store is damaged. Remove it with 'secret remove' to make a new one; servers will then need pairing again.");
        return new ConsoleIdentity(text[..split], ConsoleSigningKey.FromSeed(seed));
    }
}
