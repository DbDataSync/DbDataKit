using DbDataKit.Monitoring;
using System.Text;
using System.Text.Json;
using DbDataKit.Runner.Kit.Formats;
using DbDataKit.Runner.Kit.Testing;

namespace DbDataKit.Runner.Kit.Changes.Tests;

/// <summary>What the source says today, and what the job found.</summary>
public sealed class Feed
{
    public string Csv { get; set; } = "";

    public bool FailAfterFirstRow { get; set; }

    public List<string> Found { get; } = [];

    public IReadOnlyList<string> Deleted { get; set; } = [];

    public ChangeSet? Result { get; set; }
}

public sealed record ExportInput(Feed Feed, bool AllowEmpty = false);

public sealed class ExportProgress : Progress
{
    [Checkpoint] public RowSnapshot? Seen { get; set; }
}

[RunnerTask("export-changes")]
public sealed class ExportChanges : RunnerJob<ExportInput, ExportProgress, NoSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var feed = Input.Feed;
        feed.Found.Clear();
        var detector = new ChangeDetector(Progress.Seen, new ChangeOptions { KeyColumns = ["id"], IgnoreColumns = ["exported_at"], AllowEmptySource = Input.AllowEmpty });
        await using var stream = new MemoryStream(Encoding.UTF8.GetBytes(feed.Csv));
        await foreach (var change in detector.ChangesAsync(DelimitedReader.ReadAsync(stream), ct))
        {
            feed.Found.Add($"{change.Kind}:{change.Key}");
            if (feed.FailAfterFirstRow) throw new IOException("the source went away");
        }

        var result = detector.Complete();
        feed.Result = result;
        feed.Deleted = result.DeletedKeys;
        Progress.Seen = result.Next;
    }
}

public class ChangeDetectionTests
{
    private const string Monday = "id,name,amount,exported_at\n1,ada,10.50,2026-11-09\n2,bob,20,2026-11-09\n3,cy,30,2026-11-09\n";

    [Fact]
    public async Task A_changed_an_added_and_a_deleted_row_are_found_between_two_runs()
    {
        var feed = new Feed { Csv = Monday };
        using var sim = new RunSimulator<ExportChanges>(new ExportInput(feed));

        await sim.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(["Added:1", "Added:2", "Added:3"], feed.Found);                       // the first run has no snapshot: everything is new
        Assert.Equal(3, sim.Progress<ExportProgress>().Seen!.Count);

        // Tuesday: row 2 changed, row 3 is gone, row 4 is new, row 1 is the same (another export time only; text is compared as written, so 10.5 would not be 10.50)
        feed.Csv = "id,name,amount,exported_at\n1,ada,10.50,2026-11-10\n2,bob,25,2026-11-10\n4,dee,40,2026-11-10\n";
        await sim.RunAsync();

        Assert.Equal(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(["Changed:2", "Added:4"], feed.Found);
        Assert.Equal(["3"], feed.Deleted);
        Assert.Equal((1, 1, 1), (feed.Result!.Added, feed.Result.Changed, feed.Result.Unchanged));
        Assert.Equal(["1", "2", "4"], sim.Progress<ExportProgress>().Seen!.Rows.Keys.Order());
    }

    [Fact]
    public async Task A_rerun_with_nothing_changed_finds_nothing()
    {
        var feed = new Feed { Csv = Monday };
        using var sim = new RunSimulator<ExportChanges>(new ExportInput(feed));
        await sim.RunAsync();
        await sim.RunAsync();

        Assert.Empty(feed.Found);
        Assert.False(feed.Result!.Any);
        Assert.Equal(3, feed.Result.Unchanged);
    }

    [Fact]
    public async Task A_run_that_fails_keeps_the_old_snapshot_and_the_retry_compares_with_it_again()
    {
        var feed = new Feed { Csv = Monday };
        using var sim = new RunSimulator<ExportChanges>(new ExportInput(feed));
        await sim.RunAsync();
        var before = JsonSerializer.Serialize(sim.Progress<ExportProgress>().Seen);

        feed.Csv = "id,name,amount,exported_at\n1,ada,10.50,x\n2,bob,21,x\n";
        feed.FailAfterFirstRow = true;
        await sim.RunAsync();
        Assert.NotEqual(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(before, JsonSerializer.Serialize(sim.Progress<ExportProgress>().Seen));        // nothing was kept

        feed.FailAfterFirstRow = false;
        await sim.RunAsync();
        Assert.Equal(["Changed:2"], feed.Found);                                                  // the change is still a change
        Assert.Equal(["3"], feed.Deleted);
    }

    [Fact]
    public async Task A_source_that_gave_nothing_is_refused_not_taken_for_every_row_deleted()
    {
        var feed = new Feed { Csv = Monday };
        using var sim = new RunSimulator<ExportChanges>(new ExportInput(feed));
        await sim.RunAsync();

        feed.Csv = "id,name,amount,exported_at\n";
        await sim.RunAsync();
        Assert.NotEqual(RunOutcome.Succeeded, sim.Outcome);
        Assert.Equal(3, sim.Progress<ExportProgress>().Seen!.Count);

        var allowed = new Feed { Csv = Monday };
        using var sim2 = new RunSimulator<ExportChanges>(new ExportInput(allowed, AllowEmpty: true));
        await sim2.RunAsync();
        allowed.Csv = "id,name,amount,exported_at\n";
        await sim2.RunAsync();
        Assert.Equal(RunOutcome.Succeeded, sim2.Outcome);
        Assert.Equal(["1", "2", "3"], allowed.Deleted);
    }

    [Fact]
    public void A_key_twice_a_missing_key_and_a_snapshot_of_another_hash_are_refused_without_showing_the_key()
    {
        var detector = new ChangeDetector(null);
        detector.Observe("secret-key-1", "h");
        var twice = Assert.Throws<ChangeException>(() => detector.Observe("secret-key-1", "h"));
        Assert.DoesNotContain("secret-key-1", twice.Message);

        var row = new Row(7, 8, ["id", "name"], ["", "x"]);
        var missing = Assert.Throws<ChangeException>(() => new ChangeDetector(null, new ChangeOptions { KeyColumns = ["id"] }).Observe(row));
        Assert.Contains("Row 7", missing.Message);

        var old = new RowSnapshot { Algorithm = "md5/0" };
        Assert.Contains("md5/0", Assert.Throws<ChangeException>(() => new ChangeDetector(old)).Message);
    }

    [Fact]
    public void Too_many_deletions_are_refused_when_a_limit_is_set()
    {
        var last = new RowSnapshot();
        for (var i = 0; i < 10; i++) last.Rows[i.ToString()] = "h";
        var detector = new ChangeDetector(last, new ChangeOptions { MaxDeletedFraction = 0.5 });
        detector.Observe("0", "h");
        Assert.Throws<ChangeException>(detector.Complete);
    }

    [Fact]
    public void The_hash_tells_values_apart_that_text_would_not()
    {
        Assert.NotEqual(RowHash.Of(["a", "bc"]), RowHash.Of(["ab", "c"]));
        Assert.NotEqual(RowHash.Of([null]), RowHash.Of([""]));
        Assert.NotEqual(RowHash.Of(["5"]), RowHash.Of([5L]));
        Assert.Equal(RowHash.Of([1.50m]), RowHash.Of([1.5m]));
        Assert.Equal(RowHash.Of([new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)]), RowHash.Of([new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(2))]));
        Assert.Equal(22, RowHash.Of(["x"]).Length);
    }

    [Fact]
    public void A_snapshot_round_trips_as_json_the_way_a_checkpoint_member_stores_it()
    {
        var snapshot = new RowSnapshot();
        snapshot.Rows["k"] = RowHash.Of(["v"]);
        var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var back = JsonSerializer.Deserialize<RowSnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(snapshot.Rows, back.Rows);
        Assert.Equal(RowHash.Algorithm, back.Algorithm);
    }
}
