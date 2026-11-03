using System.Text;
using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Apps;

/// <summary>What the host enforces on app storage (plan 26 section 2a): a value up to 256 KB, a key up to 128 characters, and a total for the instance (the manifest's <c>quota_mb</c>, never above the administrator's cap).</summary>
public sealed record AppStorageLimits(int MaximumValueBytes = 256 * 1024, int MaximumKeyLength = 128, long QuotaBytes = 50L * 1024 * 1024);

public sealed record StoredValue(string Key, string Json, int Version);

/// <summary>Why a storage call was refused. <see cref="Kind"/> is stable (<c>key</c>, <c>value</c>, <c>quota</c>, <c>conflict</c>), the message says what to change.</summary>
public sealed class AppStorageException(string kind, string message) : Exception(message)
{
    public string Kind { get; } = kind;
}

/// <summary>
/// Key-value storage for apps in the group's state: <c>user</c> (one person in one instance) and <c>instance</c> (everyone in one). The rows are keyed by the instance, so instances never share, and by the owner, so one person
/// cannot name another's. Writes are last-writer-wins unless the caller passes the version it read.
/// </summary>
public sealed class AppStorage(StateDatabase database, TimeProvider? clock = null)
{
    public const string User = "user";
    public const string Instance = "instance";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public static bool IsScope(string scope) => scope is User or Instance;

    private static string OwnerOf(string scope, string person) => scope == User ? person : "";

    public StoredValue? Get(string instance, string scope, string person, string key) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var read = database.Command(connection, "SELECT Value, Version FROM AppStorage WHERE Instance = $i AND Scope = $s AND Owner = $o AND StoredKey = $k;");
            read.Bind(database, "i", instance).Bind(database, "s", scope).Bind(database, "o", OwnerOf(scope, person)).Bind(database, "k", key);
            using var reader = read.ExecuteReader();
            return reader.Read() ? new StoredValue(key, reader.GetString(0), reader.GetInt32(1)) : null;
        });

    public IReadOnlyList<StoredValue> List(string instance, string scope, string person, string prefix = "") =>
        database.Retry<IReadOnlyList<StoredValue>>(() =>
        {
            using var connection = database.OpenConnection();
            using var read = database.Command(connection, "SELECT StoredKey, Value, Version FROM AppStorage WHERE Instance = $i AND Scope = $s AND Owner = $o ORDER BY StoredKey;");
            read.Bind(database, "i", instance).Bind(database, "s", scope).Bind(database, "o", OwnerOf(scope, person));
            using var reader = read.ExecuteReader();
            var found = new List<StoredValue>();
            while (reader.Read()) if (reader.GetString(0).StartsWith(prefix, StringComparison.Ordinal)) found.Add(new StoredValue(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
            return found;
        });

    /// <summary>Stores a JSON value and returns its new version. <paramref name="ifVersion"/> of 0 means "only if the key is new".</summary>
    public int Set(string instance, string scope, string person, string key, string json, AppStorageLimits limits, int? ifVersion = null)
    {
        if (key.Length == 0 || key.Length > limits.MaximumKeyLength) throw new AppStorageException("key", $"A key is 1 to {limits.MaximumKeyLength} characters.");
        if (key.Any(char.IsControl)) throw new AppStorageException("key", "A key holds no control characters.");
        var size = Encoding.UTF8.GetByteCount(json);
        if (size > limits.MaximumValueBytes) throw new AppStorageException("value", $"A value is at most {limits.MaximumValueBytes / 1024} KB; this one is {size / 1024} KB.");
        try { using var _ = JsonDocument.Parse(json); }
        catch (JsonException) { throw new AppStorageException("value", "A value is JSON."); }
        var now = _clock.GetUtcNow().ToString("O");
        var owner = OwnerOf(scope, person);

        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var read = database.Command(connection, transaction, "SELECT Version, Size FROM AppStorage WHERE Instance = $i AND Scope = $s AND Owner = $o AND StoredKey = $k;");
            read.Bind(database, "i", instance).Bind(database, "s", scope).Bind(database, "o", owner).Bind(database, "k", key);
            int? current = null;
            long oldSize = 0;
            using (var reader = read.ExecuteReader())
                if (reader.Read()) { current = reader.GetInt32(0); oldSize = reader.GetInt32(1); }
            if (ifVersion is { } expected && expected != (current ?? 0)) throw new AppStorageException("conflict", $"The key '{key}' is at version {current ?? 0}, not {expected}; read it again.");

            using var total = database.Command(connection, transaction, "SELECT SUM(Size) FROM AppStorage WHERE Instance = $i;");
            total.Bind(database, "i", instance);
            var used = total.ExecuteScalar() is { } sum and not DBNull ? Convert.ToInt64(sum) : 0;
            if (used - oldSize + size > limits.QuotaBytes) throw new AppStorageException("quota", $"The instance's storage is limited to {limits.QuotaBytes / (1024 * 1024)} MB.");

            var version = (current ?? 0) + 1;
            using var write = database.Command(connection, transaction, current is null
                ? "INSERT INTO AppStorage (Instance, Scope, Owner, StoredKey, Value, Size, Version, UpdatedAtUtc) VALUES ($i, $s, $o, $k, $v, $z, $n, $t);"
                : "UPDATE AppStorage SET Value = $v, Size = $z, Version = $n, UpdatedAtUtc = $t WHERE Instance = $i AND Scope = $s AND Owner = $o AND StoredKey = $k;");
            write.Bind(database, "i", instance).Bind(database, "s", scope).Bind(database, "o", owner).Bind(database, "k", key).Bind(database, "v", json).Bind(database, "z", size).Bind(database, "n", version).Bind(database, "t", now).ExecuteNonQuery();
            transaction.Commit();
            return version;
        });
    }

    public bool Delete(string instance, string scope, string person, string key) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var delete = database.Command(connection, "DELETE FROM AppStorage WHERE Instance = $i AND Scope = $s AND Owner = $o AND StoredKey = $k;");
            delete.Bind(database, "i", instance).Bind(database, "s", scope).Bind(database, "o", OwnerOf(scope, person)).Bind(database, "k", key);
            return delete.ExecuteNonQuery() > 0;
        });
}
