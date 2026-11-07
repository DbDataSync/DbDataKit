using DbDataKit.Runner.Scheduler;
using DbDataKit.Scheduling;
using static DbDataKit.Runner.Tests.SchedulerHarness;

namespace DbDataKit.Runner.Tests;

/// <summary>The text of a projection row's schedule and limits, and the work item's payload (plan 28a R09).</summary>
public sealed class ProjectionDocumentsTests
{
    [Fact]
    public void A_schedule_and_limits_round_trip_through_their_text()
    {
        var cron = new Schedule { Cron = "0 2 * * *", TimeZoneId = "Europe/Berlin", Misfire = MisfirePolicy.Skip, MisfireGrace = TimeSpan.FromMinutes(5) };
        Assert.Equal(cron, ProjectionDocuments.ParseSchedule(ProjectionDocuments.ScheduleText(cron)));
        var every = Schedule.Interval(TimeSpan.FromSeconds(90));
        Assert.Equal(every, ProjectionDocuments.ParseSchedule(ProjectionDocuments.ScheduleText(every)));
        Assert.Null(ProjectionDocuments.ScheduleText(new Schedule()));
        Assert.False(ProjectionDocuments.ParseSchedule(null).HasSchedule);

        var limits = new TaskLimits(TimeSpan.FromMinutes(30), 512, 1.5m, 2, 3, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
        Assert.Equal((limits, true), ProjectionDocuments.ParseLimits(ProjectionDocuments.LimitsText(limits, true)));
        Assert.Equal((TaskLimits.None, false), ProjectionDocuments.ParseLimits(null));
    }

    [Theory]
    [InlineData("""{"every":"15 minutes"}""", "schedule.every")]
    [InlineData("""{"every":"5m","cron":"* * * * *"}""", "either an interval or a cron")]
    [InlineData("""{"evry":"5m"}""", "schedule.evry is not a key")]
    [InlineData("""{"cron":"* * * * *","time_zone":"Mars/Olympus"}""", "time zone")]
    [InlineData("""{"cron":"* * * * *","misfire":"later"}""", "run_once or skip")]
    [InlineData("[1]", "JSON object")]
    public void A_schedule_that_does_not_read_says_which_key_is_wrong(string text, string expected) =>
        Assert.Contains(expected, Assert.Throws<FormatException>(() => ProjectionDocuments.ParseSchedule(text)).Message);

    [Theory]
    [InlineData("""{"attempts":0}""", "limits.attempts is above zero")]
    [InlineData("""{"timeout":"0s"}""", "limits.timeout is above zero")]
    [InlineData("""{"concurency":2}""", "limits.concurency is not a key")]
    [InlineData("""{"cpu":"lots"}""", "limits.cpu is a number")]
    [InlineData("""{"allow_overlap":"yes"}""", "true or false")]
    public void Limits_that_do_not_read_say_which_key_is_wrong(string text, string expected) =>
        Assert.Contains(expected, Assert.Throws<FormatException>(() => ProjectionDocuments.ParseLimits(text)).Message);

    [Fact]
    public void Rows_are_built_from_a_manifests_tasks()
    {
        var manifest = Manifest(Package,
            Def("nightly", Schedule.OnCron("0 2 * * *"), new TaskLimits(Concurrency: 2, Attempts: 3), allowOverlap: true),
            Def("by-hand"));

        var rows = ProjectionDocuments.RowsOf(manifest, "7");

        Assert.Equal(["nightly", "by-hand"], rows.Select(r => r.Task));
        Assert.All(rows, r => Assert.Equal("7", r.Version));
        Assert.Equal(("0 2 * * *", true, 3), (ProjectionDocuments.ParseSchedule(rows[0].Schedule).Cron, ProjectionDocuments.ParseLimits(rows[0].Limits).AllowOverlap, ProjectionDocuments.ParseLimits(rows[0].Limits).Limits.Attempts));
        Assert.Null(rows[1].Schedule);
    }

    [Fact]
    public void A_payload_round_trips_and_an_unreadable_one_says_so()
    {
        var payload = new WorkPayload { Task = "pkg/prod/job", Version = "3", Chain = "c", Attempt = 2, Started = true, Inputs = { ["a"] = "b" } };
        var back = WorkPayload.TryParse(payload.ToJson(), out _)!;
        Assert.Equal(("pkg/prod/job", "3", "c", 2, true, "b"), (back.Task, back.Version, back.Chain, back.Attempt, back.Started, back.Inputs["a"]));
        Assert.Null(WorkPayload.TryParse("nonsense", out var problem));
        Assert.Contains("not readable", problem);
        Assert.Null(WorkPayload.TryParse(null, out problem));
        Assert.Null(WorkPayload.TryParse("""{"task":"not a ref","version":"1","chain":"c"}""", out problem));
    }
}
