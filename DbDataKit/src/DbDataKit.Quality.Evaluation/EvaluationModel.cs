namespace DbDataKit.Quality;

/// <summary>What an evaluation found. <see cref="Error"/> is a check that could not run (a connection that is down, a table or column that is not there, a limit reached): neither a pass nor a fail (plan 23a QF10).</summary>
public enum EvaluationStatus { Pass, Fail, Error }

/// <summary>
/// The limits of every statement the evaluator sends. A limit that is reached is an <see cref="EvaluationStatus.Error"/> that says so, never a silent pass (plan 23a, risk "cost of evaluation").
/// </summary>
public sealed record EvaluationLimits
{
    /// <summary>The most sample keys a result keeps, whatever a caller asks for: 10.</summary>
    public const int SampleKeyCeiling = 10;

    /// <summary>How long one statement may run. It is the command's timeout and a cancellation, so an engine that ignores one still stops at the other.</summary>
    public TimeSpan StatementTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How many sample keys to read (1 to <see cref="SampleKeyCeiling"/>). The statement itself limits its rows to this many; nothing else is ever read.</summary>
    public int MaxSampleKeys { get; init; } = SampleKeyCeiling;

    /// <summary>The longest a value in a sample key may be, in characters; a longer one is cut and ends with an ellipsis.</summary>
    public int MaxValueLength { get; init; } = 80;

    /// <summary>The most values an <c>accepted_values</c> check may list; more is an error, because the statement would be too long to be sure of.</summary>
    public int MaxAcceptedValues { get; init; } = 500;

    /// <summary>The longest an error message is, in characters.</summary>
    public int MaxMessageLength { get; init; } = 300;
}

/// <summary>What the evaluator is told about one check beyond the check itself. The host knows these; the check's file cannot.</summary>
public sealed class EvaluationContext
{
    /// <summary>The table of a dataset the check names (its own, and the target of a <c>referential</c> check), as the parts of its qualified name; null when the host does not know the dataset (the result is an error).</summary>
    public required Func<string, IReadOnlyList<string>?> ResolveTable { get; init; }

    /// <summary>
    /// The columns that identify a row of the dataset (its key). With them, the sample keys of a failing row check (<c>not_null</c>, <c>accepted_values</c>, <c>range</c>, <c>referential</c>) are the values of these columns and nothing else;
    /// without them such a check keeps the values of its own columns where they say what failed, and no sample for <c>not_null</c>, whose failing value is null.
    /// </summary>
    public IReadOnlyList<string> KeyColumns { get; init; } = [];

    /// <summary>When the last successful load of the dataset finished, from run metadata (Build tracking, Sync runs): what a <c>freshness</c> check without a column measures (plan 23a QF2).</summary>
    public DateTimeOffset? LastLoadedAt { get; init; }

    /// <summary>The row count of the previous successful evaluation of this check: the baseline of <c>row_count_change</c> (QF3). Null for the first evaluation, which only records.</summary>
    public decimal? PreviousRowCount { get; init; }
}

/// <summary>
/// The result of one check.
/// <para>
/// <b>What a sample key is.</b> A sample key is one row (or, for <c>unique</c>, one duplicated value) written as <c>column=value, column=value</c>, only for the columns that name it: the dataset's key columns when the host
/// gave them (<see cref="EvaluationContext.KeyColumns"/>), otherwise the check's own columns for the kinds in which those are what failed. No other column of a failing row is ever selected, so no value beyond them can leave the engine. At most
/// <see cref="EvaluationLimits.MaxSampleKeys"/> are read and each value is cut at <see cref="EvaluationLimits.MaxValueLength"/>. The message of an <see cref="EvaluationStatus.Error"/> never repeats a data value either: only the message of
/// a missing table or column (which holds identifiers) is kept as the engine wrote it.
/// </para>
/// </summary>
/// <param name="Measured">In the unit of the kind: failing rows (<c>not_null</c>, <c>unique</c>, <c>accepted_values</c>, <c>range</c>, <c>referential</c>), seconds of age (<c>freshness</c>), percent of change (<c>row_count_change</c>); null when there is none.</param>
/// <param name="Threshold">What the check allows, in the same unit: 0 failing rows, the maximum age in seconds, the limit in percent that applies.</param>
/// <param name="Observed">The raw reading the kind made: the row count of <c>row_count_change</c>, the age's source for <c>freshness</c> as epoch seconds; the baseline of the next row-count evaluation.</param>
/// <param name="Statement">The one statement that was sent (null for a check that sent none: a freshness check on the last load, or one refused before it ran). It holds the check's own literals, never a secret.</param>
public sealed record CheckEvaluation(string CheckId, EvaluationStatus Status, decimal? Measured, decimal? Threshold, IReadOnlyList<string> SampleKeys, decimal? Observed, string? Message, string? Statement, DateTimeOffset EvaluatedAt)
{
    public static CheckEvaluation Failed(string checkId, string message, DateTimeOffset at, string? statement = null) => new(checkId, EvaluationStatus.Error, null, null, [], null, message, statement, at);
}
