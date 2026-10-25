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

    /// <summary>The key a rotation is moving to, kept beside the current one until every server has it.</summary>
    public static readonly SecretName NextName = new("kit", "console", "signing-key-next");

    public async Task<ConsoleIdentity> GetOrCreateAsync(CancellationToken ct = default)
    {
        if (await ReadAsync(ct) is { } existing) return existing;
        var key = ConsoleSigningKey.Generate();
        var identity = new ConsoleIdentity("console-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant(), key);
        await WriteAsync(identity, ct);
        // two processes can race to make the first identity: whichever the store kept is the one both use
        return await ReadAsync(ct) ?? identity;
    }

    /// <summary>Replaces the key and keeps the id.</summary>
    public Task WriteAsync(ConsoleIdentity identity, CancellationToken ct = default) => WriteAsync(Name, identity, ct);

    private Task WriteAsync(SecretName name, ConsoleIdentity identity, CancellationToken ct)
    {
        using var value = SecretValue.From(identity.Id + ":" + Base64Url.Encode(identity.Key.ExportSeed()));
        return store.SetAsync(name, value, ct);
    }

    /// <summary>The key a rotation is moving to, if one is under way.</summary>
    public async Task<ConsoleIdentity?> NextAsync(CancellationToken ct = default) => await ReadAsync(NextName, ct);

    /// <summary>Starts a rotation (or continues the one under way): a new key with the same id, kept next to the current one. The current key keeps signing until <see cref="PromoteNextAsync"/>.</summary>
    public async Task<ConsoleIdentity> BeginRotationAsync(CancellationToken ct = default)
    {
        var current = await GetOrCreateAsync(ct);
        if (await NextAsync(ct) is { } under) return under;
        var next = new ConsoleIdentity(current.Id, ConsoleSigningKey.Generate());
        await WriteAsync(NextName, next, ct);
        return await NextAsync(ct) ?? next;
    }

    /// <summary>The identity whose key has this fingerprint: the current one or the one a rotation is moving to; null for neither.</summary>
    public async Task<ConsoleIdentity?> ForKeyAsync(string fingerprint, CancellationToken ct = default)
    {
        var current = await ReadAsync(Name, ct);
        if (current is not null && current.Key.Fingerprint == fingerprint) return current;
        var next = await ReadAsync(NextName, ct);
        return next is not null && next.Key.Fingerprint == fingerprint ? next : null;
    }

    /// <summary>Makes the new key the current one and forgets the old: only after every server has the new key.</summary>
    public async Task PromoteNextAsync(CancellationToken ct = default)
    {
        var next = await NextAsync(ct) ?? throw new InvalidOperationException("No key rotation is under way.");
        await WriteAsync(Name, next, ct);
        await store.DeleteAsync(NextName, ct);
    }

    private Task<ConsoleIdentity?> ReadAsync(CancellationToken ct) => ReadAsync(Name, ct);

    private async Task<ConsoleIdentity?> ReadAsync(SecretName name, CancellationToken ct)
    {
        using var secret = await provider.TryResolveAsync(name.ToRef(), ct);
        if (secret is null || secret.IsEmpty) return null;
        var text = secret.RevealString();
        var split = text.IndexOf(':');
        var seed = split > 0 ? Base64Url.Decode(text[(split + 1)..]) : null;
        if (seed is not { Length: ConsoleSigningKey.SeedLength })
            throw new InvalidOperationException($"The console's signing key ({name}) in the secret store is damaged. Remove it with 'secret remove' to make a new one; servers will then need pairing again.");
        return new ConsoleIdentity(text[..split], ConsoleSigningKey.FromSeed(seed));
    }
}
