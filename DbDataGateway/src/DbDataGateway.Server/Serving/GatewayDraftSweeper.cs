using DbDataGateway.Server.Configuration;
using DbDataKit.Monitoring;
using DbDataKit.Store;

namespace DbDataGateway.Server.Serving;

/// <summary>
/// Runs <see cref="GroupStoreApi.SweepDraftsAsync"/> an hour after start and every hour after (plan 21 CH07), with <c>store.idleDays</c> and <c>store.noticeDays</c> as the settings hold them at each pass, so a reload of the settings
/// applies without a restart. The notices go to the Gateway's own notification feed.
/// </summary>
public sealed class GatewayDraftSweeper(GroupStoreApi api, INotificationFeed feed, Func<StoreModuleOptions> settings, ILogger log, TimeProvider? clock = null) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    /// <summary>One pass at <paramref name="now"/>; public so a test injects the day.</summary>
    public async Task<DraftSweepResult> RunOnceAsync(DateTimeOffset now, TimeSpan sincePrevious, CancellationToken cancellationToken)
    {
        var current = settings();
        return await api.SweepDraftsAsync(feed, current.IdleDays, current.NoticeDays, now, sincePrevious, cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            using var timer = new PeriodicTimer(Interval, clock ?? TimeProvider.System);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    var result = await RunOnceAsync((clock ?? TimeProvider.System).GetUtcNow(), Interval, stoppingToken);
                    if (result.Removed + result.Warned > 0) log.LogInformation("Preview expiry: {Removed} removed, {Warned} warned.", result.Removed, result.Warned);
                }
                catch (Exception ex) when (ex is not OperationCanceledException) { log.LogWarning("Preview expiry failed: {Message}", ex.Message); }
            }
        }
        catch (OperationCanceledException) { }
    }
}
