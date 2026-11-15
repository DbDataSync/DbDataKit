using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Secrets;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.State;

/// <summary>
/// The key a group's shared sessions protect what they carry with (plan 20 O02): a pass-through session's database user name and password are written to the state encrypted with it (AES-256-GCM), so any instance of the
/// group can serve the session and the database never holds a password in the clear. The key is the secret <see cref="Secret"/>, made in the instance's store on its first start; every other instance of the group needs a copy
/// (<c>dbdatagateway secret set dbdatakit:gateway:state:session-key --stdin</c>). The state holds a value encrypted with it, so an instance given another key refuses to start, naming the secret, instead of failing sessions one by one.
/// The key is the suite's shared group key (<see cref="GroupKey"/>, plan 28c E20d), which the Runner's group uses too.
/// </summary>
public sealed class GroupSessionKey
{
    public const string Secret = "dbdatakit:gateway:state:session-key";

    private const string CheckName = "session-key";
    private const string CheckText = "dbdatagateway group session key";

    private readonly GroupKey _key;

    public GroupSessionKey(StateDatabase state, IOptionsMonitor<GatewayOptions> options) =>
        _key = new GroupKey(state, () => SecretsRuntime.OpenStore(options.CurrentValue), Spec);

    /// <summary>The Gateway's group key: made by the first instance, copied to every other by hand.</summary>
    public static GroupKeySpec Spec { get; } = new(Secret, CheckName, CheckText, MakeWhenMissing: true,
        WrongKey: () => $"The sessions in the state database are protected with another key than the one in {Secret}. Every instance that shares the state needs the same key: copy it from an instance of the group " +
            $"(dbdatagateway secret export there) into this one's store (dbdatagateway secret set {Secret} --stdin).",
        NotAKey: reason => $"The secret {Secret} is not a key: {reason} Remove it to have one made, if this group has no shared sessions yet.");

    /// <summary>The shared key this wraps (the group's secret store uses it directly).</summary>
    public GroupKey Key => _key;

    public byte[] Protect(byte[] value) => Translate(() => _key.Protect(value));

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> for a value this key did not protect (or that was changed).</summary>
    public byte[] Unprotect(byte[] value) => Translate(() => _key.Unprotect(value));

    /// <summary>Loads the key now (or makes it), checking it against the group's: a start calls it, so a wrong key stops the instance there.</summary>
    public void Ensure() => Translate(() => { _key.Ensure(); return 0; });

    internal static T Translate<T>(Func<T> call)
    {
        try { return call(); }
        catch (GroupKeyException ex) { throw new SecretConfigurationException(ex.Secret, ex.Message); }
    }
}
