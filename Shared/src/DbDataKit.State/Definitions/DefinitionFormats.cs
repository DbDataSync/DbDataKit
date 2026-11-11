using System.Globalization;

namespace DbDataKit.State;

/// <summary>
/// The formats of one kind of definition that a program reads (plan 24 section 2.4, 24a D3): its current format and the one before it. Formats are numbered from 1 per kind. A program reads
/// both and refuses a newer one with a message (<see cref="DefinitionFormat.Refusal"/>); a format older than <see cref="Previous"/> is refused the same way, naming both numbers.
/// </summary>
public readonly record struct DefinitionFormats(int Previous, int Current)
{
    /// <summary>A kind that has only ever had format 1.</summary>
    public static DefinitionFormats One { get; } = new(1, 1);

    /// <summary>A kind whose current format is <paramref name="current"/>; it reads the one before it as well (never below 1).</summary>
    public static DefinitionFormats Of(int current) => new(Math.Max(1, current - 1), current);

    public bool Reads(int format) => format >= Previous && format <= Current;

    /// <summary><c>1</c> or <c>1 to 2</c>, for messages.</summary>
    public override string ToString() => Previous == Current ? Current.ToString(CultureInfo.InvariantCulture) : $"{Previous} to {Current}";
}

/// <summary>The words of the format rules, in one place so the publisher, the followers and the runner say the same thing.</summary>
public static class DefinitionFormat
{
    /// <summary>The first format of every kind; what a version stored before the <c>format</c> column existed is.</summary>
    public const int First = 1;

    /// <summary>What a program that reads <paramref name="reads"/> says about a version it cannot read, or null when it can.</summary>
    public static string? Refusal(string kind, string name, int? version, int format, DefinitionFormats reads, string who = "this program")
    {
        if (reads.Reads(format)) return null;
        var which = version is { } v ? $"version {v} of the {kind} '{name}'" : $"the {kind} '{name}'";
        return format > reads.Current
            ? $"{which} is in format {format}, and {who} reads {(reads.Previous == reads.Current ? "format" : "formats")} {reads} of {kind}. Upgrade {who}; the definition was written by a newer version."
            : $"{which} is in format {format}, which {who} no longer reads (it reads {(reads.Previous == reads.Current ? "format" : "formats")} {reads} of {kind}). Publish it again in a current format.";
    }
}

/// <summary>A version that this program does not read because of its format (newer than the program knows, or older than it still reads). The message names the kind, the name and both numbers.</summary>
public sealed class DefinitionFormatException(string message, string kind, string name, int format, DefinitionFormats reads) : InvalidOperationException(message)
{
    public string Kind { get; } = kind;
    public string Name { get; } = name;
    public int Format { get; } = format;
    public DefinitionFormats Reads { get; } = reads;
}

/// <summary>The text an instance row keeps its definition formats as: <c>kind=previous-current;kind=previous-current</c>, sorted by kind (<c>semantic-model=1-2;app=1-1</c>).</summary>
public static class DefinitionFormatText
{
    public static string Format(IEnumerable<(string Kind, DefinitionFormats Formats)> kinds) =>
        string.Join(';', kinds.DistinctBy(k => k.Kind).OrderBy(k => k.Kind, StringComparer.Ordinal)
            .Select(k => $"{k.Kind}={k.Formats.Previous.ToString(CultureInfo.InvariantCulture)}-{k.Formats.Current.ToString(CultureInfo.InvariantCulture)}"));

    /// <summary>Reads the text back; null for no text. A part that is not <c>kind=a-b</c> (or <c>kind=a</c>) is skipped, so a newer text never stops an older reader.</summary>
    public static IReadOnlyDictionary<string, DefinitionFormats>? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var result = new Dictionary<string, DefinitionFormats>(StringComparer.Ordinal);
        foreach (var part in text.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.IndexOf('=', StringComparison.Ordinal);
            if (at <= 0) continue;
            var range = part[(at + 1)..].Split('-', 2);
            if (!int.TryParse(range[^1], NumberStyles.None, CultureInfo.InvariantCulture, out var current) || current < 1) continue;
            var previous = current;
            if (range.Length == 2 && !int.TryParse(range[0], NumberStyles.None, CultureInfo.InvariantCulture, out previous)) continue;
            if (previous < 1 || previous > current) continue;
            result[part[..at].Trim()] = new DefinitionFormats(previous, current);
        }

        return result;
    }
}
