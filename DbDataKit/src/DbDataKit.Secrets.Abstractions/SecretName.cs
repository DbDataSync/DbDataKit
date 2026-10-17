using System.Text.RegularExpressions;

namespace DbDataKit.Secrets;

/// <summary>
/// Convention <c>dbdata:&lt;product&gt;:&lt;kind&gt;:&lt;name&gt;[:&lt;part&gt;]</c>, e.g.
/// <c>dbdata:build:connection:warehouse:write</c>. Segments are lower-cased and limited to
/// letters, digits, '.', '_' and '-' so names are safe as file names and env-var stems.
/// </summary>
public readonly partial record struct SecretName
{
    public const string Root = "dbdata";

    public string Product { get; }
    public string Kind { get; }
    public string Name { get; }
    public string? Part { get; }

    public SecretName(string product, string kind, string name, string? part = null)
    {
        Product = Segment(product, nameof(product));
        Kind = Segment(kind, nameof(kind));
        Name = Segment(name, nameof(name));
        Part = part is null ? null : Segment(part, nameof(part));
    }

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex SegmentRegex();

    private static string Segment(string value, string what)
    {
        if (string.IsNullOrEmpty(value) || !SegmentRegex().IsMatch(value))
            throw new FormatException($"Secret name {what} '{value}' must be non-empty and contain only letters, digits, '.', '_' or '-'.");
        return value.ToLowerInvariant();
    }

    public static SecretName Parse(string text) =>
        TryParse(text, out var n) ? n : throw new FormatException(
            $"'{text}' is not a secret name; expected 'dbdata:<product>:<kind>:<name>[:<part>]'.");

    public static bool TryParse(string? text, out SecretName name)
    {
        name = default;
        if (text is null) return false;
        var p = text.Split(':');
        if ((p.Length != 4 && p.Length != 5) || !p[0].Equals(Root, StringComparison.OrdinalIgnoreCase)) return false;
        try { name = new SecretName(p[1], p[2], p[3], p.Length == 5 ? p[4] : null); return true; }
        catch (FormatException) { return false; }
    }

    public override string ToString() => Part is null
        ? $"{Root}:{Product}:{Kind}:{Name}"
        : $"{Root}:{Product}:{Kind}:{Name}:{Part}";

    public SecretRef ToRef() => new(SecretRefKind.Secret, ToString());
}
