using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataKit.Web.Trust;

/// <summary>What redeeming a code gives the pairing: the most the new client may grant here, and the label the administrator chose for it.</summary>
public sealed record PairingGrant(ServerRole Ceiling, string? Label);

/// <summary>A code just made. It is shown once; only its hash is kept.</summary>
public sealed record PairingCode(string Code, DateTimeOffset ExpiresAt);

/// <summary>
/// The single-use pairing codes (plan 13, W12): <c>server pair</c> makes one on the server's machine (the proof that the person owns it), and the client redeems it once, within minutes, to be paired. The
/// codes wait in a small file next to <c>server.yaml</c> because the command that makes them and the server that redeems them are different processes. Only a hash of a code is stored; a code expires,
/// works once, and five wrong codes in a row cancel every code still waiting (a code is 100 bits, so this is for the day someone tries).
/// </summary>
public sealed class PairingCodes(string path, TimeProvider? clock = null)
{
    public static readonly TimeSpan DefaultLifetime = TimeSpan.FromMinutes(10);
    public const int MaxFailures = 5;
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // no 0, O, 1, I

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private sealed class Entry
    {
        public string Hash { get; set; } = "";
        public DateTimeOffset Expires { get; set; }
        public int Ceiling { get; set; }
        public string? Label { get; set; }
    }

    private sealed class Document
    {
        public int Failures { get; set; }
        public List<Entry> Codes { get; set; } = [];
    }

    public PairingCode Create(ServerRole ceiling, string? label = null, TimeSpan? lifetime = null)
    {
        var chars = new char[20];
        for (var i = 0; i < chars.Length; i++) chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        var raw = new string(chars);
        var expires = _clock.GetUtcNow() + (lifetime ?? DefaultLifetime);
        Update(doc => doc.Codes.Add(new Entry { Hash = Hash(raw), Expires = expires, Ceiling = (int)ceiling, Label = label }));
        return new PairingCode(string.Join('-', Enumerable.Range(0, 4).Select(i => raw.Substring(i * 5, 5))), expires);
    }

    /// <summary>Spends the code: the grant it carries, or null when it is wrong, expired or used. A wrong code counts against <see cref="MaxFailures"/>.</summary>
    public PairingGrant? Redeem(string? code)
    {
        var normal = Normalize(code);
        PairingGrant? grant = null;
        Update(doc =>
        {
            var hash = normal is null ? null : Hash(normal);
            var hit = hash is null ? null : doc.Codes.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(c.Hash), Encoding.ASCII.GetBytes(hash)));
            if (hit is null)
            {
                if (++doc.Failures >= MaxFailures)
                {
                    doc.Codes.Clear();
                    doc.Failures = 0;
                }
                return;
            }
            doc.Codes.Remove(hit);
            doc.Failures = 0;
            grant = new PairingGrant((ServerRole)hit.Ceiling, hit.Label);
        });
        return grant;
    }

    /// <summary>How many codes are waiting (unexpired, unused).</summary>
    public int Pending()
    {
        var count = 0;
        Update(doc => count = doc.Codes.Count);
        return count;
    }

    private static string? Normalize(string? code)
    {
        if (code is null) return null;
        var text = new string(code.Where(c => c != '-' && !char.IsWhiteSpace(c)).Select(char.ToUpperInvariant).ToArray());
        return text.Length == 20 && text.All(Alphabet.Contains) ? text : null;
    }

    private static string Hash(string normal) => Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(normal)));

    private void Update(Action<Document> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var gate = FileLock.Take(path + ".lock");
        var document = Read();
        var now = _clock.GetUtcNow();
        document.Codes.RemoveAll(c => c.Expires <= now);
        change(document);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(document), new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    private Document Read()
    {
        if (!File.Exists(path)) return new Document();
        try { return JsonSerializer.Deserialize<Document>(File.ReadAllText(path)) ?? new Document(); }
        catch (JsonException) { return new Document(); } // a damaged file holds no codes
    }
}
