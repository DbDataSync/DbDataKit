using System.Text.RegularExpressions;

namespace DbDataKit.Secrets;

public enum CredentialFindingKind
{
    /// <summary>A key such as Password/Pwd/AccountKey holds a literal value.</summary>
    CredentialKey,
    /// <summary><c>Authentication=</c> selects a password-based mode (the value is not itself a secret).</summary>
    PasswordAuthenticationMode,
    /// <summary>A URL such as <c>scheme://user:password@host</c>.</summary>
    UrlUserInfo,
}

/// <param name="Key">The offending key (or "url-userinfo"). Never contains the value.</param>
/// <param name="Kind">What was found.</param>
public sealed record CredentialFinding(string Key, CredentialFindingKind Kind);

/// <summary>
/// Finds plaintext credentials in connection strings and property bags. Keys are matched as whole
/// keys (the token before '='), so a value that merely mentions a password is not flagged. A value
/// that is itself a <see cref="SecretRef"/> is not a plaintext credential.
/// </summary>
public static partial class CredentialKeyDetector
{
    /// <summary>Normalised (lower-case, no space/_/-/.) keys matched exactly.</summary>
    private static readonly HashSet<string> ExactKeys = new(StringComparer.Ordinal)
    {
        "password", "pwd", "passwd", "pass", "secret", "accountkey", "apikey", "token", "sas", "pat",
        "sastoken", "sharedaccesskey", "sharedaccesssignature", "accesskey", "secretkey", "clientsecret",
        "accesstoken", "authtoken", "bearertoken", "privatekey", "keypassword", "certificatepassword",
    };

    private static readonly string[] ContainsFragments =
        ["password", "passwd", "secret", "apikey", "accountkey", "sastoken", "accesstoken", "authtoken", "privatekey"];

    private static readonly string[] SuffixFragments = ["token", "pwd"];

    /// <summary>Regex alternation of connection-string credential keys, for scrubbing free text.</summary>
    public const string KeyPattern =
        @"pass[ _-]?word|pass[ _-]?wd|pwd|secret|account[ _-]?key|api[ _-]?key|access[ _-]?token|auth[ _-]?token|token|sas[ _-]?token|sas|shared[ _-]?access[ _-]?key|shared[ _-]?access[ _-]?signature|pat";

    private static string Normalize(string key) =>
        key.Trim().Replace(" ", "").Replace("_", "").Replace("-", "").Replace(".", "").ToLowerInvariant();

    /// <summary>True when the key names a credential (strict whole-key match: the connection-string rule).</summary>
    public static bool IsCredentialKey(string key) => ExactKeys.Contains(Normalize(key));

    /// <summary>Broader rule for property bags and config keys: also keys containing e.g. "password" ("DbPassword"). Keys ending in "Ref" are references, not secrets.</summary>
    public static bool IsCredentialPropertyKey(string key)
    {
        var n = Normalize(key);
        if (n.Length == 0) return false;
        if (ExactKeys.Contains(n)) return true;
        if (n.EndsWith("ref", StringComparison.Ordinal) || n.EndsWith("secretname", StringComparison.Ordinal)) return false;
        return ContainsFragments.Any(f => n.Contains(f, StringComparison.Ordinal))
            || SuffixFragments.Any(f => n.EndsWith(f, StringComparison.Ordinal));
    }

    public static bool ContainsCredential(string connectionString, bool allowReferences = true) =>
        FindInConnectionString(connectionString, allowReferences).Count > 0;

    public static IReadOnlyList<CredentialFinding> FindInConnectionString(string? connectionString, bool allowReferences = true)
    {
        var findings = new List<CredentialFinding>();
        if (string.IsNullOrWhiteSpace(connectionString)) return findings;

        foreach (var (key, value) in ConnectionStringParser.Parse(connectionString))
        {
            if (IsCredentialKey(key))
            {
                if (IsPlaintext(value, allowReferences)) findings.Add(new(key, CredentialFindingKind.CredentialKey));
            }
            else if (Normalize(key) == "authentication" && Normalize(value).Contains("password", StringComparison.Ordinal))
            {
                findings.Add(new(key, CredentialFindingKind.PasswordAuthenticationMode));
            }
        }
        if (UrlUserInfoRegex().IsMatch(connectionString))
            findings.Add(new("url-userinfo", CredentialFindingKind.UrlUserInfo));
        return findings;
    }

    /// <summary>Checks a property bag (e.g. a connection's <c>Properties</c>); values that are secret refs or empty pass.</summary>
    public static IReadOnlyList<CredentialFinding> FindInProperties(
        IEnumerable<KeyValuePair<string, string?>> properties, bool allowReferences = true)
    {
        var findings = new List<CredentialFinding>();
        foreach (var (key, value) in properties)
        {
            if (IsCredentialPropertyKey(key))
            {
                if (IsPlaintext(value, allowReferences)) findings.Add(new(key, CredentialFindingKind.CredentialKey));
            }
            else if (value is not null && value.Contains('=') && value.Contains(';'))
            {
                findings.AddRange(FindInConnectionString(value, allowReferences)); // nested connection string
            }
        }
        return findings;
    }

    private static bool IsPlaintext(string? value, bool allowReferences) =>
        !string.IsNullOrWhiteSpace(value) && !(allowReferences && SecretRef.LooksLikeRef(value.Trim()));

    [GeneratedRegex(@"://[^/:@\s]+:[^@/\s]+@")]
    private static partial Regex UrlUserInfoRegex();
}

/// <summary>Minimal connection-string tokenizer: ';' separated, values may be '...', "..." or {...} quoted.</summary>
internal static class ConnectionStringParser
{
    public static IEnumerable<(string Key, string Value)> Parse(string text)
    {
        var i = 0;
        while (i < text.Length)
        {
            var eq = -1;
            for (var j = i; j < text.Length && text[j] != ';'; j++)
            {
                if (text[j] == '=') { eq = j; break; }
            }
            if (eq < 0)
            {
                var skip = text.IndexOf(';', i);
                if (skip < 0) yield break;
                i = skip + 1;
                continue;
            }
            var key = text[i..eq].Trim();
            var k = eq + 1;
            while (k < text.Length && text[k] == ' ') k++;
            string value;
            if (k < text.Length && (text[k] == '"' || text[k] == '\'' || text[k] == '{'))
            {
                var close = text[k] == '{' ? '}' : text[k];
                var e = k + 1;
                while (e < text.Length)
                {
                    if (text[e] == close)
                    {
                        if (close != '}' && e + 1 < text.Length && text[e + 1] == close) { e += 2; continue; }
                        break;
                    }
                    e++;
                }
                e = Math.Min(e, text.Length);
                value = text[(k + 1)..e];
                var next = text.IndexOf(';', e);
                i = next < 0 ? text.Length : next + 1;
            }
            else
            {
                var semi = text.IndexOf(';', k);
                value = (semi < 0 ? text[k..] : text[k..semi]).Trim();
                i = semi < 0 ? text.Length : semi + 1;
            }
            if (key.Length > 0) yield return (key, value);
        }
    }
}
