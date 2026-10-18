using DbDataBuild.Execution;

namespace DbDataBuild.Tests.Unit;

public class TrackingClockTests
{
    [Fact]
    public void Timestamps_strictly_increase_even_inside_one_millisecond_and_across_threads()
    {
        var all = new System.Collections.Concurrent.ConcurrentBag<DateTime>();
        Parallel.For(0, 2000, _ => all.Add(TrackingClock.NextUtc()));
        var sorted = all.Order().ToList();
        Assert.Equal(2000, sorted.Distinct().Count());
        Assert.All(sorted, t => Assert.Equal(0, t.Ticks % TimeSpan.TicksPerMillisecond));
        Assert.All(sorted, t => Assert.Equal(DateTimeKind.Unspecified, t.Kind));
    }
}
