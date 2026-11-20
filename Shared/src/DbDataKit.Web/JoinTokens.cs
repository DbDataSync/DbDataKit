using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DbDataKit.Web.Trust;

/// <summary>What a join token lets a server become: which product it is, the most the console may grant itself there, and the group it joins (null for none).</summary>
/// <param name="Product">The joining product in lower case (<c>gateway</c>, <c>runner</c>, <c>workspace</c>).</param>
/// <param name="Ceiling">The role ceiling the console is paired with on the joined server.</param>
/// <param name="Group">The group the server joins; null when the token names none.</param>
public sealed partial record JoinScope(string Product, ServerRole Ceiling, string? Group = null)
{
    /// <summary>Null when the scope can be minted; otherwise what is wrong with it.</summary>
    public string? Problem() =>
        !ProductPattern().IsMatch(Product ?? "") ? "the product is a lower-case name (gateway, runner, workspace)"
        : Ceiling == ServerRole.None ? "the role ceiling names no role"
        : Group is { } g && !GroupPattern().IsMatch(g) ? "the group is letters, digits and . _ -, up to 64"
        : null;

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex ProductPattern();

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex GroupPattern();
}

/// <summary>Where a join token is in its life.</summary>
public enum JoinTokenState
{
    Waiting,
    Spent,
    Expired,

    /// <summary>Cancelled by a person, or used up by <see cref="JoinTokens.MaxTries"/> refused tries.</summary>
    Cancelled,
}

/// <summary>
/// A join token as the console keeps it: never the token, only its <paramref name="Verifier"/>, the public half of a key made from the token, which checks
/// a join's proof and cannot make one. A copy of the console's state therefore lets nobody join.
/// </summary>
/// <param name="Id">The token's id, which the audit and the Servers page name; it says nothing about the token's value.</param>
/// <param name="Verifier">The base64url Ed25519 public key made from the token (<see cref="JoinTokens.KeyFor"/>).</param>
/// <param name="MadeBy">The user id of the person who made the token.</param>
/// <param name="MadeByName">That person's name, when known.</param>
/// <param name="Tries">Refused tries with this token so far; at <see cref="JoinTokens.MaxTries"/> it is cancelled.</param>
/// <param name="Product">The joining product the token is for.</param>
/// <param name="Ceiling">The console's role ceiling on the joined server.</param>
/// <param name="Group">The group the server joins; null for none.</param>
/// <param name="MadeAt">When the token was made.</param>
/// <param name="ExpiresAt">When it stops working.</param>
/// <param name="SpentAt">When a join spent it; null while it waits.</param>
/// <param name="CancelledAt">When it was cancelled; null when it was not.</param>
public sealed record JoinTokenRecord(
    string Id, string Verifier, string Product, ServerRole Ceiling, string? Group, string MadeBy, string? MadeByName,
    DateTimeOffset MadeAt, DateTimeOffset ExpiresAt, int Tries = 0, DateTimeOffset? SpentAt = null, DateTimeOffset? CancelledAt = null)
{
    public JoinScope Scope => new(Product, Ceiling, Group);

    public JoinTokenState StateAt(DateTimeOffset now) =>
        SpentAt is not null ? JoinTokenState.Spent
        : CancelledAt is not null ? JoinTokenState.Cancelled
        : ExpiresAt <= now ? JoinTokenState.Expired
        : JoinTokenState.Waiting;
}

/// <summary>A token just made: <see cref="Token"/> is shown once, in the command the person runs, and <see cref="Record"/> is what the console keeps.</summary>
public sealed record MintedJoinToken(string Token, JoinTokenRecord Record);

/// <summary>
/// The single-use join token: 128 random bits written as 26 lower-case base32 characters, scoped to a product, a role ceiling and a group, alive for
/// <see cref="DefaultLifetime"/> unless the person asks for up to <see cref="MaxLifetime"/>. The token never leaves the machine it is given to: the join
/// carries a signature made with a key derived from it (<see cref="KeyFor"/>), and the console checks it with the public half it kept.
/// Design: docs/install.md#pairing
/// </summary>
public static class JoinTokens
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(24);

    /// <summary>Refused tries with one token before it is cancelled.</summary>
    public const int MaxTries = 5;

    public const int TokenBytes = 16;
    private const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";
    private static readonly byte[] KeyContext = Encoding.ASCII.GetBytes("dbdata-join-v1-key");

    /// <summary>Makes a token for <paramref name="scope"/>, made by <paramref name="madeBy"/>.</summary>
    /// <exception cref="ArgumentException">The scope or the lifetime is not one a token can have.</exception>
    public static MintedJoinToken Mint(JoinScope scope, string madeBy, string? madeByName = null, TimeSpan? lifetime = null, TimeProvider? clock = null)
    {
        if (scope.Problem() is { } problem) throw new ArgumentException($"The join token cannot be made: {problem}.", nameof(scope));
        if (string.IsNullOrWhiteSpace(madeBy)) throw new ArgumentException("A join token names who made it.", nameof(madeBy));
        var life = lifetime ?? DefaultLifetime;
        if (life <= TimeSpan.Zero || life > MaxLifetime) throw new ArgumentException($"A join token lives from one minute to {MaxLifetime.TotalHours:0} hours.", nameof(lifetime));
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var token = Encode(RandomNumberGenerator.GetBytes(TokenBytes));
        var id = "jt-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
        var record = new JoinTokenRecord(id, VerifierFor(token)!, scope.Product, scope.Ceiling, scope.Group, madeBy, madeByName, now, now + life);
        return new MintedJoinToken(token, record);
    }

    /// <summary>The token as written, or null when <paramref name="text"/> is not one; case, spaces and dashes are ignored.</summary>
    public static string? Normalize(string? text)
    {
        if (text is null) return null;
        var normal = new string(text.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToLowerInvariant).ToArray());
        return normal.Length == 26 && normal.All(Alphabet.Contains) && Decode(normal) is not null ? normal : null;
    }

    /// <summary>The key a join is signed with, made from the token; null when the text is not a token.</summary>
    public static ConsoleSigningKey? KeyFor(string? token)
    {
        if (Normalize(token) is not { } normal || Decode(normal) is not { } raw) return null;
        var seed = HKDF.DeriveKey(HashAlgorithmName.SHA256, raw, ConsoleSigningKey.SeedLength, salt: [], info: KeyContext);
        return ConsoleSigningKey.FromSeed(seed);
    }

    /// <summary>What the console keeps of a token: the base64url public half of <see cref="KeyFor"/>.</summary>
    public static string? VerifierFor(string? token) => KeyFor(token) is { } key ? Base64Url.Encode(key.PublicKey) : null;

    private static string Encode(byte[] bytes)
    {
        var text = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                text.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) text.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return text.ToString();
    }

    private static byte[]? Decode(string text)
    {
        var bytes = new List<byte>(TokenBytes);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
            buffer &= (1 << bits) - 1;
        }
        // 26 characters carry 130 bits: the last two must be zero, so each token has exactly one spelling
        return bytes.Count == TokenBytes && buffer == 0 ? [.. bytes] : null;
    }
}

/// <summary>
/// Where the console keeps its join tokens. The console's state implements it as a table; <see cref="MemoryJoinTokenStore"/> is for tests and for a
/// console without state. <see cref="Update"/> must be atomic for one record, so two joins with one token cannot both spend it.
/// </summary>
public interface IJoinTokenStore
{
    void Add(JoinTokenRecord record);

    JoinTokenRecord? Find(string id);

    JoinTokenRecord? FindByVerifier(string verifier);

    /// <summary>Applies <paramref name="change"/> to the record with this id under the store's lock and keeps what it returns; returns the kept record, or null when there is none.</summary>
    JoinTokenRecord? Update(string id, Func<JoinTokenRecord, JoinTokenRecord> change);

    IReadOnlyList<JoinTokenRecord> All();

    /// <summary>Removes the records that ended (spent, cancelled or expired) before <paramref name="before"/>; returns how many.</summary>
    int RemoveEndedBefore(DateTimeOffset before);
}

/// <summary>Join tokens kept in memory.</summary>
public sealed class MemoryJoinTokenStore : IJoinTokenStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, JoinTokenRecord> _records = [];

    public void Add(JoinTokenRecord record)
    {
        lock (_gate) _records.Add(record.Id, record);
    }

    public JoinTokenRecord? Find(string id)
    {
        lock (_gate) return _records.GetValueOrDefault(id);
    }

    public JoinTokenRecord? FindByVerifier(string verifier)
    {
        lock (_gate) return _records.Values.FirstOrDefault(r => r.Verifier == verifier);
    }

    public JoinTokenRecord? Update(string id, Func<JoinTokenRecord, JoinTokenRecord> change)
    {
        lock (_gate)
        {
            if (!_records.TryGetValue(id, out var record)) return null;
            return _records[id] = change(record) with { Id = id };
        }
    }

    public IReadOnlyList<JoinTokenRecord> All()
    {
        lock (_gate) return [.. _records.Values.OrderBy(r => r.MadeAt)];
    }

    public int RemoveEndedBefore(DateTimeOffset before)
    {
        lock (_gate)
        {
            var ended = _records.Values.Where(r => (r.SpentAt ?? r.CancelledAt ?? r.ExpiresAt) < before).Select(r => r.Id).ToList();
            foreach (var id in ended) _records.Remove(id);
            return ended.Count;
        }
    }
}
