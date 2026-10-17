using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Metadata;

/// <summary>An entry key: which connection, which metadata epoch, which item.</summary>
public sealed record CatalogCacheKey(string Fingerprint, long Epoch, string Item);

/// <summary>
/// Optional cache for catalog results. Entries are keyed by connection fingerprint and a per-fingerprint epoch; bumping the epoch
/// (<see cref="Invalidate"/>) makes every older entry unreachable without enumerating them. Products that cache column metadata in
/// their own files (Sync mappings) keep that; this is for the shared invalidation rule only.
/// </summary>
public interface ICatalogCache
{
    long GetEpoch(string fingerprint);

    /// <summary>Starts a new epoch for the connection and returns it.</summary>
    long Invalidate(string fingerprint);

    bool TryGet<T>(CatalogCacheKey key, out T? value) where T : class;

    void Set<T>(CatalogCacheKey key, T value) where T : class;
}

/// <summary>A stable, secret-free identity of a connection string for cache keys and lock files.</summary>
public static class ConnectionFingerprint
{
    private static readonly string[] SecretKeys = ["pwd", "password", "secret", "token", "accesstoken", "sharedaccesssignature", "apikey", "api_key"];

    /// <summary>
    /// SHA-256 (first 16 hex digits) of the connection string with keys lower-cased, sorted and secret-bearing keys removed, so the same
    /// server and database give the same fingerprint whatever the key order or password.
    /// </summary>
    public static string Of(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        var pairs = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string part in SplitPairs(connectionString))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            string key = part[..eq].Trim().ToLowerInvariant();
            if (SecretKeys.Contains(key)) continue;
            pairs[key] = part[(eq + 1)..].Trim();
        }
        string canonical = string.Join(';', pairs.Select(p => p.Key + "=" + p.Value));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..16].ToLowerInvariant();
    }

    // Splits on ';' outside {braces} and quotes.
    private static IEnumerable<string> SplitPairs(string text)
    {
        var sb = new StringBuilder();
        int brace = 0;
        char quote = '\0';
        foreach (char c in text)
        {
            if (quote != '\0') { sb.Append(c); if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
            if (c == '{') brace++;
            else if (c == '}' && brace > 0) brace--;
            if (c == ';' && brace == 0) { yield return sb.ToString(); sb.Clear(); }
            else sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }
}
