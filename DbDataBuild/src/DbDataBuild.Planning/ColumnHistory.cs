namespace DbDataBuild.Planning;

/// <summary>An applied plan, as the audit trail kept it: its id, who applied it and when, and the plan itself.</summary>
public sealed record AppliedPlan(string PlanId, string AppliedBy, DateTime AppliedUtc, Plan Plan);

/// <summary>One recorded shape of an object (`schema_version`), oldest first per object.</summary>
public sealed record RecordedShape(string Object, string ShapeHash, DateTime FirstSeenUtc, string Source, string? PlanId);

/// <summary>One range an operation produced (`operation_interval`), with the time its run started.</summary>
public sealed record RecordedInterval(string Model, string? RangeStart, string? RangeEnd, string Operation, DateTime StartedUtc);

/// <summary>An operator's acknowledgement (`block_log`), keyed `code|subject|detail`.</summary>
public sealed record HistoryAck(string Key, string By, string Reason, DateTime Utc);

/// <param name="NeedsAttention">True when the recorded history contradicts what a person decided (a backfill was requested and never recorded) and nobody has acknowledged it.</param>
/// <param name="AckKey">The key an acknowledgement of this entry has (`DDB-443|model.column|plan id`), or null when there is nothing to acknowledge.</param>
/// <param name="Acknowledgement">Who accepted the inconsistency, when and why, if somebody did.</param>
public sealed record ColumnHistoryEntry(string Model, string Column, string Disposition, string Text, bool NeedsAttention, string? AckKey = null, HistoryAck? Acknowledgement = null);

/// <summary>
/// The per-column history consistency report (DESIGN.md 12.3): for each column a plan added, when it was added, what the person decided about the rows that were
/// already loaded, and whether the recorded loads agree. Built only from the audit tables: the decision comes from the answers embedded in the applied plan.
/// </summary>
public static class ColumnHistory
{
    public static IReadOnlyList<ColumnHistoryEntry> Build(IReadOnlyList<AppliedPlan> plans, IReadOnlyList<RecordedShape> shapes, IReadOnlyList<RecordedInterval> intervals, IReadOnlyList<HistoryAck>? acknowledgements = null)
    {
        var entries = new List<ColumnHistoryEntry>();
        foreach (var applied in plans.OrderBy(p => p.AppliedUtc))
            foreach (var answer in applied.Plan.Answers.Where(a => a.QuestionId.StartsWith("Q-history-", StringComparison.Ordinal)).OrderBy(a => a.QuestionId, StringComparer.Ordinal))
            {
                var subject = answer.QuestionId["Q-history-".Length..];
                var dot = subject.LastIndexOf('.');
                if (dot <= 0) continue;
                var (model, column) = (subject[..dot], subject[(dot + 1)..]);

                var objectShapes = shapes.Where(s => s.Object == model).OrderBy(s => s.FirstSeenUtc).ToList();
                var version = objectShapes.FindIndex(s => s.PlanId == applied.PlanId && s.Source == "tool") + 1;
                var addedAt = applied.AppliedUtc;
                var before = intervals.Where(i => i.Model == model && i.StartedUtc < addedAt).ToList();
                var after = intervals.Where(i => i.Model == model && i.StartedUtc >= addedAt).ToList();
                var backfills = after.Count(i => i.Operation == "backfill");

                var where = version > 0 ? $"schema version {version} of {objectShapes.Count}" : "no recorded shape for this plan";
                var text = $"`{column}` was added to {model} by plan {applied.PlanId} on {addedAt:yyyy-MM-dd HH:mm:ss} UTC ({where}). " +
                           $"Decision: {answer.Choice} ({answer.Source.ToString().ToLowerInvariant()}{(answer.Note == null ? "" : ", \"" + answer.Note + "\"")}), applied by {applied.AppliedBy}. " +
                           $"{before.Count} recorded load range(s) before the change hold NULL in it; {after.Count} after, of which {backfills} backfill(s).";
                var inconsistent = answer.Choice == "backfill_later" && backfills == 0;
                var key = inconsistent ? $"DDB-443|{model}.{column}|{applied.PlanId}" : null;
                var ack = key == null ? null : acknowledgements?.LastOrDefault(a => a.Key == key);
                if (inconsistent) text += " A backfill was requested and none has been recorded.";
                if (ack != null) text += $" Acknowledged by {ack.By} on {ack.Utc:yyyy-MM-dd HH:mm:ss} UTC: \"{ack.Reason}\" (a continuing concern only if the operator says so).";
                entries.Add(new ColumnHistoryEntry(model, column, answer.Choice, text, inconsistent && ack == null, key, ack));
            }
        return entries;
    }
}
