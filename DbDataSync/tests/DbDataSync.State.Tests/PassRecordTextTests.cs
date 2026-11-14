namespace DbDataSync.State.Tests;

/// <summary>A pass record is what the run history shows for a pass the runner ran (plan 28c E13, E22): it carries the pass's timing when the mapping times its passes, and a record without one reads as before.</summary>
public sealed class PassRecordTextTests
{
    private static PassRecord Record(RunTiming? timing) => new(
        Guid.NewGuid(), Guid.NewGuid(), "sales", nameof(RunKind.Primary), "orders", null, nameof(RunStatus.Succeeded),
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddSeconds(2), 3, 3, null, null, null, null, timing);

    [Fact]
    public void A_traced_pass_keeps_its_timing_through_the_text_and_into_the_run_history()
    {
        var timing = new RunTiming("MsSqlChangeTracking", 12, 40, "MsSqlStagingTable", 7, "MsSqlMerge", 9);

        var read = PassRecordText.TryParse(PassRecordText.Serialize(Record(timing)));

        Assert.Equal(timing, read!.Timing);
        Assert.Equal(timing, PassRecordText.ToRun(read).Timing);
    }

    [Fact]
    public void A_record_without_timing_reads_as_none()
    {
        var text = PassRecordText.Serialize(Record(null)).Replace(",\"timing\":null", "", StringComparison.Ordinal);

        var read = PassRecordText.TryParse(text);

        Assert.NotNull(read);
        Assert.Null(PassRecordText.ToRun(read!).Timing);
    }
}
