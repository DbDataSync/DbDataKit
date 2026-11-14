namespace DbDataSync.Api.Services;

/// <summary>
/// The live view of runs over SignalR. Since plan 28c E22 every run is the runner's: <see cref="DbDataSync.Api.Runner.RunnerRunStreamer"/> reads the drains' runs and lines from the runner (plan 28c E13)
/// and publishes them to the run's group, so the screens follow a run as they did when this server ran it.
/// </summary>
public sealed class RunMonitorService(
    DbDataSync.Api.Runner.RunnerRunStreamer runnerStream,
    ILogger<RunMonitorService> logger) : BackgroundService
{
    /// <summary>
    /// How often the runs on the runner are looked at (plan 28c E13): the lines are asked of another process over HTTP and the live view is meant to show a line within a second of its being written. One
    /// request lists the runs; a request per run going reads its new lines.
    /// </summary>
    private static readonly TimeSpan RunnerTickInterval = TimeSpan.FromMilliseconds(400);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RunnerTickInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await runnerStream.Tick(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // the live view is never allowed to end this service
                logger.LogWarning(ex, "The live view of the runner's runs failed; it is tried again.");
            }
        }
    }
}
