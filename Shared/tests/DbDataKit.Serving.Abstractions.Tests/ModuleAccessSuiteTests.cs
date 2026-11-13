using DbDataKit.Serving.Conformance;
using Xunit;

namespace DbDataKit.Serving.Tests;

/// <summary>
/// Plan 22a GP14 and 24a UC15: the suite that holds every module to the serving core's answer is itself held to account here with fake modules. A module that gives every person the same rows, one that keeps an answer under a key that leaves the person out,
/// one that goes on serving the old rules after the rules table changed and one that refuses what the core answers each fail it; a module that applies the rules passes it in every phase; a question every person gets the same answer to is reported.
/// </summary>
public sealed class ModuleAccessSuiteTests
{
    private static readonly ConformanceQuestion ByRegion = new("by-region", "revenue by region");
    private static readonly ConformanceQuestion Members = new("members", "the regions a person's rows hold");

    private static readonly string[] People = ["ada", "bea", "cy"];

    /// <summary>The rules: a table of who sees which region. The core filters by it; changing it is what a producer's refresh does.</summary>
    private sealed class Rules
    {
        public Dictionary<string, string[]> Regions { get; } = new() { ["ada"] = ["East"], ["bea"] = ["East", "West"], ["cy"] = [] };

        public static readonly Dictionary<string, string> Revenue = new() { ["East"] = "200", ["West"] = "100", ["North"] = "50" };

        public ModuleAnswer For(string person, ConformanceQuestion question) => ModuleAnswer.Of(Regions[person].Select(r => question.Id == "members" ? r : ModuleAnswer.Row(r, Revenue[r])));
    }

    private sealed class Core(Rules rules) : IConformanceModule
    {
        public string Name => "core";

        public bool Asks(ConformanceQuestion question) => true;

        public Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default) => Task.FromResult(rules.For(person, question));
    }

    /// <summary>A module that goes through the core.</summary>
    private sealed class Faithful(Rules rules, string name = "faithful") : IConformanceModule
    {
        public string Name => name;

        public bool Asks(ConformanceQuestion question) => true;

        public Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default) => Task.FromResult(rules.For(person, question));
    }

    /// <summary>A module that reads the table itself: every row, for everyone.</summary>
    private sealed class Bypass : IConformanceModule
    {
        public string Name => "bypass";

        public bool Asks(ConformanceQuestion question) => true;

        public Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default) =>
            Task.FromResult(ModuleAnswer.Of(Rules.Revenue.Select(r => question.Id == "members" ? r.Key : ModuleAnswer.Row(r.Key, r.Value))));
    }

    /// <summary>A module with a cache of its own whose key is the question alone: the first person's answer goes to the next.</summary>
    private sealed class NaiveCache(Rules rules) : IConformanceModule
    {
        private readonly Dictionary<string, ModuleAnswer> _kept = [];

        public string Name => "naive-cache";

        public bool Asks(ConformanceQuestion question) => true;

        public Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
        {
            if (!_kept.TryGetValue(question.Id, out var answer)) _kept[question.Id] = answer = rules.For(person, question);
            return Task.FromResult(answer);
        }
    }

    /// <summary>A module whose cache is keyed by person and question, and never hears of a change of the rules.</summary>
    private sealed class StaleCache(Rules rules) : IConformanceModule
    {
        private readonly Dictionary<string, ModuleAnswer> _kept = [];

        public string Name => "stale-cache";

        public bool Asks(ConformanceQuestion question) => true;

        public Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
        {
            var key = person + "/" + question.Id;
            if (!_kept.TryGetValue(key, out var answer)) _kept[key] = answer = rules.For(person, question);
            return Task.FromResult(answer);
        }
    }

    private sealed class Refuses : IConformanceModule
    {
        public string Name => "refuses";

        public bool Asks(ConformanceQuestion question) => question.Id == "by-region";

        public Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default) => throw new ServingDeniedException("the module's own refusal");
    }

    private static ModuleAccessSuite Suite(Rules rules, params IConformanceModule[] modules) => new(new Core(rules), modules, People, [ByRegion, Members]);

    [Fact]
    public async Task A_module_that_goes_through_the_core_passes_in_every_phase_and_the_report_says_what_was_compared()
    {
        var rules = new Rules();
        var suite = Suite(rules, new Faithful(rules));

        var report = await suite.RunAsync("before");
        rules.Regions["cy"] = ["North"];                        // the producer refreshed the rules table
        await suite.RunAsync("after", report);

        Assert.True(report.Passed, report.Describe());
        Assert.Equal(2 * 2 * 2 * 3, report.Checks);             // two phases, two questions, two rounds, three people
        Assert.Contains("faithful/by-region", report.Asked);
    }

    [Fact]
    public async Task A_module_that_reads_the_data_itself_fails_for_everyone_the_rules_narrow()
    {
        var rules = new Rules();
        var report = await Suite(rules, new Bypass()).RunAsync("before");

        Assert.False(report.Passed);
        // ada and cy see fewer rows through the core than the bypass gives; bea sees East and West, North is the row the module adds
        Assert.Contains(report.Violations, v => v.Module == "bypass" && v.Person == "ada" && v.Question == "by-region");
        Assert.Contains(report.Violations, v => v.Module == "bypass" && v.Person == "cy");
        Assert.Contains("bypass: cy asked", report.Describe());
    }

    [Fact]
    public async Task A_module_with_a_cache_that_leaves_the_person_out_of_its_key_serves_one_person_the_answer_of_another()
    {
        var rules = new Rules();
        var report = await Suite(rules, new NaiveCache(rules)).RunAsync("before");

        Assert.False(report.Passed);
        Assert.Contains(report.Violations, v => v.Module == "naive-cache" && v.Person != "ada");
    }

    [Fact]
    public async Task A_module_that_does_not_hear_of_a_change_of_the_rules_serves_the_old_answer_in_the_next_phase()
    {
        var rules = new Rules();
        var suite = Suite(rules, new StaleCache(rules));

        var before = await suite.RunAsync("before");
        Assert.True(before.Passed, before.Describe());           // nothing is wrong until the rules change
        rules.Regions["ada"] = ["East", "West"];
        var after = await suite.RunAsync("after", before);

        Assert.Contains(after.Violations, v => v.Phase == "after" && v.Module == "stale-cache" && v.Person == "ada");
        Assert.DoesNotContain(after.Violations, v => v.Phase == "before");
    }

    [Fact]
    public async Task A_refusal_where_the_core_answers_is_a_violation_and_two_refusals_agree_whatever_their_words()
    {
        var rules = new Rules();
        var report = await Suite(rules, new Refuses()).RunAsync("before");
        Assert.Contains(report.Violations, v => v.Module == "refuses" && v.Person == "ada" && v.Actual.Refused);

        Assert.True(ModuleAnswer.Refusal("a").SameAs(ModuleAnswer.Refusal("another way to say no")));
        Assert.False(ModuleAnswer.Refusal("a").SameAs(ModuleAnswer.Of([])));
    }

    [Fact]
    public async Task A_question_every_person_gets_the_same_answer_to_is_reported_because_it_cannot_tell_a_bypass()
    {
        var rules = new Rules();
        foreach (var person in People) rules.Regions[person] = ["East"];

        var report = await Suite(rules, new Faithful(rules)).RunAsync("before");

        Assert.False(report.Passed);
        Assert.Equal(["by-region", "members"], report.WithoutContrast);
        Assert.Contains("cannot tell a module that applies the rules from one that does not", report.Describe());
    }

    [Fact]
    public void A_question_a_module_cannot_ask_is_listed_as_not_covered_and_a_run_of_nothing_does_not_pass()
    {
        var rules = new Rules();
        var suite = Suite(rules, new Refuses());

        Assert.Equal(["refuses/members"], suite.NotAsked());
        Assert.False(new ConformanceReport().Passed);
    }

    [Fact]
    public void Cells_are_compared_in_the_invariant_form_a_number_without_trailing_zeros_and_a_null_as_a_word()
    {
        Assert.Equal("200.5|NULL|true|7", ModuleAnswer.Row(200.50m, null, true, 7L));
        Assert.Equal("1.5", ModuleAnswer.Cell(1.5d));
        Assert.Equal("300", ModuleAnswer.Cell(300.0000d));
    }
}
