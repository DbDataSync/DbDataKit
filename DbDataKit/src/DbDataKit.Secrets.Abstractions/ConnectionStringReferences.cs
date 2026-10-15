using System.Text;

namespace DbDataKit.Secrets;

/// <summary>A credential key in a connection string whose value is a secret reference.</summary>
/// <param name="Key">The key as written (for example <c>Password</c>).</param>
/// <param name="Reference">The parsed reference. Never a resolved value.</param>
public sealed record ConnectionStringReference(string Key, SecretRef Reference);

/// <summary>
/// Secret references inside connection strings: <c>Password=env:DB_PASSWORD</c> (a credential key whose value is a
/// reference) and whole-string references (<c>secret:dbdata:proxy:datasource:erp</c>). Resolution returns a new
/// string; the input is never modified and no value is ever logged.
/// </summary>
public static class ConnectionStringReferences
{
    /// <summary>Quotes a resolved value for a connection-string dialect. The default wraps in <c>{...}</c> (ODBC).</summary>
    public delegate string QuoteValue(string value);

    /// <summary>ODBC style: <c>{value}</c> with <c>}</c> doubled.</summary>
    public static string QuoteBraces(string value) => "{" + value.Replace("}", "}}", StringComparison.Ordinal) + "}";

    /// <summary>ADO.NET style: <c>"value"</c> with <c>"</c> doubled.</summary>
    public static string QuoteDouble(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    /// <summary>True when the whole (trimmed) text is a secret reference.</summary>
    public static bool IsWholeReference(string? connectionString, out SecretRef? reference)
    {
        reference = null;
        return connectionString is not null && SecretRef.TryParse(connectionString, out reference);
    }

    /// <summary>
    /// The literal values of credential keys (<c>Password</c>, <c>Pwd</c>, <c>AccountKey</c>, ...) in a connection string, for
    /// registering with a redactor after a whole-string secret was resolved. Reference values are skipped.
    /// </summary>
    public static IReadOnlyList<string> CredentialValues(string? connectionString)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(connectionString)) return list;
        foreach (var (key, value) in ConnectionStringParser.Parse(connectionString))
        {
            if (CredentialKeyDetector.IsCredentialKey(key) && value.Length > 0 && !SecretRef.LooksLikeRef(value)) list.Add(value);
        }
        return list;
    }

    /// <summary>The credential keys of <paramref name="connectionString"/> whose value is a reference.</summary>
    public static IReadOnlyList<ConnectionStringReference> Find(string? connectionString)
    {
        var list = new List<ConnectionStringReference>();
        if (string.IsNullOrWhiteSpace(connectionString)) return list;
        foreach (var (key, value, _, _) in ConnectionStringParser.ParseSpans(connectionString))
        {
            if (CredentialKeyDetector.IsCredentialKey(key) && SecretRef.TryParse(value, out var r))
                list.Add(new ConnectionStringReference(key, r));
        }
        return list;
    }

    /// <summary>
    /// Returns the connection string with references resolved: a whole-string reference becomes the resolved text, and
    /// every credential key whose value is a reference gets the resolved value (quoted with <paramref name="quote"/>
    /// when it contains characters that need it). Strings without references are returned unchanged.
    /// </summary>
    /// <exception cref="SecretNotFoundException">A reference could not be resolved.</exception>
    public static async ValueTask<string> ResolveAsync(
        string connectionString, ISecretResolver resolver, QuoteValue? quote = null, CancellationToken ct = default)
    {
        if (IsWholeReference(connectionString, out var whole))
        {
            using var v = await resolver.ResolveAsync(whole!, ct).ConfigureAwait(false);
            return v.RevealString();
        }

        quote ??= QuoteBraces;
        StringBuilder? sb = null;
        var last = 0;
        foreach (var (key, value, start, end) in ConnectionStringParser.ParseSpans(connectionString))
        {
            if (!CredentialKeyDetector.IsCredentialKey(key) || !SecretRef.TryParse(value, out var r)) continue;
            using var secret = await resolver.ResolveAsync(r, ct).ConfigureAwait(false);
            var text = secret.RevealString();
            sb ??= new StringBuilder(connectionString.Length + 16);
            sb.Append(connectionString, last, start - last);
            sb.Append(NeedsQuoting(text) ? quote(text) : text);
            last = end;
        }
        if (sb is null) return connectionString;
        sb.Append(connectionString, last, connectionString.Length - last);
        return sb.ToString();
    }

    private static bool NeedsQuoting(string v) =>
        v.Length == 0 || v.IndexOfAny([';', '=', '{', '}', '"', '\'']) >= 0 || v != v.Trim();
}
