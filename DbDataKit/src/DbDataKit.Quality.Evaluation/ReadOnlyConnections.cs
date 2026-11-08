using System.Data.Common;
using DbDataKit.Connections;

namespace DbDataKit.Quality;

/// <summary>Opens the connection an evaluation runs over: always through the provider's read-only recipe (plan 23a D5), whatever the definition's own access says.</summary>
public static class ReadOnlyConnections
{
    /// <summary>
    /// A function for <see cref="CheckEvaluator.EvaluateAsync(IReadOnlyList{CheckDefinition}, Func{CancellationToken, Task{DbConnection}}, Func{CheckDefinition, EvaluationContext}, CancellationToken)"/>. The context is made read-only by role (<see cref="LoginRole.Read"/>),
    /// so the provider applies its recipe (keys and session statements) and the read login is the one used; the login's secrets are wiped when the connection is disposed with the function's owner (the caller disposes the login).
    /// </summary>
    public static Func<CancellationToken, Task<DbConnection>> Opener(IDbProvider provider, ConnectionDefinition definition, ResolvedLogin? login, ConnectionPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(definition);
        var context = new ConnectionContext { Definition = definition, Login = login, Policy = policy ?? ConnectionPolicy.Default, Role = LoginRole.Read };
        return ct => provider.OpenAsync(context, ct);
    }
}
