using System.Globalization;

namespace DbDataKit.Runner;

/// <summary>
/// The versions of a runtime package a definition accepts (plan 28c E04). A range is comparators separated by spaces, all of which must hold: <c>&gt;=2.1</c>, <c>&lt;3</c>, <c>&gt;</c>, <c>&lt;=</c>, <c>=</c>; or a wildcard,
/// <c>2.x</c>, <c>2.1.x</c>, <c>2.*</c>, <c>*</c>; a bare <c>2</c> or <c>2.1</c> is the wildcard of that prefix, and a bare <c>2.1.4</c> is exactly that version. Versions have one to three numeric parts (<c>2</c>, <c>2.1</c>, <c>2.1.4</c>); a missing part is 0.
/// </summary>
public sealed class VersionRange
{
    private readonly List<(string Op, int[] Version)> _comparators = [];

    private VersionRange(string text) => Text = text;

    /// <summary>The range as written.</summary>
    public string Text { get; }

    /// <summary>Reads a version (<c>2.1.4</c>); null when it is not one.</summary>
    public static int[]? ParseVersion(string text)
    {
        var parts = text.Split('.');
        if (parts.Length is < 1 or > 3) return null;
        var numbers = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i])) return null;
        return numbers;
    }

    /// <summary>Reads a range; false with the problem in words when it is not one.</summary>
    public static bool TryParse(string text, out VersionRange range, out string error)
    {
        range = new VersionRange(text);
        error = "";
        var tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length == 0) { error = "the range is empty; write one such as 2.x or >=2.1 <3."; return false; }
        foreach (var token in tokens)
        {
            if (token is "*" or "x" or "X") continue;
            var op = token.StartsWith(">=", StringComparison.Ordinal) ? ">=" : token.StartsWith("<=", StringComparison.Ordinal) ? "<=" : token[0] is '>' or '<' or '=' ? token[..1] : "";
            var rest = token[op.Length..];
            var wild = rest.EndsWith(".x", StringComparison.OrdinalIgnoreCase) || rest.EndsWith(".*", StringComparison.Ordinal);
            var core = wild ? rest[..^2] : rest;
            var parsed = core.Length == 0 ? null : ParseVersion(core);
            if (parsed is null || (wild && (op.Length > 0 || core.Split('.').Length > 2)))
            {
                error = $"'{token}' is not a version comparator; write 2.x, 2.1.x, 2.1.4, >=2.1 or <3.";
                return false;
            }

            var parts = core.Split('.').Length;
            if (wild || (op.Length == 0 && parts < 3))
            {
                var next = (int[])parsed.Clone();
                next[parts - 1]++;
                range._comparators.Add((">=", parsed));
                range._comparators.Add(("<", next));
            }
            else range._comparators.Add((op.Length == 0 ? "=" : op, parsed));
        }

        return true;
    }

    /// <summary>True when <paramref name="version"/> (<c>2.1.4</c>) is in the range; false for text that is not a version.</summary>
    public bool Contains(string version)
    {
        if (ParseVersion(version) is not { } v) return false;
        foreach (var (op, bound) in _comparators)
        {
            var c = Compare(v, bound);
            var ok = op switch { ">=" => c >= 0, ">" => c > 0, "<=" => c <= 0, "<" => c < 0, _ => c == 0 };
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>Orders two versions as numbers, part by part.</summary>
    public static int Compare(int[] a, int[] b)
    {
        for (var i = 0; i < 3; i++)
            if (a[i] != b[i]) return a[i].CompareTo(b[i]);
        return 0;
    }

    public override string ToString() => Text;
}
