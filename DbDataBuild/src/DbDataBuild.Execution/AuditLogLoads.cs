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
        var result = new List<LastLoad>();
        foreach (var model in rows.GroupBy(r => Text(r[0])))
        {
            var newest = model.GroupBy(r => Convert.ToString(r[1], CultureInfo.InvariantCulture)).OrderByDescending(g => g.Max(r => When(r[4]))).First().ToList();
            var counted = newest.Select(r => Long(r[3])).Where(n => n != null).ToList();
            result.Add(new LastLoad(model.Key, newest.Max(r => r[5] is DateTime ended ? ended : When(r[4])), newest.All(r => Text(r[2]) == "ok"), counted.Count == 0 ? null : counted.Sum()));
        }
        return result.OrderBy(l => l.Model, StringComparer.Ordinal).ToList();
    }
}
