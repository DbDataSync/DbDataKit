using System.Globalization;
using System.Text;

namespace DbDataKit.State;

/// <summary>
/// What two ways of signing in are matched on (plan 13a U16): the person's email address, which for a Windows account is its user principal name (<c>user@domain</c>). One address is one account, whichever way the person signs in.
/// <para>
/// The key is the address trimmed, in Unicode normalisation form C and lower-cased with the invariant culture, so <c>Ada@Example.com</c>, <c>ada@example.com </c> and the UPN <c>ADA@EXAMPLE.COM</c> are one key. A name in the
/// older <c>DOMAIN\user</c> form (the SAM account name) is **never** a key: it does not say which domain's suffix the person's address has, and guessing one (<c>user@domain.local</c>) is how two people end up as one account, or one person
/// as two. A Windows sign-in that carries only a SAM name is matched by its SID alone and is not linked to anything by name (see <c>WindowsSignIn</c>).
/// </para>
/// </summary>
public static class AccountKey
{
    private const int MaxLength = 254, MaxLocalLength = 64;

    /// <summary>The key for an address, or null when it is not one: empty, with a space or control character, with other than one <c>@</c>, with nothing before or after it, or a SAM name (<c>DOMAIN\user</c>).</summary>
    public static string? Normalize(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        string text;
        try { text = address.Trim().Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return null; }
        if (text.Length is 0 or > MaxLength || IsSamName(text)) return null;
        foreach (var c in text)
            if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '\\' or ',' or ';' or '<' or '>' or '"' or '(' or ')' or '[' or ']' or ':') return null;
        var at = text.IndexOf('@');
        if (at <= 0 || at != text.LastIndexOf('@') || at == text.Length - 1 || at > MaxLocalLength) return null;
        var domain = text[(at + 1)..];
        if (domain.StartsWith('.') || domain.EndsWith('.') || domain.Contains("..", StringComparison.Ordinal)) return null;
        return text.ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>Whether this is a SAM account name (<c>DOMAIN\user</c>), which names an account in a domain but not the person's address.</summary>
    public static bool IsSamName(string? name) => name is not null && name.Contains('\\') && !name.Contains('@');
}
