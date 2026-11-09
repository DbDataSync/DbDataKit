using System.Text.Json;

namespace DbDataSync.State;

/// <summary>
/// One pass of a drain on the runner as the run's own output (plan 28c E13): what <c>sync_state.run_detail</c> and the run's counters hold for a pass the API's worker ran, written by the drain as the step
/// <see cref="PassRecordText.StepName"/> keyed by the pass's run id and read back by the API through the runner's <c>.../steps/{name}</c>. A drain is one run of the runner and a pass is a unit of it, so this is the row of Sync's run
/// history for a runner-owned replication: its mapping, kind, outcome, rows, timings, the failure Sync classified (<see cref="FailureKind"/>, what drives the "resync" offer) and the summary, which for a pass that lost the
/// initial-load race is the <c>Not run: ...</c> reason of an outcome that is <c>Succeeded</c>.
/// </summary>
/// <param name="RunId">The pass's own run id (what its log lines name in the engine).</param>
/// <param name="DrainRunId">The runner's run of the drain that ran the pass: the log lines of the pass are in that run's log, between <see cref="StartedAt"/> and <see cref="EndedAt"/>.</param>
/// <param name="Status">A <see cref="RunStatus"/> as text: <c>Succeeded</c>, <c>Failed</c> or <c>Cancelled</c>.</param>
public sealed record PassRecord(
    Guid RunId,
    Guid DrainRunId,
    string Replication,
    string Kind,
    string Mapping,
    string? Segment,
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    long RowsRead,
    long RowsWritten,
    string? Summary,
    string? FailureKind,
    string? Detail,
    string? BatchId);

/// <summary>The pass records as text: written by the drain, read by the API (the same code on both sides).</summary>
public static class PassRecordText
{
    /// <summary>The name of the step a drain records each pass as.</summary>
    public const string StepName = "pass-record";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Serialize(PassRecord record) => JsonSerializer.Serialize(record, Json);

    /// <summary>The record in the text of a step result, or null when the text is not one (a record of another version is skipped, not guessed at).</summary>
    public static PassRecord? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<PassRecord>(text, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>The mapping a runner's failed item names: the unit key of a pass is the mapping, <c>#</c> and the claim's token (<c>orders#1a2b3c4d</c>).</summary>
    public static string MappingOf(string? failedItem)
    {
        if (string.IsNullOrEmpty(failedItem)) return "";
        var at = failedItem.LastIndexOf('#');
        return at > 0 ? failedItem[..at] : failedItem;
    }

    /// <summary>The Sync run a pass record stands for: the shape the run history, the run read and the monitoring row are made of.</summary>
    public static TaskRunRecord ToRun(PassRecord record) => new(
        record.RunId, record.Replication, Pid: null,
        Enum.TryParse<RunStatus>(record.Status, out var status) ? status : RunStatus.Failed,
        Enum.TryParse<RunKind>(record.Kind, out var kind) ? kind : RunKind.Primary,
        record.Mapping, record.Segment is { Length: > 0 } s && s != WorkQueueStore.NoSegment ? s : null,
        EnqueuedAtUtc: record.StartedAt, ClaimedAtUtc: record.StartedAt, StartedAtUtc: record.StartedAt, EndedAtUtc: record.EndedAt,
        record.RowsRead, record.RowsWritten, record.Summary, record.FailureKind, Timing: null, PreviousWatermark: null, NewWatermark: null, ErrorDetail: record.Detail);
}

/// <summary>One check the polling gate made of a source: what its counter read (<c>sync_state.change_check</c>'s row, plan 28c E11b).</summary>
public sealed record ChangeCheckEntry(string Connection, string Database, string ReaderKind, string? Value, DateTimeOffset? SourceTimeUtc, DateTimeOffset CheckedAtUtc);

/// <summary>The checks of one cycle of a drain, an output of the run.</summary>
public sealed record ChangeCheckBatch(string Replication, List<ChangeCheckEntry> Checks);

/// <summary>The checks of a drain as text (what the API reads to say how far behind its source a runner-owned mapping is, plan 28c E13).</summary>
public static class ChangeCheckText
{
    /// <summary>The name of the step a drain records the polling gate's checks as.</summary>
    public const string StepName = "change-checks";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The batch in the text of a step result, or null when the text is not one.</summary>
    public static ChangeCheckBatch? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        try { return JsonSerializer.Deserialize<ChangeCheckBatch>(text, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>One check as the state's own row (what <see cref="ChangeCheckStore"/> keeps for the API's path), so the lag figures are computed from the same shape on both paths.</summary>
    public static ChangeCheck ToCheck(ChangeCheckEntry entry) => new(entry.Connection, entry.Database, entry.ReaderKind, entry.Value, entry.CheckedAtUtc, entry.SourceTimeUtc);
}
