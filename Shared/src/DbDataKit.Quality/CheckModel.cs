using System.Globalization;

namespace DbDataKit.Quality;

/// <summary>The seven kinds of check (plan 23a D4). No free-form SQL check in the first version (QF8).</summary>
public enum CheckKind { NotNull, Unique, AcceptedValues, Range, Referential, Freshness, RowCountChange }

/// <summary><see cref="Warn"/> tells the owner; <see cref="Block"/> stops the dependents of the dataset (D7), never its own writers.</summary>
public enum CheckSeverity { Warn, Block }

/// <summary>What a check that cannot run (a connection that is down) does (QF10): its result is <c>error</c>; <see cref="Alert"/> alerts and does not block, <see cref="Block"/> blocks all the same.</summary>
public enum CheckOnError { Alert, Block }

/// <summary>The names of kinds and settings as a file writes them.</summary>
public static class CheckNames
{
    public static IReadOnlyList<string> Kinds { get; } = ["not_null", "unique", "accepted_values", "range", "referential", "freshness", "row_count_change"];
    public static IReadOnlyList<string> Severities { get; } = ["warn", "block"];
    public static IReadOnlyList<string> OnErrors { get; } = ["alert", "block"];

    public static string Name(CheckKind kind) => Kinds[(int)kind];
    public static string Name(CheckSeverity severity) => Severities[(int)severity];
    public static string Name(CheckOnError onError) => OnErrors[(int)onError];

    public static bool TryKind(string? text, out CheckKind kind)
    {
        var at = text is null ? -1 : Kinds.ToList().IndexOf(text);
        kind = at < 0 ? CheckKind.NotNull : (CheckKind)at;
        return at >= 0;
    }
}

/// <summary>Whether a check is about a dataset or about an entity of a master data model, from the identity it names (lineage identities of entities are <c>suite://&lt;instance&gt;/entity/&lt;name&gt;</c>).</summary>
public enum CheckTargetKind { Dataset, Entity }

/// <summary>
/// What a check is about: a dataset or an entity, and the columns of it (none for a whole-dataset check). <see cref="Dataset"/> is a lineage identity (plan 23a D3: <c>LineageIdentity.Dataset(...).ToString()</c>, <c>LineageIdentity.SuiteObject(instance, "entity", name)</c>), so a result,
/// a lineage node and a block line up; inside a definition that cannot know its server's id (a model file, a catalog) it is the dataset's own name there (an entity, a table), which the host turns into the identity.
/// </summary>
public sealed record CheckAttachment(string Dataset, IReadOnlyList<string> Columns)
{
    /// <summary>True when <see cref="Dataset"/> is a lineage identity (<c>scheme://...</c>) and not a name local to the definition.</summary>
    public bool IsIdentity => Dataset.Contains("://", StringComparison.Ordinal);

    public CheckTargetKind TargetKind => Dataset.Contains("/entity/", StringComparison.Ordinal) && Dataset.StartsWith("suite://", StringComparison.Ordinal) ? CheckTargetKind.Entity : CheckTargetKind.Dataset;

    /// <summary>The one column of a column check, null for none or for several.</summary>
    public string? Column => Columns.Count == 1 ? Columns[0] : null;
}

/// <summary>The parameters of a kind. <see cref="NoParameters"/> for <c>not_null</c> and <c>unique</c>, which are their columns alone.</summary>
public abstract record CheckParameters;

public sealed record NoParameters : CheckParameters { public static readonly NoParameters Instance = new(); }

/// <summary>The values a column may hold (text as written; the evaluator converts them to the column's type).</summary>
public sealed record AcceptedValuesParameters(IReadOnlyList<string> Values) : CheckParameters;

/// <summary>A bound of a range, a number or an ISO date or datetime; both bounds of a check are the same sort.</summary>
public sealed record RangeBound(string Text, decimal? Number, DateTime? Moment)
{
    public bool IsNumber => Number is not null;

    public static bool TryParse(string text, out RangeBound bound)
    {
        bound = new RangeBound(text, null, null);
        if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) { bound = bound with { Number = number }; return true; }
        string[] formats = ["yyyy-MM-dd", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssZ"];
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var moment)) { bound = bound with { Moment = moment }; return true; }
        return false;
    }
}

/// <summary>At least one bound; the check passes when every value is inside them (null values are the not-null check's business).</summary>
public sealed record RangeParameters(RangeBound? Min, RangeBound? Max) : CheckParameters;

/// <summary>Every value of the columns has a row in <see cref="Dataset"/> with the same values in <see cref="Columns"/> (as many as the check's own).</summary>
public sealed record ReferentialParameters(string Dataset, IReadOnlyList<string> Columns) : CheckParameters;

/// <summary>The newest data is no older than <see cref="MaxAge"/>; by the column when the check names one, otherwise by the last successful load from run metadata (QF2).</summary>
public sealed record FreshnessParameters(TimeSpan MaxAge) : CheckParameters
{
    /// <summary>A whole number above zero and a unit (<c>s</c>, <c>m</c>, <c>h</c>, <c>d</c>), matched entirely; with anchors in the JSON Schema.</summary>
    public const string Pattern = "[1-9][0-9]{0,5}(s|m|h|d)";

    public static bool TryParse(string? text, out TimeSpan age)
    {
        age = default;
        if (string.IsNullOrEmpty(text) || text.Length < 2 || text[0] == '0' || !int.TryParse(text[..^1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1) return false;
        var span = text[^1] switch { 's' => TimeSpan.FromSeconds(count), 'm' => TimeSpan.FromMinutes(count), 'h' => TimeSpan.FromHours(count), 'd' => TimeSpan.FromDays(count), _ => (TimeSpan?)null };
        if (span is not { } s) return false;
        age = s;
        return true;
    }
}

/// <summary>The row count compared with the one of the previous successful evaluation (QF3; the first evaluation only records): at least one limit, in percent.</summary>
public sealed record RowCountChangeParameters(decimal? MaxIncreasePercent, decimal? MaxDecreasePercent) : CheckParameters;

/// <summary>One check, as read from a <c>checks:</c> section (format 2 of a definition) or a <c>check/1</c> file.</summary>
/// <param name="Id">Names the check in its results and statuses; unique within a section.</param>
/// <param name="Kind">One of the seven kinds.</param>
/// <param name="Attachment">The dataset or entity and the columns.</param>
/// <param name="Parameters">The kind's own parameters.</param>
/// <param name="Severity">Warn or block.</param>
/// <param name="OnError">What a check that cannot run does (QF10).</param>
/// <param name="Description">Free text for people.</param>
public sealed record CheckDefinition(string Id, CheckKind Kind, CheckAttachment Attachment, CheckParameters Parameters, CheckSeverity Severity = CheckSeverity.Warn, CheckOnError OnError = CheckOnError.Alert, string? Description = null);
