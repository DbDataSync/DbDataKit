using System.Text.RegularExpressions;

namespace DbDataBuild.Core.Questions;

/// <summary>
/// The areas a question id can belong to (DESIGN.md 6.5 and 10.1). Ids are <c>Q-&lt;area&gt;-&lt;subject&gt;</c>, built only by
/// <see cref="QuestionIds"/>, and are deterministic: the same scenario always yields the same id, so answer files are reusable.
/// </summary>
public static class QuestionAreas
{
    public const string Define = "define";
    public const string History = "history";
    public const string Rename = "rename";
    public const string Param = "param";
    public const string Adopt = "adopt";
    public static readonly IReadOnlyList<string> All = [Define, History, Rename, Param, Adopt];
}

public static partial class QuestionIds
{
    [GeneratedRegex(@"^Q-(?<area>[a-z]+)-(?<subject>[A-Za-z0-9_.\-]+)$")]
    private static partial Regex IdPattern();

    [GeneratedRegex(@"^[A-Za-z0-9_.\-]+$")]
    private static partial Regex PartPattern();

    public static bool IsValid(string id) =>
        IdPattern().Match(id) is { Success: true } m && QuestionAreas.All.Contains(m.Groups["area"].Value);

    /// <summary>Q-define-&lt;model&gt;-&lt;field&gt;: a definition question for one field of a model.</summary>
    public static string Define(string model, string field) => Build(QuestionAreas.Define, $"{Part(model)}-{Part(field)}");

    /// <summary>Q-history-&lt;model&gt;.&lt;column&gt;: how history of a new column was handled.</summary>
    public static string History(string model, string column) => Build(QuestionAreas.History, $"{Part(model)}.{Part(column)}");

    /// <summary>Q-rename-&lt;model&gt;.&lt;column&gt;: whether a missing and a new column are a rename.</summary>
    public static string Rename(string model, string column) => Build(QuestionAreas.Rename, $"{Part(model)}.{Part(column)}");

    /// <summary>Q-param-&lt;model&gt;-&lt;operation&gt;-&lt;parameter&gt;: a runtime parameter without a value.</summary>
    public static string Param(string model, string operation, string parameter) => Build(QuestionAreas.Param, $"{Part(model)}-{Part(operation)}-{Part(parameter)}");

    /// <summary>Q-adopt-&lt;object&gt;: an existing object with no tool record.</summary>
    public static string Adopt(string obj) => Build(QuestionAreas.Adopt, Part(obj));

    private static string Build(string area, string subject) => $"Q-{area}-{subject}";

    /// <summary>
    /// Makes any name usable in an id: characters outside letters, digits, '_', '.' and '-' become <c>_u&lt;hex&gt;_</c> (a space is <c>_u20_</c>).
    /// Deterministic, so ids stay stable. An empty name is a bug in the caller.
    /// </summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) throw new ArgumentException("A question id part cannot be empty.", nameof(value));
        if (PartPattern().IsMatch(value)) return value;
        var sb = new System.Text.StringBuilder();
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.IsAscii && (char.IsAsciiLetterOrDigit((char)rune.Value) || rune.Value is '_' or '.' or '-')) sb.Append((char)rune.Value);
            else sb.Append("_u").Append(rune.Value.ToString("x")).Append('_');
        }
        return sb.ToString();
    }

    private static string Part(string value) => Sanitize(value);
}

/// <summary>One explicit way to answer. <see cref="Key"/> is lowercase snake_case and is what an answers file writes in <c>choice:</c>.</summary>
public sealed record QuestionOption(string Key, string Description, string? Consequence = null, bool TakesValue = false, string? ValueHint = null);

public enum ProposalCertainty
{
    /// <summary>Safe for <c>--accept-inferred</c> (names from paths, types from the DuckDB describe, nullability from lineage).</summary>
    High,

    /// <summary>Must be accepted explicitly, one question at a time.</summary>
    Normal,
}

/// <summary>An inferred answer with the evidence for it. Accepting it is an explicit answer, never a default.</summary>
public sealed record Proposal(string OptionKey, string? Value, ProposalCertainty Certainty, IReadOnlyList<string> Evidence);

/// <summary>An open decision: a stable id, a prompt, context lines, explicit options, and optionally an inferred proposal. No option is a default.</summary>
public sealed record Question
{
    private static readonly Regex OptionKeyPattern = new("^[a-z][a-z0-9_]*$", RegexOptions.Compiled);

    public string Id { get; }
    public string Prompt { get; }
    public IReadOnlyList<string> Context { get; }
    public IReadOnlyList<QuestionOption> Options { get; }
    public Proposal? Proposal { get; }

    /// <exception cref="ArgumentException">The question is malformed. That is a bug in the code that built it, not a user error.</exception>
    public Question(string id, string prompt, IReadOnlyList<string> context, IReadOnlyList<QuestionOption> options, Proposal? proposal = null)
    {
        if (!QuestionIds.IsValid(id)) throw new ArgumentException($"`{id}` is not a valid question id (Q-<area>-<subject>, area one of {string.Join(", ", QuestionAreas.All)}).", nameof(id));
        if (string.IsNullOrWhiteSpace(prompt)) throw new ArgumentException("A question needs a prompt.", nameof(prompt));
        if (options.Count < 2) throw new ArgumentException($"{id}: a question needs at least two explicit options.", nameof(options));
        foreach (var o in options)
        {
            if (!OptionKeyPattern.IsMatch(o.Key)) throw new ArgumentException($"{id}: option key `{o.Key}` must be lowercase snake_case.", nameof(options));
            if (string.IsNullOrWhiteSpace(o.Description)) throw new ArgumentException($"{id}: option `{o.Key}` needs a description.", nameof(options));
            if (!o.TakesValue && o.ValueHint != null) throw new ArgumentException($"{id}: option `{o.Key}` has a value hint but takes no value.", nameof(options));
        }
        if (options.Select(o => o.Key).Distinct().Count() != options.Count) throw new ArgumentException($"{id}: option keys must be unique.", nameof(options));
        if (proposal != null)
        {
            var target = options.FirstOrDefault(o => o.Key == proposal.OptionKey)
                ?? throw new ArgumentException($"{id}: the proposal names `{proposal.OptionKey}`, which is not an option.", nameof(proposal));
            if (target.TakesValue != (proposal.Value != null)) throw new ArgumentException($"{id}: the proposal's value does not fit option `{target.Key}`.", nameof(proposal));
            if (proposal.Evidence.Count == 0) throw new ArgumentException($"{id}: a proposal needs evidence.", nameof(proposal));
        }
        (Id, Prompt, Context, Options, Proposal) = (id, prompt, context, options, proposal);
    }

    public QuestionOption? FindOption(string key) => Options.FirstOrDefault(o => o.Key == key);
}
