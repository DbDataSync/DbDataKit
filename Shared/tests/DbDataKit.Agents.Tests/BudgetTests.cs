using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG11: the model's space.</summary>
public sealed class BudgetTests
{
    private static string Words(int n, string word = "word") => string.Join(' ', Enumerable.Repeat(word, n));

    private static ChatMessage Turn(string text) => new(ChatRole.User, text);

    private static ContextSource Part(string kind, string id, int words) => ContextSource.Of(kind, id, Words(words, id));

    [Fact]
    public void A_definition_over_its_budget_is_refused_naming_what_is_largest_and_how_it_was_counted()
    {
        var parts = new[] { Part("instruction", "role.md", 120), Part("tool", "project_compile", 30), Part("skill", "dbdatabuild", 80), Part("guardrail", "K2", 10) };

        var report = ContextBudget.Check(parts, windowTokens: 1000, new WordCounter(), share: 0.2);        // a budget of 200 words; 240 are given

        Assert.True(report.Over);
        Assert.Equal((1000, 200, 240), (report.Window, report.BudgetTokens, report.TotalTokens));
        Assert.Equal("instruction role.md (120 tokens), skill dbdatabuild (80 tokens), tool project_compile (30 tokens)", report.Largest());
        var refusal = report.Refusal("build-helper");
        Assert.Contains("over its budget of 200 tokens (20% of the model's 1000-token window; counted with a word count)", refusal);
        Assert.Contains("The largest parts are: instruction role.md (120 tokens)", refusal);
    }

    [Fact]
    public void A_definition_within_its_budget_is_not_over_and_the_default_share_is_a_fifth()
    {
        var report = ContextBudget.Check([Part("instruction", "role.md", 50)], 1000, new WordCounter());

        Assert.False(report.Over);
        Assert.Equal(200, report.BudgetTokens);
        Assert.Equal(0.20, ContextBudget.DefaultShare);
    }

    [Fact]
    public void A_model_with_no_local_tokenizer_is_counted_by_a_conservative_estimate_that_says_so()
    {
        var counter = EstimateCounter.Instance;

        Assert.Equal(3, counter.Count(new string('a', 7)));                                                // 7 / 3.5 = 2, plus 15% is 2.3, rounded up
        Assert.Equal(0, counter.Count(""));
        Assert.Contains("characters divided by 3.5, plus 15%", counter.Basis);
        Assert.Contains("an estimate", ContextBudget.Check([Part("instruction", "r", 3000)], 1000, counter).Refusal("a"));
    }

    [Fact]
    public void Tool_definitions_count_toward_the_always_given_text()
    {
        var parts = ContextBudget.ToolDefinitions(new Product().Tools()).ToList();

        Assert.Equal(5, parts.Count);
        Assert.All(parts, p => { Assert.Equal("tool", p.Kind); Assert.Contains("\"type\":\"object\"", p.Text.Replace(" ", "")); });
    }


    [Fact]
    public void A_turn_over_the_window_drops_references_first_the_last_found_first_and_never_an_instruction()
    {
        var references = new[] { Part("reference", "r1", 30), Part("reference", "r2", 30), Part("reference", "r3", 30) };
        var conversation = new[] { Turn(Words(10)), Turn(Words(10)) };

        // always 100, window 250, reserve 20: room is 130; used 90 + 20 = 110 fits, so nothing goes
        var fits = ContextFitter.Fit(100, 250, 20, references, conversation, new WordCounter(), _ => "x");
        Assert.Empty(fits.Dropped);

        // window 210: room 90; used 110, so the last reference goes (80 fits)
        var tight = ContextFitter.Fit(100, 210, 20, references, conversation, new WordCounter(), _ => "x");
        Assert.Equal(["reference r3"], tight.Dropped);
        Assert.Equal(["r1", "r2"], tight.References.Select(r => r.Id));
        Assert.Equal(2, tight.Conversation.Count);
        Assert.True(tight.InstructionsFit);
    }

    [Fact]
    public void When_the_references_are_gone_the_oldest_turns_are_summarised_and_the_summary_is_recorded()
    {
        var conversation = new[] { Turn("old " + Words(40)), Turn("middle " + Words(40)), Turn("latest " + Words(10)) };
        var cut = new List<ChatMessage>();

        // always 100, window 140, reserve 10: room 30; the conversation is 41 + 41 + 11 words
        var fit = ContextFitter.Fit(100, 140, 10, [Part("reference", "r1", 20)], conversation, new WordCounter(), messages => { cut.AddRange(messages); return "they talked about the range"; });

        Assert.Equal(["reference r1", "2 earlier message(s), summarised"], fit.Dropped);
        Assert.Equal("they talked about the range", fit.Summary);
        Assert.Equal(2, cut.Count);
        Assert.Equal(2, fit.Conversation.Count);                                                           // the summary and the latest turn
        Assert.StartsWith("Summary of the earlier conversation: they talked about the range", fit.Conversation[0].Text);
        Assert.StartsWith("latest", fit.Conversation[1].Text);                                             // the latest turn is never summarised away
    }

    [Fact]
    public void When_the_instructions_alone_do_not_fit_nothing_is_cut_it_is_a_refusal()
    {
        var references = new[] { Part("reference", "r1", 10) };

        var fit = ContextFitter.Fit(300, 250, 20, references, [Turn("hello")], new WordCounter(), _ => "x");

        Assert.False(fit.InstructionsFit);
        Assert.Empty(fit.Dropped);
        Assert.Single(fit.References);
    }
}
