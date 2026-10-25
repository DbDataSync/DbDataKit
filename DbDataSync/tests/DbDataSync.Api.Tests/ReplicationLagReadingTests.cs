using DbDataSync.Api.Services;

namespace DbDataSync.Api.Tests;

/// <summary>A replication's lag for the shared monitoring model (plan 13, M06): the worst mapping, one figure per mapping, never an exact figure compared with an estimate.</summary>
public sealed class ReplicationLagReadingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 25, 3, 0, 0, TimeSpan.Zero);

    [Fact]
    public void No_mapping_that_can_say_gives_no_reading()
    {
        Assert.Null(ReplicationLagReading.Worst([], Now));
        Assert.Null(ReplicationLagReading.Worst([new ReaderLag("Watermark", Supported: false), new ReaderLag("MsSqlCdc", Supported: true)], Now));
    }

    [Fact]
    public void The_furthest_behind_mapping_is_the_replications_lag()
    {
        var reading = ReplicationLagReading.Worst(
            [new ReaderLag("MsSqlCdc", true, ExactLag: TimeSpan.FromSeconds(5)), new ReaderLag("MsSqlCdc", true, ExactLag: TimeSpan.FromMinutes(2))], Now);

        Assert.Equal(TimeSpan.FromMinutes(2), reading!.Behind);
        Assert.Equal(Now, reading.MeasuredAt);
    }

    [Fact]
    public void An_exact_lag_beats_an_estimate_for_the_same_mapping_and_an_estimate_stands_alone()
    {
        var both = new ReaderLag("MsSqlChangeTracking", true, ExactLag: TimeSpan.FromSeconds(3), EstimatedLag: TimeSpan.FromHours(1));
        var estimate = new ReaderLag("MsSqlChangeTracking", true, EstimatedLag: TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(3), ReplicationLagReading.Worst([both], Now)!.Behind);
        Assert.Equal(TimeSpan.FromSeconds(20), ReplicationLagReading.Worst([both, estimate], Now)!.Behind);
    }
}
