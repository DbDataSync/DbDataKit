using DbDataKit.Connections;
using DbDataKit.Dialect;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.ArrowFlightSql;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.Postgres;

namespace DbDataSync.Api.Services;

/// <summary>Registers the built-in engines' failure mappers with <see cref="DriverFailures"/> (plan 13, A10).</summary>
internal static class DriverFailureSetup
{
    /// <summary>The environment variable (<c>Diagnostics:DriverDetails</c> as the configuration system spells it) the worker processes read.</summary>
    public const string DetailsVariable = "Diagnostics__DriverDetails";

    public static void Configure(bool includeDetails) => DriverFailures.Configure(Mappers(), includeDetails);

    /// <summary>Every built-in engine that has a mapper. Shared with the worker process.</summary>
    public static Dictionary<EngineId, IFailureMapper> Mappers()
    {
        var mappers = new Dictionary<EngineId, IFailureMapper>
        {
            [EngineId.SqlServer] = MsSqlDriver.FailureMapper,
            [EngineId.Postgres] = PostgresDriver.FailureMapper,
        };
        var flight = new FlightSqlFailureMapper();
        foreach (var id in DriverIds.ArrowFlightSqlVariants) mappers[DriverFailures.EngineFor(id)] = flight;
        return mappers;
    }
}
