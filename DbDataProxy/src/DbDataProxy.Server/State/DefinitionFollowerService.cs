using DbDataKit.State;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Serving;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.State;

/// <summary>
/// Serves on this instance what the group publishes (plan 20 O03): the models published before they were in the state are read in once, every published definition is handed to what serves its kind when the instance
/// starts, and the change log is read again every <see cref="ProxyOptions.DefinitionPollSeconds"/>. A version that cannot be used here is logged, and the one served before keeps serving.
/// </summary>
public sealed class DefinitionFollowerService(DefinitionFollower follower, PublishedSemanticModels semantic, PublishedDataSources dataSources, IOptionsMonitor<ProxyOptions> options, ILogger<DefinitionFollowerService> log) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        var dataDirectory = DbDataProxy.Server.Hosting.ProxyHost.ResolveDataDirectory(options.CurrentValue);
        if (semantic.ImportFolder(dataDirectory) is > 0 and var imported)
            log.LogInformation("Read {Count} published semantic models of {Folder} into the group's state; the folder is now {Folder}.imported.", imported, PublishedSemanticModels.LegacyFolder, PublishedSemanticModels.LegacyFolder);
        // a data source the group stopped serving while this instance was stopped goes before the rest is applied
        dataSources.Reconcile();
        follower.Start();
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(options.CurrentValue.DefinitionPollSeconds), stoppingToken); }
            catch (OperationCanceledException) { return; }
            try { follower.Poll(); }
            catch (Exception ex) when (ex is System.Data.Common.DbException or InvalidOperationException or TimeoutException)
            {
                // the state could not be read: what is served stays, and the next poll tries again from where this one stood
                log.LogWarning("The published definitions could not be read from the state: {Message}", ex.Message);
            }
        }
    }
}
