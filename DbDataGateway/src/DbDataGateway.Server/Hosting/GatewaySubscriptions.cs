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
/// views module is on <b>and this instance can export</b> (<c>views.export</c>): an instance that could only fail a due subscription (no browser) does not claim it, so it waits for a process that can.
/// <para><b>Channels.</b> The Gateway has no delivery channels of its own (a channel name is the console's binding and connection: plan 22a GP18, plan 27a), and needs none: it knows a channel by the <b>kind the console stored on the subscription</b>
/// (<see cref="EnqueueOnlyChannels"/>), draws the document as the subscriber and <b>writes the message to the shared outbox</b> (the <c>delivery</c> module of the group's state, the same one the console's sender works through); the console's sender claims it and sends it through its
/// own channel, so the Gateway never holds a connection or a secret and does not run a sender. A host that registers real <see cref="IDeliveryChannels"/> (a test, a future install with its own) sends the outbox here as well.</para>
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

    /// <summary>Whether the host registered delivery channels of its own: only then does this instance send the outbox. Without them it only enqueues for the process that has the channels.</summary>
    public bool HasChannels => _channels is not null;

    /// <summary>The subscriptions of the Gateway's state.</summary>
    public SubscriptionStore Subscriptions => new(_state);

    /// <summary>The delivery service the task hands its documents to (composed on the first pass).</summary>
    public DeliveryService? Delivery => _delivery;

    private ViewsExportTasks? Compose()
    {
        if (_tasks is not null) return _tasks;
        if (_views.Module.Api is not { } api) return null;                           // the module has not started
        var renderer = new SubscriptionRenderer(api, _views.Publications, new HomeSubscriberPrincipals(_views));
        _delivery = new DeliveryService(new DeliveryStore(_state), new PayloadStore(_state), _channels ?? new EnqueueOnlyChannels(() => Subscriptions.ChannelKinds()), DeliveryPolicy.Default, new SecretScrubber(), _time, null, RunService);
        var service = new SubscriptionService(Subscriptions, _delivery, renderer, new HomeSubscriberDirectory(_views), _time);
        return _tasks = new ViewsExportTasks(service, _state, RunService);
    }

    /// <summary>One pass of the task, or null when this instance does not draw subscriptions (no exporter, or the views module is not started).</summary>
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
