using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.State;

/// <summary>The table behind <see cref="OneTimeValues"/>. A host that signs people in appends it to its schema list as the next entry (plan 20 O02b).</summary>
public static class OneTimeSchema
{
    public const string Values =
        """
        -- What a sign-in or a call holds between two requests and may use once (plan 20 O02b): a passkey ceremony's challenge, an upstream sign-in's verifier and nonce, a management token's id that has been used.
        -- In the state rather than one process's memory so that every instance sharing the state can finish what another started, and refuses what another has already accepted. KeyHash is the SHA-256 of the
        -- purpose and the key the browser or caller holds, so the table names nothing a caller could present.
        CREATE TABLE OneTimeValues (
            KeyHash      {{key}} PRIMARY KEY,
            Purpose      {{key}} NOT NULL,
            Value        {{text}} NULL,
            ExpiresAtUtc {{key}} NOT NULL
        );
        CREATE INDEX IX_OneTimeValues_Expires ON OneTimeValues(ExpiresAtUtc);
        """;
}

/// <summary>
/// Values that are used once, in the state (<see cref="OneTimeSchema.Values"/>), so any instance sharing the state can use what another put there, and only one of them can. A purpose keeps one use's keys apart from another's.
/// </summary>
public sealed class OneTimeValues(StateDatabase database, TimeProvider? clock = null) : IOneTimeValues
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private long _nextSweepTicks;

    /// <summary>Keeps <paramref name="value"/> under <paramref name="key"/> until <paramref name="expires"/>, for one <see cref="Take"/>.</summary>
    public void Hold(string purpose, string key, string value, DateTimeOffset expires)
    {
        Sweep();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "INSERT INTO OneTimeValues (KeyHash, Purpose, Value, ExpiresAtUtc) VALUES ($hash, $purpose, $value, $expires);");
            cmd.Bind(database, "hash", Hash(purpose, key));
            cmd.Bind(database, "purpose", purpose);
            cmd.Bind(database, "value", value);
            cmd.Bind(database, "expires", Stamp(expires));
            cmd.ExecuteNonQuery();
        });
    }

    /// <summary>The value held under <paramref name="key"/>, removing it: null when there is none, it has expired, or another caller (on this instance or another) took it first.</summary>
    public string? Take(string purpose, string key)
    {
        var hash = Hash(purpose, key);
        var now = _clock.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            string? value;
            DateTimeOffset expires;
            using (var read = database.Command(connection, "SELECT Value, ExpiresAtUtc FROM OneTimeValues WHERE KeyHash = $hash;"))
            {
                read.Bind(database, "hash", hash);
                using var reader = read.ExecuteReader();
                if (!reader.Read()) return null;
                value = reader.IsDBNull(0) ? null : reader.GetString(0);
                expires = Time(reader.GetString(1));
            }
            using var delete = database.Command(connection, "DELETE FROM OneTimeValues WHERE KeyHash = $hash;");
            delete.Bind(database, "hash", hash);
            // the delete is what decides: of two callers that both read the row, one removes it
            if (delete.ExecuteNonQuery() != 1) return null;
            return expires > now ? value : null;
        });
    }

    /// <summary>Records that <paramref name="key"/> has been used, until <paramref name="forgetAfter"/>: true the first time, false when it was already recorded (by this instance or another).</summary>
    public bool TryClaim(string purpose, string key, DateTimeOffset forgetAfter)
    {
        Sweep();
        var hash = Hash(purpose, key);
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            try
            {
                using var insert = database.Command(connection, "INSERT INTO OneTimeValues (KeyHash, Purpose, Value, ExpiresAtUtc) VALUES ($hash, $purpose, NULL, $expires);");
                insert.Bind(database, "hash", hash);
                insert.Bind(database, "purpose", purpose);
                insert.Bind(database, "expires", Stamp(forgetAfter));
                insert.ExecuteNonQuery();
                return true;
            }
            catch (DbException) when (Exists(connection, hash))
            {
                // the key is the primary key: another use got there first
                return false;
            }
        });
    }

    private bool Exists(DbConnection connection, string hash)
    {
        using var cmd = database.Command(connection, "SELECT COUNT(*) FROM OneTimeValues WHERE KeyHash = $hash;");
        cmd.Bind(database, "hash", hash);
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    /// <summary>Drops what has expired, at most once a minute per instance (every instance sweeps; deleting what another already deleted is nothing).</summary>
    private void Sweep()
    {
        var now = _clock.GetUtcNow();
        var next = Interlocked.Read(ref _nextSweepTicks);
        if (now.UtcTicks < next || Interlocked.CompareExchange(ref _nextSweepTicks, (now + SweepEvery).UtcTicks, next) != next) return;
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "DELETE FROM OneTimeValues WHERE ExpiresAtUtc <= $now;");
            cmd.Bind(database, "now", Stamp(now));
            cmd.ExecuteNonQuery();
        });
    }

    private static string Hash(string purpose, string key) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(purpose + "\n" + key)));

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Time(string text) => DateTimeOffset.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
