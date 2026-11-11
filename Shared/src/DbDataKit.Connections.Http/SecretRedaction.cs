namespace DbDataKit.Connections.Http;

/// <summary>Takes secrets out of words that may be shown or logged (plan 22a GP18 fold): an HTTP failure's message can hold the request address, which holds the secret parts of a webhook's address.</summary>
public static class SecretRedaction
{
    public const string Mask = "***";

    /// <summary>The text with every value (and its URL-escaped form, and for a value with slashes each of its segments) replaced by <c>***</c>, longest first. Values shorter than 4 characters are not replaced (they would blank ordinary words).</summary>
    public static string Redact(string? text, IEnumerable<string> values)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (value.Length < 4) continue;
            all.Add(value);
            all.Add(Uri.EscapeDataString(value));
            all.Add(Uri.EscapeDataString(value).Replace("%2F", "/", StringComparison.Ordinal));
            foreach (var segment in value.Split('/')) if (segment.Length >= 4) all.Add(segment);
        }

        foreach (var value in all.OrderByDescending(v => v.Length)) text = text.Replace(value, Mask, StringComparison.Ordinal);
        return text;
    }
}
