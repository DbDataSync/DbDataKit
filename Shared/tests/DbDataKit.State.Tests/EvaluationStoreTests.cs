using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 27a AG23: the evaluation tables of the <c>agents</c> module on every state engine (SQLite always; SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> and <c>DBDATAKIT_TEST_POSTGRES</c> name them): a run is kept whole with its
/// cases, listed newest first per agent and channel, the latest run of exactly one version is found, and the minimum score before a promotion is a recorded choice that can be changed and removed.
/// </summary>
public sealed class EvaluationStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T09:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private EvaluationStore Open(string engine, StateTestClock? clock = null) => new(_engines.Provision(engine).Open([AgentsModule.Module], time: clock ?? new StateTestClock(Start)));

    private static EvaluationRunRow Run(string id, string agent = "sales-helper", string version = "3", string channel = "test", decimal score = 0.75m, DateTimeOffset? at = null) =>
        new(id, agent, version, channel, "anthropic/claude", """["policy/refunds.md@v3"]""", "manual", score, 1, 2, 0.0425m, at ?? Start, (at ?? Start).AddSeconds(30),
        [
            new EvaluationCaseRow(id, "revenue", "Revenue by region?", 1.0m, true, "EMEA 120.5", """[{"kind":"answer-contains","method":"suite","score":1}]""", 1200, 300, 0.0123m, null),
            new EvaluationCaseRow(id, "margin", "Margin?", 0.5m, false, "", "[]", 0, 0, 0.0302m, "The agent could not be run on this case: the model is down"),
        ]);

    [Fact]
    public void The_module_follows_the_conventions_and_has_the_evaluation_tables()
    {
        Assert.Empty(StateConventions.Violations(AgentsModule.Module));
        var tables = StateTableDefinitions.Read(AgentsModule.Module).Select(t => t.Name).ToList();
        Assert.Contains("evaluation_run", tables);
        Assert.Contains("evaluation_case", tables);
        Assert.Contains("evaluation_minimum", tables);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_run_is_kept_whole_with_its_cases_the_facts_of_the_run_and_the_judgement_json(string engine)
    {
        var store = Open(engine);

        store.Save(Run("run1"));
        var read = store.Run("run1")!;

        Assert.Equal(("sales-helper", "3", "test", "anthropic/claude", "manual"), (read.Agent, read.AgentVersion, read.Channel, read.Model, read.Trigger));
        Assert.Equal((0.75m, 1, 2, 0.0425m), (read.Score, read.Passed, read.Total, read.Cost));
        Assert.Equal("""["policy/refunds.md@v3"]""", read.KnowledgeVersions);
        Assert.Equal((Start, Start.AddSeconds(30)), (read.StartedAt, read.FinishedAt));
        Assert.Equal(["margin", "revenue"], read.Cases.Select(c => c.Name));
        var margin = read.Cases[0];
        Assert.Equal((0.5m, false, "the model is down"), (margin.Score, margin.Passed, margin.Problem![^"the model is down".Length..]));
        Assert.Equal((1200L, 300L, 0.0123m), (read.Cases[1].InputTokens, read.Cases[1].OutputTokens, read.Cases[1].Cost));
        Assert.Contains("answer-contains", read.Cases[1].Checks, StringComparison.Ordinal);
        Assert.Null(store.Run("nothing"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Runs_are_listed_newest_first_per_agent_and_channel_and_the_latest_of_one_version_is_found(string engine)
    {
        var store = Open(engine);
        store.Save(Run("old", version: "2", at: Start));
        store.Save(Run("mid", version: "3", score: 0.6m, at: Start.AddHours(1)));
        store.Save(Run("new", version: "3", score: 0.9m, at: Start.AddHours(2)));
        store.Save(Run("prod", version: "3", channel: "prod", at: Start.AddHours(3)));
        store.Save(Run("other", agent: "build-helper", at: Start.AddHours(4)));

        Assert.Equal(["prod", "new", "mid", "old"], store.Runs("sales-helper").Select(r => r.Id));
        Assert.Equal(["new", "mid", "old"], store.Runs("sales-helper", "test").Select(r => r.Id));
        Assert.Equal(["new"], store.Runs("sales-helper", "test", 1).Select(r => r.Id));
        Assert.Empty(store.Runs("sales-helper").SelectMany(r => r.Cases));              // a list does not carry the cases
        Assert.Equal("new", store.Latest("sales-helper", "3", "test")!.Id);
        Assert.Equal("old", store.Latest("sales-helper", "2", "test")!.Id);
        Assert.Null(store.Latest("sales-helper", "4", "test"));
        Assert.Null(store.Latest("sales-helper", "2", "prod"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_minimum_is_a_recorded_choice_that_can_be_changed_and_removed(string engine)
    {
        var clock = new StateTestClock(Start);
        var store = Open(engine, clock);
        Assert.Null(store.Minimum("sales-helper", "prod"));

        store.SetMinimum("sales-helper", "prod", 0.8m, "ada");
        clock.Advance(TimeSpan.FromHours(1));
        store.SetMinimum("sales-helper", "prod", 0.9m, "grace");

        var minimum = store.Minimum("sales-helper", "prod")!;
        Assert.Equal((0.9m, "grace", Start.AddHours(1)), (minimum.Minimum, minimum.SetBy, minimum.SetAt));
        Assert.Null(store.Minimum("sales-helper", "test"));                      // a channel's own
        Assert.Throws<ArgumentOutOfRangeException>(() => store.SetMinimum("sales-helper", "prod", 1.5m, "ada"));

        store.SetMinimum("sales-helper", "prod", null, "grace");
        Assert.Null(store.Minimum("sales-helper", "prod"));
    }
}
