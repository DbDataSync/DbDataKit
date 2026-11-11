using DbDataKit.Runner.Scheduler;
using DbDataKit.State;

namespace DbDataKit.Runner.Tests;

/// <summary>Plan 28c E01: a run started by a schedule or a trigger gives no inputs, so the manifest's default of each declared input is what the task gets; a value the run gave wins.</summary>
public sealed class InputDefaultsTests
{
    private static readonly TaskInput[] Declared =
    [
        new("connection", ProgressType.Text, Required: true, Default: "warehouse"),
        new("plan", ProgressType.Text, Default: "daily"),
        new("note", ProgressType.Text),
    ];

    [Fact]
    public void A_run_with_no_inputs_gets_the_defaults_of_the_declared_inputs()
    {
        var inputs = RunnerScheduler.WithDefaults(Declared, new Dictionary<string, string>());

        Assert.Equal(new Dictionary<string, string> { ["connection"] = "warehouse", ["plan"] = "daily" }, inputs);
    }

    [Fact]
    public void A_value_the_run_gave_wins_over_the_default()
    {
        var inputs = RunnerScheduler.WithDefaults(Declared, new Dictionary<string, string> { ["plan"] = "hourly" });

        Assert.Equal(new Dictionary<string, string> { ["connection"] = "warehouse", ["plan"] = "hourly" }, inputs);
    }

    [Fact]
    public void Inputs_without_a_default_stay_absent_and_the_given_ones_are_returned_as_they_are()
    {
        var given = new Dictionary<string, string> { ["connection"] = "other", ["plan"] = "x" };

        Assert.Same(given, RunnerScheduler.WithDefaults(Declared, given));
        Assert.Empty(RunnerScheduler.WithDefaults([new TaskInput("note", ProgressType.Text)], new Dictionary<string, string>()));
    }
}
