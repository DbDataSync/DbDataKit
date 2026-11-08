using DbDataBuild.Cli.Server;
using DbDataKit.State;

namespace DbDataBuild.Tests.Unit;

/// <summary>Plan 28c E09: the <c>schedule:</c> section of the Build server's <c>server.yaml</c>, read strictly and edited without touching what else the file says.</summary>
public sealed class BuildScheduleSettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "ddb-owners-" + Guid.NewGuid().ToString("N")[..8]);

    public BuildScheduleSettingsTests() => Directory.CreateDirectory(dir);

    public void Dispose() { try { Directory.Delete(dir, true); } catch (IOException) { } }

    private string File_ => Path.Combine(dir, "server.yaml");

    private void Write(string text) => File.WriteAllText(File_, text);

    [Fact]
    public void No_file_and_no_section_are_the_default_where_every_plan_is_this_servers()
    {
        Assert.Same(BuildScheduleSettings.Default, BuildScheduleSettings.Read(dir));
        Write("instanceId: build-1\n");
        var settings = BuildScheduleSettings.Read(dir);
        Assert.Equal(TaskOwners.Old, settings.OwnerOf("sales", "sqlserver", "hourly"));
        Assert.Null(settings.Runner);
    }

    [Fact]
    public void A_section_is_read_with_the_runner_and_the_owners()
    {
        Write("instanceId: build-1\nschedule:\n  runner: { server: prod-runner, channel: prod, client_dir: /var/client }\n  owners:\n    sales/sqlserver/hourly: runner\n    sales/sqlserver/nightly: observe\n");

        var settings = BuildScheduleSettings.Read(dir);

        Assert.Equal(new ScheduleRunnerLink("prod-runner", "prod", "/var/client"), settings.Runner);
        Assert.Equal(TaskOwners.Runner, settings.OwnerOf("sales", "sqlserver", "hourly"));
        Assert.Equal(TaskOwners.Observe, settings.OwnerOf("sales", "sqlserver/nightly"));
        Assert.Equal(TaskOwners.Old, settings.OwnerOf("sales", "sqlserver", "other"));
        Assert.Equal(TaskOwners.Old, settings.OwnerOf("other", "sqlserver", "hourly"));
    }

    [Theory]
    [InlineData("schedule:\n  owner: {}\n", "schedule.owner is not a schedule setting", "did you mean 'owners'?")]
    [InlineData("schedule:\n  runner: { server: r, channel: c, chanel: x }\n", "schedule.runner.chanel is not a runner setting", "did you mean 'channel'?")]
    [InlineData("schedule:\n  runner: { server: r, channel: c }\n  owners:\n    sales/sqlserver/hourly: runnr\n", "sales/sqlserver/hourly is 'runnr', which is not an owner", "did you mean 'runner'?")]
    [InlineData("schedule:\n  runner: { server: r, channel: c }\n  owners:\n    hourly: runner\n", "'hourly' is not a plan", "<project>/<connection>/<plan>")]
    [InlineData("schedule:\n  owners:\n    sales/sqlserver/hourly: runner\n", "needs schedule.runner (server and channel)", "where the runner is")]
    [InlineData("schedule:\n  runner: { server: r }\n", "schedule.runner.channel is missing", "")]
    [InlineData("schedule: runner\n", "'schedule' has to be a set of keys", "")]
    public void A_section_that_is_not_valid_is_refused_naming_the_file_and_the_key(string yaml, string message, string hint)
    {
        Write(yaml);

        var refused = Assert.Throws<InvalidOperationException>(() => BuildScheduleSettings.Read(dir));

        Assert.StartsWith(File_, refused.Message);
        Assert.Contains(message, refused.Message);
        Assert.Contains(hint, refused.Message);
    }

    [Fact]
    public void Writing_an_owner_adds_the_runner_and_the_line_and_keeps_every_other_line_and_comment()
    {
        var before = "# my server\ninstanceId: build-1\nstate:\n  engine: sqlite   # as it was\ntracking:\n  mode: both\n";
        Write(before);

        BuildScheduleSettings.Write(dir, "sales", "sqlserver", "hourly", TaskOwners.Observe, new ScheduleRunnerLink("prod-runner", "prod", null));
        BuildScheduleSettings.Write(dir, "sales", "sqlserver", "nightly", TaskOwners.Runner);
        BuildScheduleSettings.Write(dir, "sales", "sqlserver", "hourly", TaskOwners.Runner);

        var text = File.ReadAllText(File_);
        Assert.StartsWith(before.TrimEnd(), text);
        var settings = BuildScheduleSettings.Read(dir);
        Assert.Equal(new ScheduleRunnerLink("prod-runner", "prod", null), settings.Runner);
        Assert.Equal((TaskOwners.Runner, TaskOwners.Runner), (settings.OwnerOf("sales", "sqlserver", "hourly"), settings.OwnerOf("sales", "sqlserver", "nightly")));
        Assert.Contains("engine: sqlite   # as it was", text);
        Assert.Contains("tracking:", text);

        BuildScheduleSettings.Write(dir, "sales", "sqlserver", "hourly", TaskOwners.Old);
        Assert.Matches("sales/sqlserver/hourly[\"']?: old", File.ReadAllText(File_));            // put back, and the file says so
        Assert.Equal(TaskOwners.Old, BuildScheduleSettings.Read(dir).OwnerOf("sales", "sqlserver", "hourly"));
    }

    [Fact]
    public void Writing_an_owner_that_needs_a_runner_when_there_is_none_changes_nothing_and_names_the_file()
    {
        Write("instanceId: build-1\n");

        var refused = Assert.Throws<InvalidOperationException>(() => BuildScheduleSettings.Write(dir, "sales", "sqlserver", "hourly", TaskOwners.Runner));

        Assert.StartsWith(File_, refused.Message);
        Assert.Equal("instanceId: build-1\n", File.ReadAllText(File_));
        Assert.Throws<InvalidOperationException>(() => BuildScheduleSettings.Write(dir, "sales", "sql.server", "hourly", TaskOwners.Old));   // a dot would be read as a level of the file
    }

    [Fact]
    public void A_file_with_no_server_yaml_yet_is_made_with_the_section()
    {
        BuildScheduleSettings.Write(dir, "sales", "sqlserver", "hourly", TaskOwners.Observe, new ScheduleRunnerLink("r", "c", null));

        Assert.Equal(TaskOwners.Observe, BuildScheduleSettings.Read(dir).OwnerOf("sales", "sqlserver", "hourly"));
    }
}
