using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace DbDataKit.Quality;

/// <summary>
/// One check as a screen or an API sends it (plan 33 UE07): the keys of a <c>checks:</c> entry in the file, flat, with the names the file uses (<c>on_error</c>, <c>max_age</c>), so a form and the YAML say the same thing. It carries no rule of its own:
/// <see cref="CheckForms.ToYaml"/> writes it as a section and <see cref="CheckSection"/> (the strict reader, one for everything that reads a <c>checks:</c> section) decides whether it is a check, with the file, line and a suggestion for what is wrong.
/// </summary>
public sealed record CheckForm
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("kind")] public string? Kind { get; init; }
    [JsonPropertyName("dataset")] public string? Dataset { get; init; }
    [JsonPropertyName("column")] public string? Column { get; init; }
    [JsonPropertyName("columns")] public IReadOnlyList<string>? Columns { get; init; }
    [JsonPropertyName("severity")] public string? Severity { get; init; }
    [JsonPropertyName("on_error")] public string? OnError { get; init; }
    [JsonPropertyName("description")] public string? Description { get; init; }
    [JsonPropertyName("max_age")] public string? MaxAge { get; init; }
    [JsonPropertyName("values")] public IReadOnlyList<string>? Values { get; init; }
    [JsonPropertyName("min")] public string? Min { get; init; }
    [JsonPropertyName("max")] public string? Max { get; init; }
    [JsonPropertyName("references")] public CheckReferenceForm? References { get; init; }
    [JsonPropertyName("max_increase_percent")] public string? MaxIncreasePercent { get; init; }
    [JsonPropertyName("max_decrease_percent")] public string? MaxDecreasePercent { get; init; }
}

/// <summary>The target of a <c>referential</c> check: the dataset and the column or columns the values must exist in.</summary>
public sealed record CheckReferenceForm
{
    [JsonPropertyName("dataset")] public string? Dataset { get; init; }
    [JsonPropertyName("column")] public string? Column { get; init; }
    [JsonPropertyName("columns")] public IReadOnlyList<string>? Columns { get; init; }
}

public static class CheckForms
{
    /// <summary>The form of a check that was read: what a screen shows. Column and columns follow the file's own choice (one column is <c>column</c>); severity and on_error are always given.</summary>
    public static CheckForm Of(CheckDefinition check)
    {
        var columns = check.Attachment.Columns;
        var form = new CheckForm
        {
            Id = check.Id, Kind = CheckNames.Name(check.Kind), Dataset = check.Attachment.Dataset, Severity = CheckNames.Name(check.Severity), OnError = CheckNames.Name(check.OnError), Description = check.Description,
            Column = columns.Count == 1 ? columns[0] : null, Columns = columns.Count > 1 ? columns : null,
        };
        return check.Parameters switch
        {
            AcceptedValuesParameters a => form with { Values = a.Values },
            RangeParameters r => form with { Min = r.Min?.Text, Max = r.Max?.Text },
            ReferentialParameters r => form with { References = new CheckReferenceForm { Dataset = r.Dataset, Column = r.Columns.Count == 1 ? r.Columns[0] : null, Columns = r.Columns.Count > 1 ? r.Columns : null } },
            FreshnessParameters f => form with { MaxAge = AgeText(f.MaxAge) },
            RowCountChangeParameters c => form with { MaxIncreasePercent = Percent(c.MaxIncreasePercent), MaxDecreasePercent = Percent(c.MaxDecreasePercent) },
            _ => form,
        };
    }

    private static string? Percent(decimal? value) => value?.ToString(CultureInfo.InvariantCulture);

    /// <summary>A span as the file writes it: the largest unit that divides it, <c>1d</c>, <c>90m</c>, <c>6h</c>.</summary>
    public static string AgeText(TimeSpan age)
    {
        var seconds = (long)age.TotalSeconds;
        if (seconds % 86400 == 0) return $"{seconds / 86400}d";
        if (seconds % 3600 == 0) return $"{seconds / 3600}h";
        if (seconds % 60 == 0) return $"{seconds / 60}m";
        return $"{seconds}s";
    }

    /// <summary>
    /// The <c>checks:</c> section for these forms, as block YAML with a final newline (nothing at all for no check, so a section with no entries is not written). Only what a form sets is written, in the order the file's own examples use. Text
    /// that is not plain is double-quoted; nothing is validated here (a kind that is not one, a missing column, a repeated id are for <see cref="CheckSection"/>).
    /// </summary>
    public static string ToYaml(IReadOnlyList<CheckForm> forms)
    {
        if (forms.Count == 0) return "";
        var text = new StringBuilder("checks:\n");
        foreach (var f in forms)
        {
            var lines = new List<(string Key, string Value)>();
            void Add(string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) lines.Add((key, Scalar(value))); }
            void AddList(string key, IReadOnlyList<string>? values) { if (values is { Count: > 0 }) lines.Add((key, "[" + string.Join(", ", values.Select(Scalar)) + "]")); }
            Add("id", f.Id); Add("kind", f.Kind); Add("dataset", f.Dataset); Add("column", f.Column); AddList("columns", f.Columns);
            Add("max_age", f.MaxAge); AddList("values", f.Values); Add("min", f.Min); Add("max", f.Max);
            if (f.References is { } r)
            {
                var inner = new List<string>();
                if (!string.IsNullOrWhiteSpace(r.Dataset)) inner.Add("dataset: " + Scalar(r.Dataset));
                if (!string.IsNullOrWhiteSpace(r.Column)) inner.Add("column: " + Scalar(r.Column));
                if (r.Columns is { Count: > 0 }) inner.Add("columns: [" + string.Join(", ", r.Columns.Select(Scalar)) + "]");
                lines.Add(("references", "{ " + string.Join(", ", inner) + " }"));
            }
            Add("max_increase_percent", f.MaxIncreasePercent); Add("max_decrease_percent", f.MaxDecreasePercent);
            Add("severity", f.Severity); Add("on_error", f.OnError); Add("description", f.Description);
            for (var i = 0; i < lines.Count; i++) text.Append(i == 0 ? "  - " : "    ").Append(lines[i].Key).Append(": ").Append(lines[i].Value).Append('\n');
            if (lines.Count == 0) text.Append("  - {}\n");
        }

        return text.ToString();
    }

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase) { "true", "false", "null", "yes", "no", "on", "off", "y", "n" };

    /// <summary>A scalar as plain text when it is safe in YAML (letters, digits, <c>_ . / + -</c> inside, no leading symbol, not a word YAML reads as a value), else double-quoted.</summary>
    internal static string Scalar(string value)
    {
        var plain = value.Length > 0 && !Reserved.Contains(value) && (char.IsLetterOrDigit(value[0]) || value[0] == '_')
            && value.All(c => char.IsLetterOrDigit(c) || c is '_' or '.' or '/' or '+' or '-');
        if (plain) return value;
        var quoted = new StringBuilder("\"");
        foreach (var c in value)
            quoted.Append(c switch { '\\' => "\\\\", '"' => "\\\"", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t", _ => c.ToString() });
        return quoted.Append('"').ToString();
    }
}
