using DbDataKit.Connections.Descriptor;
using EngineSupport = DbDataKit.Connections.Descriptor.EngineSupport;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;

namespace DbDataSync.Api.Services;

/// <summary>
/// How Sync's drivers meet the suite's engine registry (plan 14a SL04, D25): each built-in driver is the engine of a descriptor, whose roles <c>sync_source</c> and <c>sync_target</c>
/// say what the driver may be offered as. A driver the registry does not describe (a descriptor file or a compiled plugin an operator added) is <b>custom</b>: Sync's own, shown as custom, outside the registry.
/// The drivers keep their own capabilities (the readers and writers they register); a test holds the two to each other, so a role cannot be older than the code.
/// </summary>
public static class SyncEngines
{
    /// <summary>The registry's engine id for a built-in driver id, or null when the driver is custom.</summary>
    public static string? EngineOf(string driverType) => driverType switch
    {
        DriverIds.MsSql => "sqlserver",
        DriverIds.Postgres => "postgres",
        DriverIds.MySql => "mysql",
        DriverIds.Oracle => "oracle",
        DriverIds.DuckDb => "duckdb",
        _ when DriverIds.ArrowFlightSqlVariants.Contains(driverType) => "flightsql",
        _ => null,
    };

    public static bool IsCustom(string driverType) => EngineOf(driverType) is null;

    /// <summary>Whether the driver may be offered as a source (or a target): a custom driver is offered as what it registers, a built-in one as its descriptor's role says.</summary>
    public static bool Offers(DriverRegistry drivers, string driverType, string role)
    {
        if (EngineOf(driverType) is { } engine)
            return EngineRegistry.Builtin.Find(engine)!.Role(role).Status >= EngineSupport.Unverified;
        var capabilities = drivers.Describe(driverType);
        return capabilities is not null && (role == EngineRoles.SyncSource ? capabilities.Readers.Count > 0 : capabilities.Writers.Count > 0);
    }

    /// <summary>The driver ids the screens offer for a role, in id order.</summary>
    public static IReadOnlyList<string> Offered(DriverRegistry drivers, string role) =>
        [.. drivers.All.Select(d => d.DriverType).Where(id => Offers(drivers, id, role)).Order(StringComparer.Ordinal)];
}
