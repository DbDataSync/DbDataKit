using DbDataKit.Connections.Descriptor;
using EngineSupport = DbDataKit.Connections.Descriptor.EngineSupport;
using DbDataSync.Api.Services;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.ArrowFlightSql;
using DbDataSync.Drivers.DuckDb;
using DbDataSync.Drivers.MsSql;
using DbDataSync.Drivers.MySql;
using DbDataSync.Drivers.Oracle;
using DbDataSync.Drivers.Postgres;

namespace DbDataSync.Api.Tests;

/// <summary>Plan 14a SL04: every built-in driver is an engine of the registry, and the registry's Sync roles agree with what the driver registers (a mismatch names the engine and the role).</summary>
public class SyncEnginesTests
{
    private static DriverRegistry Registry()
    {
        var registry = new DriverRegistry();
        IDriver[] all = [new MsSqlDriver(), new PostgresDriver(), new MySqlDriver(), new OracleDriver(), new DuckDbDriver(), .. ArrowFlightSqlDriver.CreateAll()];
        foreach (var driver in all) registry.Register(driver);
        return registry;
    }

    [Fact]
    public void Each_built_in_driver_has_a_descriptor_and_the_roles_agree_with_what_it_registers()
    {
        var drivers = Registry();
        foreach (var driver in drivers.All)
        {
            var id = SyncEngines.EngineOf(driver.DriverType);
            Assert.True(id is not null, $"{driver.DriverType}: no engine of the registry (SyncEngines.EngineOf).");
            var descriptor = EngineRegistry.Builtin.Find(id!);
            Assert.True(descriptor is not null, $"{driver.DriverType}: the registry has no engine '{id}'.");
            var caps = drivers.Describe(driver.DriverType)!;
            Assert.True((caps.Readers.Count > 0) == (descriptor!.Role(EngineRoles.SyncSource).Status >= EngineSupport.Unverified), $"{id}: sync_source says {descriptor.Role(EngineRoles.SyncSource).Status}, the driver registers {caps.Readers.Count} readers.");
            Assert.True((caps.Writers.Count > 0) == (descriptor.Role(EngineRoles.SyncTarget).Status >= EngineSupport.Unverified), $"{id}: sync_target says {descriptor.Role(EngineRoles.SyncTarget).Status}, the driver registers {caps.Writers.Count} writers.");
        }
    }

    [Fact]
    public void Each_engine_the_registry_gives_a_sync_role_has_a_driver()
    {
        var drivers = Registry();
        foreach (var role in new[] { EngineRoles.SyncSource, EngineRoles.SyncTarget })
            foreach (var engine in EngineRegistry.Builtin.Supporting(role, EngineSupport.Unverified))
                Assert.True(drivers.All.Any(d => SyncEngines.EngineOf(d.DriverType) == engine.Id), $"{engine.Id}: the registry says {EngineRoles.Title(role)} is built, but Sync registers no driver for it.");
    }

    [Fact]
    public void The_roles_decide_what_the_screens_offer_and_a_driver_of_the_operators_own_is_custom()
    {
        var drivers = Registry();
        Assert.Contains(DriverIds.MsSql, SyncEngines.Offered(drivers, EngineRoles.SyncTarget));
        Assert.DoesNotContain(DriverIds.DuckDb, SyncEngines.Offered(drivers, EngineRoles.SyncTarget));
        Assert.Contains(DriverIds.DuckDb, SyncEngines.Offered(drivers, EngineRoles.SyncSource));
        Assert.DoesNotContain(DriverIds.ArrowFlightSql, SyncEngines.Offered(drivers, EngineRoles.SyncTarget));
        Assert.Contains(DriverIds.ArrowFlightSql, SyncEngines.Offered(drivers, EngineRoles.SyncSource));
        Assert.True(SyncEngines.IsCustom("SnowflakeFromAYamlFile"));
        Assert.False(SyncEngines.IsCustom(DriverIds.Postgres));
    }
}
