using System.Globalization;
using System.Text.Json;
using DbDataKit.Monitoring;
using DbDataKit.State;

namespace DbDataKit.Tracking.Build;

/// <summary>
/// Build's runs, read from the copy of its tracking into the shared <c>runs</c> module (plan 29a Q21): a Build run is the steps of <c>run_log</c> that share a <c>run_id</c>, and it is one <see cref="RunRecord"/> of the service
/// <see cref="Service"/> there, so the console reads Build's runs and Sync's and the runner's with one query. The job is the connection's identity (<c>buildtracking:&lt;engine&gt;://&lt;server&gt;/&lt;database&gt;</c>, with the
/// <c>@connection</c> of a run on a runner), the start is the first step's, the end the last step's, the rows the sum of the steps', and the outcome is Running while a step is <c>started</c>, Failed when one failed (the
/// first one is the failed item; the message says which step, never a statement or a value), else Succeeded. Nothing is read from the target: the copy is the source, so a target that is down changes nothing here.
/// A run that ended is written once; one still running is written when its first step arrives. Only when the state has the <c>runs</c> module (a state that holds the copy and no runs has nothing to project to).
/// </summary>
public static class BuildRunsProjection
{
    /// <summary>The service the projected runs carry.</summary>
    public const string Service = "build";

    public static bool Applies(StateDatabase database) => database.Layout is { } layout && layout.Has(RunsModule.Name) && layout.Has(BuildTrackingModule.Name);

    /// <summary>Projects the runs the written <paramref name="rows"/> (rows of <c>run_log</c> of <paramref name="identity"/>) belong to. Returns how many runs were written or finished.</summary>
    public static int Project(StateDatabase database, string identity, IReadOnlyList<DbDataKit.State.Tracking.TrackingRow> rows)
    {
        if (!Applies(database) || rows.Count == 0) return 0;
        var ids = new HashSet<Guid>();
        foreach (var row in rows)
        {
            try
            {
                using var parsed = JsonDocument.Parse(row.Document);
                if (parsed.RootElement.TryGetProperty("run_id", out var id) && Guid.TryParse(id.GetString(), out var guid)) ids.Add(guid);
            }
            catch (JsonException) { /* not a run row: nothing to project */ }
        }

        var written = 0;
        var store = new RunStore(database, Service);
        foreach (var id in ids) written += ProjectRun(database, store, identity, id) ? 1 : 0;
        return written;
    }

    private sealed record Step(string Id, string Model, string Status, long? Rows, DateTimeOffset Started, DateTimeOffset? Ended);

    private static bool ProjectRun(StateDatabase database, RunStore store, string identity, Guid runId)
    {
        var steps = StepsOf(database, identity, runId);
        if (steps.Count == 0) return false;
        var started = steps.Min(s => s.Started);
        var running = steps.Any(s => s.Status == "started");
        var failed = steps.Where(s => s.Status == "failed").ToList();
        var outcome = running ? RunOutcome.Running : failed.Count > 0 ? RunOutcome.Failed : RunOutcome.Succeeded;
        var existing = store.Get(runId);
        if (existing is null) store.Start(identity, runId, started);
        else if (existing.Run.Outcome != RunOutcome.Running) return false;   // a run ends once
        if (outcome == RunOutcome.Running) return existing is null;

        var ended = steps.Where(s => s.Ended is not null).Select(s => s.Ended!.Value).DefaultIfEmpty(started).Max();
        var rows = steps.Where(s => s.Rows is not null).Sum(s => s.Rows!.Value);
        RunFailure? failure = failed.Count > 0 ? new RunFailure("step", $"step {failed[0].Id} ({failed[0].Model}) failed") : null;
        return store.Finish(runId, outcome, rows, failed.FirstOrDefault()?.Model, failure, ended);
    }

    private static List<Step> StepsOf(StateDatabase database, string identity, Guid runId) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(BuildTrackingModule.Name, connection,
                "SELECT document FROM {table:tracked_row} WHERE connection_identity = $identity AND stream = 'run_log' AND archive_id = '' AND row_key LIKE $key;");
            cmd.Bind(database, "identity", identity).Bind(database, "key", $"%{runId:D}%");
            using var reader = cmd.ExecuteReader();
            var steps = new List<Step>();
            while (reader.Read())
            {
                using var parsed = JsonDocument.Parse(reader.GetString(0));
                var root = parsed.RootElement;
                string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
                DateTimeOffset? Time(string name) => Text(name) is { } t ? new DateTimeOffset(DateTime.Parse(t, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime()) : null;
                if (Text("run_id") is not { } id || !Guid.TryParse(id, out var guid) || guid != runId || Time("started_utc") is not { } start) continue;
                long? rows = root.TryGetProperty("rows_affected", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt64() : null;
                steps.Add(new Step(Text("step_id") ?? "", Text("model") ?? "", Text("status") ?? "", rows, start, Time("ended_utc")));
            }

            return steps;
        });
}
