using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>Plan 28c CW02: which readers can say that a primary key moved (the Pipeline screen shows the note, the docs state it). No database involved.</summary>
public sealed class ReaderKeyChangeCapabilityTests
{
    [Fact]
    public void OnlyTheWatermarkScan_CannotSeeAKeyChange_OfTheReadersThisDriverRegisters()
    {
        var registry = new DriverRegistry();
        registry.Register(new MsSqlDriver());

        var readers = registry.Describe(DriverIds.MsSql)!.Readers;

        Assert.Equal([GenericDriverKinds.Watermark], readers.Where(r => !r.DetectsKeyChanges).Select(r => r.Kind));
        // the ones that say it: the change feeds (Change Tracking, CDC, the audit trigger) and the reloads
        Assert.All(
            readers.Where(r => r.Kind is "MsSqlChangeTracking" or "MsSqlCdc" or GenericDriverKinds.TriggerAudit or "BatchReload"),
            r => Assert.True(r.DetectsKeyChanges));
    }
}
