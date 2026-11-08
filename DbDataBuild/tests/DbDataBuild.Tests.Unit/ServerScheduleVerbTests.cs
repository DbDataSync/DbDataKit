using DbDataBuild.Cli.Server;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.State.Cli;

namespace DbDataBuild.Tests.Unit;

/// <summary>Plan 28c E09: <c>server schedule owners|observe|cutover|rollback|compare</c> and the server's start, against a runner that is faked (the verbs' own words, exit codes and files; the moves themselves are <see cref="BuildOwnershipTests"/>').</summary>
public sealed class ServerScheduleVerbTests
{
    private static async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Verb(OwnershipFixture f, FakeRunnerSchedule? runner, params string[] args)
    {
        var (o, e) = (new StringWriter(), new StringWriter());
        var context = new CliContext(o, e, TextReader.Null);
        var exit = await ScheduleVerb.RunAsync([.. args, "--repo", f.Root], context, null, runner, (_, _) => Task.CompletedTask);
        return (exit, o.ToString(), e.ToString(), context.Data);
    }

    [Fact]
    public async Task The_verbs_move_a_plan_to_the_runner_and_back_and_say_what_they_did()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();
        var runner = new FakeRunnerSchedule();

        var observe = await Verb(f, runner, "observe", "sales/sqlserver/hourly", "--runner", "prod-runner", "--channel", "prod");
        Assert.True(observe.Exit == 0, observe.Err);
        Assert.Equal(TaskOwners.Observe, observe.Data["owner"]);
        Assert.Equal(new ScheduleRunnerLink("prod-runner", "prod", null), BuildScheduleSettings.Read(f.Root).Runner);   // given once, kept in the file

        var cutover = await Verb(f, runner, "cutover", "sales/sqlserver/hourly", "--reason", "go live", "--wait", "5");
        Assert.True(cutover.Exit == 0, cutover.Err);
        Assert.Contains("scheduled by the runner from", cutover.Out);
        Assert.Equal((TaskOwners.Observe, TaskOwners.Runner, "sqlserver-hourly"), (cutover.Data["from"], cutover.Data["owner"], cutover.Data["task"]));
        Assert.Equal("go live", runner.Told.Last().Reason);

        var owners = await Verb(f, runner, "owners");
        Assert.Equal(0, owners.Exit);
        Assert.Contains("this server: runner", owners.Out);

        var rollback = await Verb(f, runner, "rollback", "sales/sqlserver/hourly");
        Assert.True(rollback.Exit == 0, rollback.Err);
        Assert.Equal(TaskOwners.Old, f.OwnerInFile("hourly"));
        Assert.Equal(["schedule.observe", "schedule.cutover", "schedule.rollback"], new DbDataKit.Web.Trust.FileAuditLog(Path.Combine(f.Root, "management-audit.log")).Read().Select(a => a.Command));
    }

    [Fact]
    public async Task A_refusal_is_the_verbs_exit_one_with_the_words_and_a_wrong_use_is_exit_two()
    {
        using var f = new OwnershipFixture();
        f.Scheduler.Tick();

        var noRunner = await Verb(f, new FakeRunnerSchedule(), "cutover", "sales/sqlserver/hourly");
        Assert.Equal(CliExit.Usage, noRunner.Exit);
        Assert.Contains("Say where the runner is", noRunner.Err);

        var inactive = await Verb(f, new FakeRunnerSchedule { Version = null }, "cutover", "sales/sqlserver/hourly", "--runner", "r", "--channel", "prod");
        Assert.Equal(CliExit.Failure, inactive.Exit);
        Assert.Contains("no active version", inactive.Err);
        Assert.Equal(TaskOwners.Old, f.OwnerInFile("hourly"));

        Assert.Equal(CliExit.Usage, (await Verb(f, new FakeRunnerSchedule(), "cutover", "sales/hourly", "--runner", "r", "--channel", "prod")).Exit);
        Assert.Equal(CliExit.Usage, (await Verb(f, new FakeRunnerSchedule(), "cutover", "--runner", "r", "--channel", "prod")).Exit);
        Assert.Equal(CliExit.Usage, (await Verb(f, new FakeRunnerSchedule(), "cutover", "sales/sqlserver/hourly", "--runner", "r", "--channel", "prod", "--wait", "soon")).Exit);
        Assert.Equal(CliExit.Usage, (await Verb(f, new FakeRunnerSchedule(), "flip", "sales/sqlserver/hourly")).Exit);
    }

    [Fact]
    public async Task Compare_says_how_many_decisions_agree_and_fails_on_a_difference()
    {
        using var f = new OwnershipFixture();
        var runner = new FakeRunnerSchedule();
        var at = OwnershipFixture.At(25, 12, 15);
        runner.Decisions.AddRange([new ClientDecisionBuilder("runner", at).Make(), new ClientDecisionBuilder("old", at.AddSeconds(5)).Make(), new ClientDecisionBuilder("runner", at.AddHours(1)).Make()]);

        var result = await Verb(f, runner, "compare", "sales/sqlserver/hourly", "--runner", "r", "--channel", "prod");

        Assert.Equal(CliExit.Failure, result.Exit);
        Assert.Contains("1 decision(s) agree, 1 differ.", result.Out);
        Assert.Equal(1, result.Data["matched"]);
    }

    private sealed record ClientDecisionBuilder(string Source, DateTimeOffset At)
    {
        public DbDataKit.Runner.Client.ClientDecision Make() => new(Source, At, 0, false);
    }

    [Fact]
    public async Task A_schedule_section_that_is_not_valid_stops_the_verbs_and_the_start_naming_the_file()
    {
        using var f = new OwnershipFixture();
        File.WriteAllText(Path.Combine(f.Root, "server.yaml"), "instanceId: build-1\nschedule:\n  owners:\n    sales/sqlserver/hourly: runnr\n");

        var verb = await Verb(f, new FakeRunnerSchedule(), "owners");
        Assert.Equal(StateExit.Settings, verb.Exit);
        Assert.Contains("server.yaml", verb.Err);
        Assert.Contains("did you mean 'runner'?", verb.Err);

        var (o, e) = (new StringWriter(), new StringWriter());
        var start = await ServerCli.RunAsync(["server", "run", "--scheduler", "--repo", f.Root, "--listen", "https://127.0.0.1:0"], o, e, TextReader.Null, _ => null);
        Assert.Equal(StateExit.Settings, start);
        Assert.Contains("server.yaml", e.ToString());
        Assert.Contains("is 'runnr', which is not an owner", e.ToString());
    }
}
