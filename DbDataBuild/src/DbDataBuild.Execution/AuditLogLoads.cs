using System.Globalization;
using DbDataBuild.State;

namespace DbDataBuild.Execution;

/// <summary>Reads of the audit tables for lineage: the last run of each model's load.</summary>
public static partial class AuditLog
{
    /// <summary>
    /// The last run of one model's load or copy: when it ended, whether every step of it ended `ok`, and the rows its steps wrote (null when none of them counted). A load that was skipped or never ran is not a run and is not here.
    /// </summary>
    public sealed record LastLoad(string Model, DateTime EndedUtc, bool Succeeded, long? Rows);

    /// <summary>
    /// The last run of each model on the scope's connection, from `run_log`. <paramref name="hookEvents"/> are the operation names of hooks, which are rows of the model's run and not loads. A run is the rows one run id has for a model (a model with
    /// several operations is one run); the newest by start time is the last. Only the newest <paramref name="limit"/> rows are read, so a model that has not run for a long time may not be in the answer.
    /// </summary>
    public static async Task<IReadOnlyList<LastLoad>> LastLoadsAsync(ReadSession read, TrackingScope scope, IReadOnlyCollection<string> hookEvents, int limit = 20000, CancellationToken ct = default)
    {
        string c(string n) => C(scope.Engine, n);
        var cols = $"{c("model")}, {c("run_id")}, {c("status")}, {c("rows_affected")}, {c("started_utc")}, {c("ended_utc")}";
        var notHooks = hookEvents.Count == 0 ? "" : $" AND {c("operation")} NOT IN ({string.Join(", ", hookEvents.Select(e => "'" + e.Replace("'", "''") + "'"))})";
        var filter = $"{c("connection")} = @connection AND ({c("status")} = 'ok' OR {c("status")} LIKE 'failed%'){notHooks}";
        var sql = scope.Engine == "postgres"
            ? $"SELECT {cols} FROM {T(scope, "run_log")} WHERE {filter} ORDER BY {c("started_utc")} DESC LIMIT {limit}"
            : $"SELECT TOP ({limit}) {cols} FROM {T(scope, "run_log")} WHERE {filter} ORDER BY {c("started_utc")} DESC";
        var rows = await read.QueryAsync(sql, [Connection(scope)], ct);
        static string Text(object? v) => (v as string)?.Trim() ?? "";
        static DateTime When(object? v) => v is DateTime d ? d : DateTime.MinValue;
        static long? Long(object? v) => v is null or DBNull ? null : Convert.ToInt64(v, CultureInfo.InvariantCulture);
        return LastLoadsOf(rows.Select(r => new LoadStep(Text(r[0]), Convert.ToString(r[1], CultureInfo.InvariantCulture) ?? "", Text(r[2]), Long(r[3]), When(r[4]), r[5] is DateTime ended ? ended : When(r[4]))));
    }

    /// <summary>One step of a load as the tracking tables record it (a row of <c>run_log</c>): the model, the run it belongs to, how it ended (<c>ok</c> or <c>failed...</c>), the rows it wrote and when it began and ended (the start when it never ended).</summary>
    public sealed record LoadStep(string Model, string RunId, string Status, long? Rows, DateTime StartedUtc, DateTime EndedUtc);

    /// <summary>The last run of each model among the steps: a run is the steps of one run id (a model with several operations is one run), the newest by start time is the last.</summary>
    public static IReadOnlyList<LastLoad> LastLoadsOf(IEnumerable<LoadStep> steps)
    {
        var result = new List<LastLoad>();
        foreach (var model in steps.GroupBy(r => r.Model))
        {
            var newest = model.GroupBy(r => r.RunId).OrderByDescending(g => g.Max(r => r.StartedUtc)).First().ToList();
            var counted = newest.Where(r => r.Rows != null).Select(r => r.Rows!.Value).ToList();
            result.Add(new LastLoad(model.Key, newest.Max(r => r.EndedUtc), newest.All(r => r.Status == "ok"), counted.Count == 0 ? null : counted.Sum()));
        }
        return result.OrderBy(l => l.Model, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The last run of each model from the documents of the server's copy of the tracking tables (the <c>run_log</c> stream, plan 28c E16): the same answer as <see cref="LastLoadsAsync"/> gives from the tables, without a connection to the target. A
    /// document that is not a step of a load (a hook's row, a step that is still running, one that is not JSON) is not a run and is skipped.
    /// </summary>
    public static IReadOnlyList<LastLoad> LastLoadsFromCopy(IEnumerable<string> documents, IReadOnlyCollection<string> hookEvents)
    {
        var steps = new List<LoadStep>();
        foreach (var document in documents)
        {
            try
            {
                using var parsed = System.Text.Json.JsonDocument.Parse(document);
                var root = parsed.RootElement;
                string? Field(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString()?.Trim() : null;
                DateTime? Time(string name) => Field(name) is { } t && DateTime.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when) ? when : null;
                var status = Field("status") ?? "";
                if (Field("model") is not { Length: > 0 } model || Field("run_id") is not { } run || Time("started_utc") is not { } started) continue;
                if (status != "ok" && !status.StartsWith("failed", StringComparison.Ordinal)) continue;
                if (Field("operation") is { } operation && hookEvents.Contains(operation)) continue;
                long? rows = root.TryGetProperty("rows_affected", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number ? r.GetInt64() : null;
                steps.Add(new LoadStep(model, run, status, rows, started, Time("ended_utc") ?? started));
            }
            catch (System.Text.Json.JsonException) { /* not a step of a run */ }
        }
        return LastLoadsOf(steps);
    }
}
