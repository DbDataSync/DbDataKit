using System.Text.Json.Nodes;

namespace DbDataKit.State;

/// <summary>
/// The JSON a server sends a console about the checks it evaluated (plan 23a FQ17): every product that writes the <c>quality</c> module answers <c>GET .../quality</c> with <see cref="Overview"/> and <c>GET .../quality/history</c> with
/// <see cref="History"/>, so the console reads one shape from Build, Sync and the others. Nothing here evaluates; it only reads what <see cref="QualityStore"/> holds. A message of a result is the evaluator's own (never a secret).
/// </summary>
public static class QualityDocuments
{
    /// <summary>The newest results shown for one check; the store keeps 90 days (<see cref="QualityModule.HistoryDays"/>).</summary>
    public const int HistoryLimit = 50;

    /// <summary>
    /// <c>{ datasets: [{ dataset, blocking, checks: [{ check_id, severity, on_error, status, status_since, measured, threshold, evaluated_at, blocking, blocked_since }] }] }</c>, datasets by identity, checks by id. <paramref name="canRun"/> and
    /// <paramref name="runNote"/> say whether this server can evaluate on request and, when it cannot, how its checks are evaluated; <paramref name="canRelease"/> whether an administrator can release a block here (<see cref="QualityStore.Release"/>).
    /// </summary>
    public static JsonObject Overview(QualityStore store, bool canRun, string? runNote = null, bool canRelease = false)
    {
        var datasets = new JsonArray();
        foreach (var group in store.All().GroupBy(s => s.Dataset, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var checks = new JsonArray();
            foreach (var s in group.OrderBy(s => s.CheckId, StringComparer.Ordinal))
                checks.Add(new JsonObject
                {
                    ["check_id"] = s.CheckId, ["severity"] = s.Severity, ["on_error"] = s.OnError, ["status"] = QualityStore.Name(s.Outcome), ["status_since"] = s.StatusSince,
                    ["measured"] = s.Measured, ["threshold"] = s.Threshold, ["evaluated_at"] = s.EvaluatedAt, ["blocking"] = s.Blocking, ["blocked_since"] = s.BlockedSince,
                    ["released_since"] = s.ReleasedSince, ["released_by"] = s.ReleasedBy,
                });
            datasets.Add(new JsonObject { ["dataset"] = group.Key, ["blocking"] = group.Count(s => s.Blocking), ["checks"] = checks });
        }

        return new JsonObject { ["datasets"] = datasets, ["can_run"] = canRun, ["run_note"] = runNote, ["can_release"] = canRelease };
    }

    /// <summary><c>{ dataset, check_id, results: [{ id, status, evaluated_at, measured, threshold, sample_keys, run_id, message }] }</c>, newest first.</summary>
    public static JsonObject History(QualityStore store, string dataset, string checkId, int limit = HistoryLimit)
    {
        var results = new JsonArray();
        foreach (var r in store.History(dataset, checkId, Math.Clamp(limit, 1, 500)))
            results.Add(new JsonObject
            {
                ["id"] = r.Id, ["status"] = QualityStore.Name(r.Outcome), ["evaluated_at"] = r.EvaluatedAt, ["measured"] = r.Measured, ["threshold"] = r.Threshold,
                ["sample_keys"] = new JsonArray(r.SampleKeys.Select(k => (JsonNode)JsonValue.Create(k)!).ToArray()), ["run_id"] = r.RunId, ["message"] = r.Message,
            });
        return new JsonObject { ["dataset"] = dataset, ["check_id"] = checkId, ["results"] = results };
    }
}
