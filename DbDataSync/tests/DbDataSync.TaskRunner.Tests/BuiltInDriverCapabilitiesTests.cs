using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Scripting;
using Xunit;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>
/// The built-in drivers' capability table (<see cref="BuiltInDriverCapabilities"/>) is what a screen and the Workspace's offline answers read in
/// place of the drivers. These tests hold it equal to what each built-in driver registers the way every run does
/// (<see cref="EngineComposition.RegisterBuiltInDrivers"/>): its readers, staging providers and writers in order, their settings, the key
/// each needs and the changes each writer makes, and the driver's own answers.
/// </summary>
public sealed class BuiltInDriverCapabilitiesTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private readonly string root = Directory.CreateTempSubdirectory("ddsync-capabilities-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(root, true); } catch (IOException) { }
    }

    private DriverRegistry Registered()
    {
        var repository = ConfigRepository.ReadOnly(Path.Combine(root, "config"), SecretStore.ForProviders());
        var registry = new DriverRegistry();
        EngineComposition.RegisterBuiltInDrivers(registry, Scripting.ForTests(repository, root));
        return registry;
    }

    [Fact]
    public void The_table_lists_every_built_in_driver_and_no_other()
    {
        var registered = Registered().All.Select(d => d.DriverType).Order(StringComparer.Ordinal).ToList();
        var table = BuiltInDriverCapabilities.All.Select(d => d.DriverType).Order(StringComparer.Ordinal).ToList();

        Assert.Equal(registered, table);
    }

    [Fact]
    public void Each_driver_registers_what_the_table_says()
    {
        var registry = Registered();
        foreach (var expected in BuiltInDriverCapabilities.All)
        {
            var actual = registry.Describe(expected.DriverType);
            Assert.NotNull(actual);
            Assert.Equal(JsonSerializer.Serialize(expected, Json), JsonSerializer.Serialize(actual, Json));
        }
    }

    [Theory]
    [InlineData(DriverIds.MsSql, "MsSqlChangeTracking", KeyNeed.SourceKey)]
    [InlineData(DriverIds.MsSql, "MsSqlCdc", KeyNeed.None)]
    [InlineData(DriverIds.Postgres, "PgLogicalSlot", KeyNeed.SourceKey)]
    [InlineData(DriverIds.Postgres, "TriggerAudit", KeyNeed.SourceKey)]
    [InlineData(DriverIds.Postgres, "Watermark", KeyNeed.None)]
    public void A_reader_says_which_key_it_needs(string driver, string kind, KeyNeed key) =>
        Assert.Equal(key, Registered().FindReader(driver, kind)!.NeedsKey);

    [Theory]
    [InlineData(DriverIds.MsSql, "MsSqlMerge", KeyNeed.TargetKey)]
    [InlineData(DriverIds.MsSql, "MsSqlDeleteInsert", KeyNeed.None)]
    [InlineData(DriverIds.MsSql, "ChangeApply", KeyNeed.TargetKey)]
    [InlineData(DriverIds.MySql, "Scd2", KeyNeed.NaturalKey)]
    public void A_writer_says_which_key_it_needs(string driver, string kind, KeyNeed key) =>
        Assert.Equal(key, Registered().Get(driver).Writers.Single(w => w.Kind == kind).NeedsKey);

    [Theory]
    [InlineData(DriverIds.MsSql, "MsSqlMerge", WritePrivileges.All)]
    [InlineData(DriverIds.MsSql, "MsSqlDeleteInsert", WritePrivileges.Insert | WritePrivileges.Delete)]
    [InlineData(DriverIds.MsSql, "MsSqlBulkInsert", WritePrivileges.Insert | WritePrivileges.Delete)]
    [InlineData(DriverIds.Postgres, "DeleteInsert", WritePrivileges.Insert | WritePrivileges.Delete)]
    [InlineData(DriverIds.Postgres, "ChangeApply", WritePrivileges.All)]
    [InlineData(DriverIds.Postgres, "KeyReconcileDelete", WritePrivileges.Delete)]
    [InlineData(DriverIds.MySql, "KeyReconcileScd2Close", WritePrivileges.Update)]
    [InlineData(DriverIds.MySql, "Scd2", WritePrivileges.Insert | WritePrivileges.Update)]
    [InlineData(DriverIds.Oracle, "Snapshot", WritePrivileges.Insert)]
    public void A_writer_says_which_changes_it_makes(string driver, string kind, WritePrivileges privileges) =>
        Assert.Equal(privileges, Registered().Get(driver).Writers.Single(w => w.Kind == kind).NeedsPrivileges);
}
