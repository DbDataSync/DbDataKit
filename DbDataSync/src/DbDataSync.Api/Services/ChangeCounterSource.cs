namespace DbDataSync.Api.Services;

/// <summary>
/// Fetches a change counter over a connection of the API's own connection factory: <see cref="ConnectionChangeCounterSource"/> (what the planner's gate and the engine share, plan 28c E11), opened here.
/// </summary>
public sealed class DriverChangeCounterSource(DriverConnectionFactory connections)
    : ConnectionChangeCounterSource(async (connectionName, cancellationToken) => (await connections.OpenAsync(connectionName, cancellationToken)).Connection);
