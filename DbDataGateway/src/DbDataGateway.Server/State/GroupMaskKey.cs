using System.Security.Cryptography;

using DbDataKit.State;

namespace DbDataGateway.Server.State;

/// <summary>
/// The key of the keyed hash that masks a column with <c>mask: hash</c> (plan 22a GP13): one key for the group, so every instance of it gives the same mask for the same value, and a restart or another instance never changes a mask. It is made once, on first use
/// (32 random bytes), and kept in the group's state in the row <see cref="SecretName"/> of the table of the clients' secrets, encrypted with the group's key (<see cref="GroupSessionKey"/>), so the database never holds it in the clear and no secret reference reaches it
/// (the group's secret store serves only a client's own names). <b>Rotation is an explicit act</b> (<see cref="Rotate"/>, an administrator's call that is audited): it deliberately changes every hash, so a value masked before no longer matches the same value masked after.
/// An instance reads the key again every <see cref="Refresh"/>, so a rotation made through another instance reaches it within that time.
/// </summary>
public sealed class GroupMaskKey(StateDatabase state, GroupSessionKey key, TimeProvider? time = null)
{
    /// <summary>The name of the row; a name of the suite's shape, in no secret store.</summary>
    public const string SecretName = "dbdatakit:gateway:state:mask-key";

    /// <summary>How long an instance keeps the key it read before it reads the group's again.</summary>
    public static readonly TimeSpan Refresh = TimeSpan.FromSeconds(30);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _lock = new();
    private byte[]? _held;
    private DateTimeOffset _readAt;

    /// <summary>The group's key; made if the group has none. Throws when the state cannot be read: a hash mask is never made with another key.</summary>
    public byte[] Current()
    {
        lock (_lock)
        {
            var now = _time.GetUtcNow();
            if (_held is not null && now - _readAt < Refresh) return _held;
            _held = ReadOrMake();
            _readAt = now;
            return _held;
        }
    }

    /// <summary>Whether the group has a key yet (a key is made on first use, not at start).</summary>
    public bool Exists() => Read() is not null;

    /// <summary>Replaces the group's key with a new one; every hash mask changes. The instance that rotates uses the new key at once, the others within <see cref="Refresh"/>.</summary>
    public void Rotate()
    {
        var fresh = RandomNumberGenerator.GetBytes(32);
        var sealedKey = Convert.ToBase64String(key.Protect(fresh));
        state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var delete = state.Command("trust", connection, transaction, "DELETE FROM {table:group_secret} WHERE name = $name;"))
                delete.Bind(state, "name", SecretName).ExecuteNonQuery();
            using (var insert = state.Command("trust", connection, transaction, "INSERT INTO {table:group_secret} (name, value) VALUES ($name, $value);"))
                insert.Bind(state, "name", SecretName).Bind(state, "value", sealedKey).ExecuteNonQuery();
            transaction.Commit();
        });
        lock (_lock)
        {
            _held = fresh;
            _readAt = _time.GetUtcNow();
        }
    }

    private byte[]? Read()
    {
        var stored = state.Retry(() =>
        {
            using var connection = state.OpenConnection();
            using var read = state.Command("trust", connection, "SELECT value FROM {table:group_secret} WHERE name = $name;");
            read.Bind(state, "name", SecretName);
            return read.ExecuteScalar() as string;
        });
        return stored is null ? null : key.Unprotect(Convert.FromBase64String(stored));
    }

    private byte[] ReadOrMake()
    {
        if (Read() is { } found) return found;
        var fresh = RandomNumberGenerator.GetBytes(32);
        var sealedKey = Convert.ToBase64String(key.Protect(fresh));
        try
        {
            state.Retry(() =>
            {
                using var connection = state.OpenConnection();
                using var insert = state.Command("trust", connection, "INSERT INTO {table:group_secret} (name, value) VALUES ($name, $value);");
                insert.Bind(state, "name", SecretName).Bind(state, "value", sealedKey).ExecuteNonQuery();
            });
            return fresh;
        }
        catch (System.Data.Common.DbException)
        {
            // another instance made it first: its key is the group's
            return Read() ?? throw new InvalidOperationException("The group's mask key could not be made or read.");
        }
    }
}
