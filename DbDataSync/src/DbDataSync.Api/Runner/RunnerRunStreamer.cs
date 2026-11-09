using DbDataKit.Runner.Client;
using DbDataSync.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace DbDataSync.Api.Runner;

/// <summary>
/// The live view of the runs the runner owns (plan 28c E13): <see cref="Tick"/> asks the runner which drains of the replications it owns are going or have just ended, sends the new lines of each one's log to the hub
/// group of its run id as <c>logLine</c> and, when one has ended, <c>runCompleted</c>, as <c>RunMonitorService</c> does for the runs of this server's own workers. It reads the runner's log by line id, so a line is sent once
/// and the order is the runner's; a runner that cannot be asked is logged (once per spell) and tried again at the next tick. The hub does not replay: a client that joins late reads the run's log endpoint first
/// (<c>GET /api/runs/{id}/logs</c>, which serves the runner's lines for a run of the runner) and the hub for what comes after.
/// </summary>
public sealed class RunnerRunStreamer(RunnerRunSource runs, IHubContext<RunHub> hub, ILogger<RunnerRunStreamer> logger, TimeProvider? clock = null)
{
    /// <summary>How long an ended drain stays in the window in which its end is announced if the stream never saw it going (a drain that began and ended between two ticks).</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromSeconds(30);

    private readonly Dictionary<Guid, long> _lastLine = [];
    private readonly HashSet<Guid> _watching = [];
    private readonly HashSet<Guid> _announced = [];
    private bool _unreachable;

    public async Task Tick(CancellationToken ct)
    {
        if (!runs.AnyOwned) return;
        IReadOnlyList<(string Replication, ClientRun Run)> drains;
        try
        {
            drains = await runs.RecentDrainsAsync(ct).ConfigureAwait(false);
            if (_unreachable) logger.LogInformation("The runner answers again; the live view of its runs goes on.");
            _unreachable = false;
        }
        catch (Exception ex) when (ex is RunnerUnavailableException or RunnerClientException)
        {
            if (!_unreachable) logger.LogWarning("The live view of the runs the runner owns waits: {Reason}", ex.Message);
            _unreachable = true;
            return;
        }

        var now = (clock ?? TimeProvider.System).GetUtcNow();
        foreach (var (_, run) in drains)
        {
            if (run.Outcome == "running")
            {
                _watching.Add(run.Id);
                await StreamAsync(run.Id, ct).ConfigureAwait(false);
            }
            else if (!_announced.Contains(run.Id) && (_watching.Contains(run.Id) || (run.Finished is { } ended && now - ended <= RecentWindow)))
            {
                await StreamAsync(run.Id, ct).ConfigureAwait(false);
                _announced.Add(run.Id);
                _watching.Remove(run.Id);
                _lastLine.Remove(run.Id);
                await hub.Clients.Group(run.Id.ToString()).SendAsync(
                    "runCompleted",
                    new { runId = run.Id, status = StatusOf(run.Outcome), RowsRead = (long?)null, RowsWritten = run.Rows, ErrorSummary = run.FailureMessage },
                    ct).ConfigureAwait(false);
            }
        }
    }

    private static string StatusOf(string outcome) => outcome switch
    {
        "succeeded" => "Succeeded",
        "stopped" => "Cancelled",
        "running" => "Running",
        _ => "Failed",
    };

    /// <summary>Sends the lines after the last one sent; a long log is read in pages, so a burst of lines is sent in one tick.</summary>
    private async Task StreamAsync(Guid runId, CancellationToken ct)
    {
        var group = hub.Clients.Group(runId.ToString());
        while (true)
        {
            var after = _lastLine.TryGetValue(runId, out var last) ? last : (long?)null;
            var lines = await runs.LogLinesAsync(runId, after, ct).ConfigureAwait(false);
            if (lines is null || lines.Count == 0) return;
            foreach (var line in lines)
            {
                await group.SendAsync("logLine", new { line.Id, line.TimestampUtc, Level = line.Level.ToString(), line.Message }, ct).ConfigureAwait(false);
                _lastLine[runId] = line.Id;
            }

            if (lines.Count < 1000) return;
        }
    }
}
