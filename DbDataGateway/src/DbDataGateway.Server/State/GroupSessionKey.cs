using System.Security.Cryptography;
using System.Text;
using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Secrets;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.State;

/// <summary>
/// The key a group's shared sessions protect what they carry with (plan 20 O02): a pass-through session's database user name and password are written to the state encrypted with it (AES-256-GCM), so any instance of the
/// group can serve the session and the database never holds a password in the clear. The key is the secret <see cref="Secret"/>, made in the instance's store on its first start; every other instance of the group needs a copy
/// (<c>dbdatagateway secret set dbdata:gateway:state:session-key --stdin</c>). The state holds a value encrypted with it, so an instance given another key refuses to start, naming the secret, instead of failing sessions one by one.
/// </summary>
public sealed class GroupSessionKey(StateDatabase state, IOptionsMonitor<GatewayOptions> options)
{
    public const string Secret = "dbdata:gateway:state:session-key";

    private const string CheckName = "session-key";
    private static readonly byte[] CheckPlain = Encoding.UTF8.GetBytes("dbdatagateway group session key");

    private readonly Lazy<KeySigningKeyProtector> _protector = new(() => Load(state, options.CurrentValue), LazyThreadSafetyMode.ExecutionAndPublication);

    public byte[] Protect(byte[] value) => _protector.Value.Protect(value);

    /// <summary>Throws <see cref="CryptographicException"/> for a value this key did not protect (or that was changed).</summary>
    public byte[] Unprotect(byte[] value) => _protector.Value.Unprotect(value);

    /// <summary>Loads the key now (or makes it), checking it against the group's: a start calls it, so a wrong key stops the instance there.</summary>
    public void Ensure() => _ = _protector.Value;

    private static KeySigningKeyProtector Load(StateDatabase state, GatewayOptions current)
    {
        var store = SecretsRuntime.OpenStore(current);
        var name = SecretName.Parse(Secret);
        string text;
        var made = false;
        using (var held = store.TryResolveAsync(SecretRef.Parse("secret:" + Secret)).AsTask().GetAwaiter().GetResult())
        {
            if (held is null)
            {
                text = KeySigningKeyProtector.NewKey();
                using var value = SecretValue.From(text);
                store.SetAsync(name, value).GetAwaiter().GetResult();
                made = true;
            }
            else text = held.RevealString();
        }

        KeySigningKeyProtector protector;
        try { protector = KeySigningKeyProtector.FromBase64(text); }
        catch (FormatException ex) { throw new SecretConfigurationException(Secret, $"The secret {Secret} is not a key: {ex.Message} Remove it to have one made, if this group has no shared sessions yet."); }

        // the group's check value: written by the first instance, read by every other
        var check = Convert.ToBase64String(protector.Protect(CheckPlain));
        var existing = state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var read = state.Command("trust", connection, "SELECT check_value FROM {table:group_key} WHERE name = $name;");
            read.Bind(state, "name", CheckName);
            if (read.ExecuteScalar() is string found) return found;
            try
            {
                using var insert = state.Command("trust", connection, "INSERT INTO {table:group_key} (name, check_value) VALUES ($name, $check);");
                insert.Bind(state, "name", CheckName);
                insert.Bind(state, "check", check);
                insert.ExecuteNonQuery();
                return check;
            }
            catch (System.Data.Common.DbException)
            {
                // another instance wrote it first: its is the group's
                using var again = state.Command("trust", connection, "SELECT check_value FROM {table:group_key} WHERE name = $name;");
                again.Bind(state, "name", CheckName);
                return (string)again.ExecuteScalar()!;
            }
        });
        try
        {
            if (!protector.Unprotect(Convert.FromBase64String(existing)).AsSpan().SequenceEqual(CheckPlain)) throw new CryptographicException("The check value differs.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            if (made) store.DeleteAsync(name).GetAwaiter().GetResult();
            throw new SecretConfigurationException(Secret,
                $"The sessions in the state database are protected with another key than the one in {Secret}. Every instance that shares the state needs the same key: copy it from an instance of the group " +
                $"(dbdatagateway secret export there) into this one's store (dbdatagateway secret set {Secret} --stdin).");
        }
        return protector;
    }
}
