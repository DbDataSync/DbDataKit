using DbDataKit.Hosting;
using DbDataKit.Serving;
using DbDataKit.Serving.FlightSql;
using DbDataKit.State;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Hosting;

/// <summary>How a module of the Gateway reports itself on the host's <c>/health</c> (plan 20 O18): not started is down, draining or a back-end it needs that does not answer is what the module says, and nothing else about it is looked at.</summary>
public static class ServingModuleHealth
{
    /// <summary>The module's own state first (not started, draining), then the group's state database, which every module reads: when it does not answer the line is <paramref name="whenStateIsDown"/>.</summary>
    public static ServingHealth Of(bool started, bool draining, StateDatabase? state, ServingHealthState whenStateIsDown)
    {
        if (!started) return new ServingHealth(ServingHealthState.Down, "not started");
        if (draining) return new ServingHealth(ServingHealthState.Degraded, "draining");
        if (state is not null && Unreachable(state) is { } problem) return new ServingHealth(whenStateIsDown, $"the state database does not answer: {problem}");
        return ServingHealth.Healthy;
    }

    private static string? Unreachable(StateDatabase state)
    {
        try
        {
            using var connection = state.OpenConnection();
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ex.GetType().Name;
        }
    }
}

/// <summary>A module's <see cref="IServingModule.Health"/> among the Gateway's readiness components, under the module's name.</summary>
public sealed class ServingModuleReadinessProbe(IServingModule module, Func<ServingHealth>? health = null) : IReadinessProbe
{
    public string Name => module.Name;

    public Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken)
    {
        var reported = health?.Invoke() ?? module.Health();
        return Task.FromResult(new ComponentHealth(Name, reported.State switch
        {
            ServingHealthState.Ok => HealthStatus.Healthy,
            ServingHealthState.Degraded => HealthStatus.Degraded,
            _ => HealthStatus.Unhealthy,
        }, reported.Detail));
    }
}
