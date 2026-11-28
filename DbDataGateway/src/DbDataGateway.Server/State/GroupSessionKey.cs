using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Secrets;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.State;

/// <summary>
/// The key a group's shared sessions protect what they carry with (plan 20 O02): a pass-through session's database user name and password are written to the state encrypted with it (AES-256-GCM), so any instance of the
/// group can serve the session and the database never holds a password in the clear. The key is the secret <see cref="Secret"/> in the instance's own store. The console that manages the group makes it and hands it to each
/// instance in the answer to its join; an instance that starts with a join request waiting and holds no key makes none and serves nothing that needs it until the join step has kept the console's
/// (<see cref="AwaitingJoin"/>). An instance that never joins a console makes its own at its first start. The state holds a value encrypted with the key, so an instance given another key refuses to start, naming the secret,
/// instead of failing sessions one by one. The key is the suite's shared group key (<see cref="GroupKey"/>, plan 28c E20d), which the Runner's group uses too.
/// Design: docs/install.md#pairing
/// </summary>
public sealed class GroupSessionKey
{
    public const string Secret = "dbdatakit:gateway:state:session-key";

    private const string CheckName = "session-key";
    private const string CheckText = "dbdatagateway group session key";

    private readonly GroupKey _key;

    public GroupSessionKey(StateDatabase state, IOptionsMonitor<GatewayOptions> options)
    {
        AwaitingJoin = File.Exists(ServerJoin.RequestPath(Hosting.GatewayHost.ResolveDataDirectory(options.CurrentValue)));
        _key = new GroupKey(state, () => SecretsRuntime.OpenStore(options.CurrentValue), AwaitingJoin ? JoinSpec : Spec);
    }

    /// <summary>The Gateway's group key on an instance that joins no console at this start: made by the first instance when none is held.</summary>
    public static GroupKeySpec Spec { get; } = new(Secret, CheckName, CheckText, MakeWhenMissing: true,
        WrongKey: () => $"The sessions in the state database are protected with another key than the one in {Secret}. Every instance that shares the state needs the same key: join this instance to the group " +
            $"from the console that manages it (dbdatakit setup invite gateway --group <group>, then the pair command it prints), which hands the group's key over.",
        NotAKey: reason => $"The secret {Secret} is not a key: {reason} Remove it to have one made, if this group has no shared sessions yet.");

    /// <summary>The key on an instance that joins a console at this start: never made here, since the console hands the group's key over in the join's answer.</summary>
    public static GroupKeySpec JoinSpec { get; } = Spec with
    {
        MakeWhenMissing = false,
        Missing = () => $"This instance does not hold its group's key {Secret} yet: the console hands it over when the instance joins, at the join step of this start.",
    };

    /// <summary>Whether a join request waited when this instance started: the key is then not made here, and what needs it waits for the join.</summary>
    public bool AwaitingJoin { get; }

    /// <summary>Whether this instance can use the key now: it holds the group's key, or it may make one.</summary>
    public bool Usable => !AwaitingJoin || _key.Held;

    /// <summary>The shared key this wraps (the group's secret store uses it directly).</summary>
    public GroupKey Key => _key;

    public byte[] Protect(byte[] value) => Translate(() => _key.Protect(value));

    /// <summary>Throws <see cref="System.Security.Cryptography.CryptographicException"/> for a value this key did not protect (or that was changed).</summary>
    public byte[] Unprotect(byte[] value) => Translate(() => _key.Unprotect(value));

    /// <summary>
    /// Loads the key now (or makes it), checking it against the group's: a start calls it, so a wrong key stops the instance there. An instance awaiting its join and holding no key passes: the join step of the
    /// same start keeps the console's.
    /// </summary>
    public void Ensure()
    {
        try { _key.Ensure(); }
        catch (GroupKeyException ex) when (AwaitingJoin && ex.Problem == GroupKeyProblem.Missing)
        {
            // expected: the join step of this start keeps the key the console hands over
        }
        catch (GroupKeyException ex) { throw new SecretConfigurationException(ex.Secret, ex.Message, ex); }
    }

    /// <summary>
    /// Keeps the group's key the console handed over in the join's answer (checked against the group's check value, written from it when the group has none); a key that is not the group's is refused naming the
    /// secret, and nothing is kept.
    /// </summary>
    public void Join(string key)
    {
        try { _key.Join(key); }
        catch (GroupKeyException ex) { throw new SecretConfigurationException(ex.Secret, ex.Message, ex); }
    }

    internal static T Translate<T>(Func<T> call)
    {
        try { return call(); }
        catch (GroupKeyException ex) { throw new SecretConfigurationException(ex.Secret, ex.Message); }
    }
}
