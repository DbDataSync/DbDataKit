using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using DbDataSync.State;
using DbDataSync.TaskRunner;

namespace DbDataSync.TaskRunner.Tests;

/// <summary>Plan 28c E17: where a pass resumes when the target and the runner's state each hold a position, and which replications keep their state on the target at all.</summary>
public sealed class PositionLogTrackingTests
{
    private static PositionLogEntry Entry(string after, long seq = 7) =>
        new(seq, Guid.NewGuid(), "r", "m", "k", null, after, null, 1, null, DateTimeOffset.UtcNow);

    [Fact]
    public void The_same_position_on_both_sides_says_nothing()
    {
        var (resume, message) = PositionLogTracking.Reconcile("p5", Entry("p5"), targetPrimary: true);
        Assert.Equal("p5", resume);
        Assert.Null(message);
    }

    [Fact]
    public void A_target_with_no_state_is_adopted_from_the_runner_and_the_log_names_the_restore_blind_spot()
    {
        var (resume, message) = PositionLogTracking.Reconcile("p5", null, targetPrimary: true);
        Assert.Equal("p5", resume);
        Assert.Contains("holds no state", message);
        Assert.Contains("restored from a backup older than its state table", message);
    }

    [Fact]
    public void Nothing_anywhere_starts_from_nothing_without_a_message()
    {
        var (resume, message) = PositionLogTracking.Reconcile(null, null, targetPrimary: true);
        Assert.Null(resume);
        Assert.Null(message);
    }

    [Fact]
    public void A_restored_target_behind_the_state_resumes_from_the_targets_position_when_it_is_primary()
    {
        var (resume, message) = PositionLogTracking.Reconcile("p9", Entry("p5"), targetPrimary: true);
        Assert.Equal("p5", resume);
        Assert.Contains("resumes from the target's position", message);
        Assert.Contains("'p9'", message);
    }

    [Fact]
    public void A_lost_runner_state_resumes_from_the_target_and_the_log_says_a_capturing_reader_reloads()
    {
        var (resume, message) = PositionLogTracking.Reconcile(null, Entry("p5"), targetPrimary: true);
        Assert.Equal("p5", resume);
        Assert.Contains("holds no position", message);
        Assert.Contains("initial load", message);
    }

    [Fact]
    public void With_the_state_primary_the_state_decides_and_the_difference_is_said()
    {
        var (resume, message) = PositionLogTracking.Reconcile("p9", Entry("p5"), targetPrimary: false);
        Assert.Equal("p9", resume);
        Assert.Contains("state is the primary", message);
    }

    private static ReplicationTaskConfig Task(TrackingConfig? tracking) => new()
    {
        Name = "r",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Periodic },
        ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = "Watermark" }, Cache = new CacheConfig { Kind = "StagingTable" }, Writer = new WriterConfig { Kind = "DeleteInsert" } },
        Tracking = tracking,
    };

    private static void NoLog(LogSeverity level, string text) { }

    [Fact]
    public void No_tracking_section_keeps_the_runners_state()
    {
        Assert.Null(PositionLogTracking.For(Task(null), new MsSqlDriver(), new MsSqlDriver().Writers.First(w => w.Kind == GenericDriverKinds.ChangeApply), NoLog));
    }

    [Theory]
    [InlineData("target", null, true)]
    [InlineData("both", "target", true)]
    [InlineData("both", "state", false)]
    public void An_asked_for_mode_on_a_supported_target_and_writer_keeps_the_state_there(string mode, string? primary, bool targetPrimary)
    {
        var driver = new MsSqlDriver();
        var tracking = PositionLogTracking.For(Task(new TrackingConfig { Mode = mode, Primary = primary }), driver, driver.Writers.First(w => w.Kind == GenericDriverKinds.ChangeApply), NoLog);
        Assert.NotNull(tracking);
        Assert.Equal(targetPrimary, tracking!.TargetPrimary);
    }

    [Fact]
    public void A_writer_that_cannot_carry_the_state_is_refused_naming_it()
    {
        var driver = new MsSqlDriver();
        var writer = driver.Writers.First(w => !w.SupportsPositionLog);
        var ex = Assert.Throws<InvalidOperationException>(() => PositionLogTracking.For(Task(new TrackingConfig { Mode = "target" }), driver, writer, NoLog));
        Assert.Contains(writer.Kind, ex.Message);
    }
}
