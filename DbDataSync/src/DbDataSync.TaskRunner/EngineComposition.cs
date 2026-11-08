using DbDataKit.Connections;
using DbDataKit.Dialect;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.ArrowFlightSql;
using DbDataSync.Drivers.Descriptor;
using DbDataSync.Drivers.DuckDb;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.MySql;
using DbDataSync.Drivers.Oracle;
using DbDataSync.Drivers.Postgres;
using DbDataSync.Libraries;
using DbDataSync.Scripting;

namespace DbDataSync.TaskRunner;

/// <summary>What an engine process runs on: the config it reads, the drivers and libraries it opens connections through, and the script host.</summary>
internal sealed record EngineServices(ConfigRepository Config, DriverRegistry Drivers, LibraryRegistry Libraries, ScriptHost Scripts);

/// <summary>
/// The composition both ways of starting the engine share (plan 28c E03): the worker the API spawns (<see cref="WorkerEntry"/>) and the drain a runner starts (<see cref="DrainJob"/>). Drivers, libraries and scripts are
/// composed the same way whoever starts the process; what differs is where state comes from (loopback to the API, or the runner's protocol) and where the engine may write (the script cache).
/// </summary>
internal static class EngineComposition
{
    public static EngineServices Compose(ConfigRepository configRepository, string repoRoot, ScriptCacheDirectory scriptCache)
    {
        // The cache is what makes scripting affordable here: this process is spawned per run, so without it
        // every pass would start a compiler before compiling anything of ours.
        var scriptHost = new ScriptHost(configRepository, new ScriptCompiler(scriptCache, configRepository.ScriptsDirectory));

        // Loaded before DriverRegistry for the same reason the API loads it first: a descriptor or compiled
        // driver registered from 109d/109e on resolves its library through this. The worker is a separate
        // process per the plan doc's "two composition roots" note, so it repeats this rather than sharing the
        // API's in-memory registry.
        var libraryRegistry = new LibraryRegistry(repoRoot).LoadAll();

        // A driver's failure is shown as its classified message; the API process hands the details setting down in the environment.
        var failureMappers = new Dictionary<EngineId, IFailureMapper>
        {
            [EngineId.SqlServer] = MsSqlDriver.FailureMapper,
            [EngineId.Postgres] = PostgresDriver.FailureMapper,
            [EngineId.MySql] = MySqlDriver.FailureMapper,
            [EngineId.Oracle] = OracleDriver.FailureMapper,
        };
        foreach (var id in DriverIds.ArrowFlightSqlVariants)
            failureMappers[DriverFailures.EngineFor(id)] = new DbDataKit.Connections.FlightSql.FlightSqlFailureMapper();
        DriverFailures.Configure(
            failureMappers,
            string.Equals(Environment.GetEnvironmentVariable("Diagnostics__DriverDetails"), "true", StringComparison.OrdinalIgnoreCase));

        var driverRegistry = new DriverRegistry();
        // The scripted reader is composed here rather than inside a driver, because it needs the script host
        // and a driver must not depend on Roslyn.
        var msSqlDriver = new MsSqlDriver();
        var postgresDriver = new PostgresDriver();
        var mySqlDriver = new MySqlDriver();
        var oracleDriver = new OracleDriver();
        var duckDbDriver = new DuckDbDriver();
        driverRegistry.RegisterWithScripting(msSqlDriver, scriptHost);
        driverRegistry.RegisterWithScripting(postgresDriver, scriptHost);
        driverRegistry.RegisterWithScripting(mySqlDriver, scriptHost);
        driverRegistry.RegisterWithScripting(oracleDriver, scriptHost);
        driverRegistry.RegisterWithScripting(duckDbDriver, scriptHost);
        // One driver per SQL dialect (phase 197F); like DuckDb they name a dialect but supply no catalog, so
        // they get no ScriptedQuery reader out of the same call.
        foreach (var flightSqlDriver in ArrowFlightSqlDriver.CreateAll())
            driverRegistry.RegisterWithScripting(flightSqlDriver, scriptHost);
        DriverLoader.LoadDescriptorDrivers(repoRoot, libraryRegistry, driverRegistry);
        DriverLoader.LoadCompiledDrivers(repoRoot, driverRegistry, cacheRoot: Path.Combine(scriptCache.Root, "drivers"));

        return new EngineServices(configRepository, driverRegistry, libraryRegistry, scriptHost);
    }
}
