using Grpc.Core;

namespace DbDataKit.Serving.FlightSql;

/// <summary>
/// The Flight SQL module (plan 20; plan 19, N02): an endpoint of the serving layer that speaks the Arrow Flight SQL protocol over the core services it is started with. The host starts it with its services, adds
/// <see cref="FlightAuthInterceptor"/> to its gRPC pipeline and <see cref="ServingFlightServer"/> as the Flight service, and registers this object as a singleton so both find it; it stops taking new calls when drained.
/// </summary>
public sealed class FlightSqlModule(FlightSqlOptions options, TimeProvider? time = null) : IServingModule, IAsyncDisposable
{
    private IServingServices? _services;
    private volatile bool _draining;

    public const string ModuleName = "flightsql";

    public string Name => ModuleName;

    public FlightSqlOptions Options { get; } = options;

    public PendingAnswerStore Pending { get; } = new(time ?? TimeProvider.System);

    public PreparedStatementStore Prepared { get; } = new(time ?? TimeProvider.System);

    /// <summary>The core services the module was started with.</summary>
    public IServingServices Services => _services ?? throw new RpcException(new Status(StatusCode.Unavailable, "The Flight SQL module is not running."));

    public Task StartAsync(IServingServices services, CancellationToken cancellationToken = default)
    {
        _services = services;
        _draining = false;
        return Task.CompletedTask;
    }

    /// <summary>Refuses a call that arrives after the module began draining or before it started; a client retries it against another instance.</summary>
    public void EnsureServing()
    {
        if (_services is null || _draining) throw new RpcException(new Status(StatusCode.Unavailable, _draining ? "This server is shutting down; try another instance." : "The Flight SQL module is not running."));
    }

    public Task DrainAsync(CancellationToken cancellationToken = default)
    {
        _draining = true;
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        _draining = true;
        await Pending.DisposeAsync().ConfigureAwait(false);
        _services = null;
    }

    public ServingHealth Health() => _services is null ? new ServingHealth(ServingHealthState.Down, "not started") : _draining ? new ServingHealth(ServingHealthState.Degraded, "draining") : ServingHealth.Healthy;

    /// <summary>Expired pending results and idle prepared statements are dropped; the host calls this on a timer.</summary>
    public async Task SweepAsync()
    {
        await Pending.SweepAsync().ConfigureAwait(false);
        Prepared.Sweep();
    }

    public ValueTask DisposeAsync() => new(StopAsync());
}
