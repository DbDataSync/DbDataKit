using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.State.Engines.Snapshots;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Secrets;

namespace DbDataGateway.Server.State;

/// <summary>
/// The encrypted columns of the gateway's state, with the keys of its secret store (plan 29b C04), for <c>state move</c> and the move the console starts: the identity home's signing keys (protected with the secret
/// <see cref="GatewayIdentityModule.SigningKeySecret"/>) and the secrets of a group's clients (protected with <see cref="GroupSessionKey.Secret"/>). A key that is not in the store decrypts nothing, so the move says so
/// before it changes the settings.
/// </summary>
public static class GatewayStateSecrets
{
    public static IReadOnlyList<StateSecretSample> For(Func<GatewayOptions> options) =>
    [
        new StateSecretSample(IdentityModule.Name, "signing_key", "private_key", value => Open(options, GatewayIdentityModule.SigningKeySecret, value)),
        new StateSecretSample(TrustModule.Name, "group_secret", "value", value => Open(options, GroupSessionKey.Secret, value)),
    ];

    /// <summary>True when the key the secret names is in the store and decrypts the stored value (base64 of <see cref="KeySigningKeyProtector"/>'s form).</summary>
    private static bool Open(Func<GatewayOptions> options, string secret, string stored)
    {
        using var held = SecretsRuntime.OpenStore(options()).TryResolveAsync(SecretRef.Parse("secret:" + secret)).AsTask().GetAwaiter().GetResult();
        if (held is null) return false;
        var plain = KeySigningKeyProtector.FromBase64(held.RevealString()).Unprotect(Convert.FromBase64String(stored));
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
        return true;
    }
}
