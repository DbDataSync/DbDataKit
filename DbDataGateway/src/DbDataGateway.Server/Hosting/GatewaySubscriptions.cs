using DbDataKit.Delivery;
using DbDataKit.Runner;
using DbDataKit.Runner.Execution;
using DbDataKit.State;
using DbDataKit.Views.Hosting;
using DbDataGateway.Server.Serving;
using Microsoft.Extensions.Hosting;

namespace DbDataGateway.Server.Hosting;

/// <summary>The subscriber as the views module draws them: the identity home's enabled user of that subject id (<see cref="GatewayViews.SubscriberPrincipalAsync"/>), never the Gateway's own identity.</summary>
public sealed class HomeSubscriberPrincipals(GatewayViews views) : ISubscriberPrincipals
{
    public async Task<ViewsPrincipal?> ForAsync(string subscriber, CancellationToken cancellationToken) => await views.SubscriberPrincipalAsync(subscriber, cancellationToken).ConfigureAwait(false);
}

/// <summary>Who a subscriber is to the delivery service: the identity home's enabled user (their address is the one the home has); a removed or disabled one is not found.</summary>
public sealed class HomeSubscriberDirectory(GatewayViews views) : ISubscriberDirectory
{
    public async Task<SubscriberContact?> FindAsync(string person, CancellationToken cancellationToken) =>
        await views.SubscriberAsync(person, cancellationToken).ConfigureAwait(false) is { } user ? new SubscriberContact(user.Id, user.DisplayName, user.Email, user.Enabled) : null;
}

/// <summary>
/// Scheduled delivery in the Gateway (plan 32 DBR18, decision: in process, as <c>quality-system</c> runs in its host): the system task <c>views-export / subscriptions</c> (<see cref="ViewsExportTasks"/>) composed over the Gateway's state, the views module's
/// <see cref="SubscriptionRenderer"/> (the document drawn <b>as the subscriber</b>: the identity home's user of that subject id, their groups, their data sources) and the delivery service, run every minute (<see cref="ViewsExportSettings.Every"/>) while the
/// views module is on <b>and this instance can export</b> (<c>views.export</c>) <b>and the host registered delivery channels</b> (<see cref="IDeliveryChannels"/>): an instance that could only fail a due subscription (no browser, or no channel to send by) does not claim it,
/// so it waits for a process that can. The Gateway has no channels of its own yet (the channel names are the console's bindings and connections: plan 22a GP18, plan 27a), so nothing registers them today and the task is idle; a host or a test that registers them
/// makes it run, and this instance then also sends the outbox.
/// </summary>
public sealed class GatewaySubscriptions : BackgroundService
{
    public const string RunService = "dbdatagateway";

    private readonly GatewayViews _views;
    private readonly StateDatabase _state;
    private readonly IDeliveryChannels? _channels;
    private readonly TimeProvider _time;
    private readonly ILogger _log;
    private readonly TimeSpan _every;
    private ViewsExportTasks? _tasks;
    private DeliveryService? _delivery;

    public GatewaySubscriptions(GatewayViews views, StateDatabase state, ILoggerFactory loggers, IDeliveryChannels? channels = null, TimeProvider? time = null, TimeSpan? every = null)
    {
        _views = views;
        _state = state;
        _channels = channels;
        _time = time ?? TimeProvider.System;
        _log = loggers.CreateLogger("DbDataGateway.ViewsExport");
        _every = every ?? TimeSpan.FromMinutes(1);
    }

    /// <summary>Whether the host registered delivery channels (without them the task does not run).</summary>
    public bool HasChannels => _channels is not null;

    /// <summary>The subscriptions of the Gateway's state.</summary>
    public SubscriptionStore Subscriptions => new(_state);

    /// <summary>The delivery service the task hands its documents to (composed on the first pass).</summary>
    public DeliveryService? Delivery => _delivery;

    private ViewsExportTasks? Compose()
    {
        if (_tasks is not null) return _tasks;
        if (_channels is null || _views.Module.Api is not { } api) return null;      // no channel to send by, or the module has not started
        var renderer = new SubscriptionRenderer(api, _views.Publications, new HomeSubscriberPrincipals(_views));
        _delivery = new DeliveryService(new DeliveryStore(_state), new PayloadStore(_state), _channels, DeliveryPolicy.Default, new SecretScrubber(), _time, null, RunService);
        var service = new SubscriptionService(Subscriptions, _delivery, renderer, new HomeSubscriberDirectory(_views), _time);
        return _tasks = new ViewsExportTasks(service, _state, RunService);
    }

    /// <summary>One pass of the task, or null when this instance does not draw subscriptions (no exporter, no delivery channels, or the views module is not started).</summary>
    public async Task<TaskRunResult?> RunPassAsync(CancellationToken cancellationToken, string? subscription = null)
    {
        if (!_views.CanExport) return null;
        if (Compose() is not { } tasks) return null;
        var request = new TaskRunRequest
        {
            Task = new TaskRef(ViewsExportTasks.Package, DefinitionStore.Production, ViewsExportTasks.Task), PackageDirectory = AppContext.BaseDirectory, PackageVersion = "system",
            Launch = new PackageLaunch(LaunchKind.Executable, Command: "dbdatagateway"), Chain = Guid.NewGuid().ToString("N"),
            Inputs = subscription is null ? new Dictionary<string, string>() : new Dictionary<string, string> { [ViewsExportTasks.SubscriptionInput] = subscription },
        };
        return await tasks.RunAsync(request, cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_every, _time);
        Task? sender = null;
        try
        {
            do
            {
                try
                {
                    var result = await RunPassAsync(stoppingToken).ConfigureAwait(false);
                    if (result is { End.State: not TaskEndState.Succeeded }) _log.LogWarning("Subscriptions: the pass ended {State}: {Failure}", result.End.State, result.End.Failure?.Message ?? result.End.FirstFailedItem);
                    if (sender is null && _delivery is not null && HasChannels) sender = Task.Run(() => _delivery.RunAsync(stoppingToken), CancellationToken.None);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
                catch (Exception ex) { _log.LogWarning("Subscriptions: the pass failed: {Message}", ex.Message); }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) { }
        if (sender is not null) await Task.WhenAny(sender, Task.Delay(TimeSpan.FromSeconds(5), CancellationToken.None)).ConfigureAwait(false);
    }
}
