using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DbDataKit.Secrets;

/// <summary>
/// Scrubs registered secrets from text: the exact value plus URL-encoded, base64 and JSON-escaped
/// variants, and (always) connection-string credential fragments such as <c>Password=...</c> and
/// <c>scheme://user:password@host</c>. Thread-safe.
/// <para>Registered values are kept as strings inside the redactor for the life of the process; that is
/// the unavoidable cost of text replacement.</para>
/// </summary>
public sealed partial class Redactor : IRedactor
{
    public const string Replacement = "***";

    private readonly object _gate = new();
    private readonly HashSet<string> _needles = new(StringComparer.Ordinal);
    private volatile string[] _ordered = [];

    /// <summary>Values shorter than this are not registered (they would mangle ordinary text). Default 3.</summary>
    public int MinLength { get; init; } = 3;

    public void Register(SecretValue value) => Register(value.RevealString());

    public void Register(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length < MinLength) return;
        lock (_gate)
        {
            var changed = false;
            foreach (var v in Variants(value))
                if (v.Length > 0 && _needles.Add(v)) changed = true;
            if (changed) _ordered = [.. _needles.OrderByDescending(s => s.Length)];
        }
    }

    public string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        foreach (var needle in _ordered)
            text = text.Replace(needle, Replacement, StringComparison.Ordinal);
        text = FragmentRegex().Replace(text, m => m.Groups["key"].Value + m.Groups["eq"].Value + Replacement);
        text = UserInfoRegex().Replace(text, m => m.Groups["pre"].Value + Replacement + "@");
        return text;
    }

    private static IEnumerable<string> Variants(string v)
    {
        yield return v;
        yield return Uri.EscapeDataString(v);
        yield return WebUtility.UrlEncode(v);
        yield return Convert.ToBase64String(Encoding.UTF8.GetBytes(v));
        yield return JsonEncodedText.Encode(v).ToString();
        yield return JsonEncodedText.Encode(v, System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString();
    }

    // key = value, where value is quoted, braced, or runs to ';' / end of line.
    [GeneratedRegex("(?<key>(?<![A-Za-z0-9])(?:" + CredentialKeyDetector.KeyPattern + ")(?![A-Za-z0-9]))(?<eq>\\s*=\\s*)(?:\"[^\"]*\"|'[^']*'|\\{[^}]*\\}|[^;\\r\\n\"']*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FragmentRegex();

    [GeneratedRegex(@"(?<pre>://[^/:@\s]+:)[^@/\s]+@")]
    private static partial Regex UserInfoRegex();
}
