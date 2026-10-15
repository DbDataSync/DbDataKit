using System.Text.Json.Serialization;

namespace DbData.Hosting;

/// <summary>Body of <c>GET /version</c>. The updater's post-update check polls this.</summary>
public sealed record VersionInfo(
    string Product,
    string Version,
    string? Commit,
    IReadOnlyDictionary<string, string> SchemaVersions,
    bool Ready);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HealthStatus
{
    Healthy,
    Degraded,
    Unhealthy,
}

public sealed record ComponentHealth(string Name, HealthStatus Status, string? Detail = null);

/// <summary>Body of <c>GET /health</c>: liveness is "the process answers", readiness is "components are usable".</summary>
public sealed record HealthReport(
    HealthStatus Status,
    bool Live,
    bool Ready,
    bool Draining,
    IReadOnlyList<ComponentHealth> Components)
{
    public static HealthReport From(IReadOnlyList<ComponentHealth> components, bool draining)
    {
        ArgumentNullException.ThrowIfNull(components);
        var worst = components.Count == 0 ? HealthStatus.Healthy : components.Max(c => c.Status);
        return new HealthReport(worst, true, worst != HealthStatus.Unhealthy && !draining, draining, components);
    }
}

/// <summary>A readiness component (database reachable, certificate not expired, ...).</summary>
public interface IReadinessProbe
{
    string Name { get; }

    Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken);
}
