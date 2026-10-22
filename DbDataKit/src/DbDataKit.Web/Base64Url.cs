namespace DbDataKit.Web.Trust;

internal static class Base64Url
{
    public static string Encode(ReadOnlySpan<byte> bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>Decodes unpadded URL-safe base64; null for anything else (padding, the standard alphabet, whitespace, a bad length).</summary>
    public static byte[]? Decode(string text)
    {
        if (text.Length == 0 || text.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))) return null;
        var padded = text.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 0 => padded, 2 => padded + "==", 3 => padded + "=", _ => null! };
        if (padded is null) return null;
        try { return Convert.FromBase64String(padded); }
        catch (FormatException) { return null; }
    }
}
