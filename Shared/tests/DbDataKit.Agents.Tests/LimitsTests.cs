using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG08: limits per conversation, person and install, cost from the provider's usage and the price, and one notification at the threshold.</summary>
public sealed class LimitsTests
{
    private static readonly AgentRunContext Context = new("alice", "build-helper", "3", "conv-1");

    private static ChatMessage Go() => new(ChatRole.User, "go");

    private static ScriptStep Call(long input = 10, long output = 5) => ScriptStep.Call("thing_rows").WithUsage(input, output);

    private static AgentRunner Runner(ScriptedChatClient client) => new(client, new Product().Tools());

    [Fact]
    public async Task A_conversation_that_reaches_its_turn_limit_stops_with_the_message_and_records_it()
    {
        var recorder = new InMemoryRecorder();
        var limits = new RunLimits(new LimitSet(Turns: 3), LimitSet.None, LimitSet.None, null, new InMemoryUsageLedger());
        var client = new ScriptedChatClient(Call(), Call(), Call(), ScriptStep.Say("never"));

        var run = await Runner(client).RunAsync([Go()], Context, new AgentRunOptions { Limits = limits, Recorder = recorder });

        Assert.Equal(AgentStop.LimitReached, run.Stop);
        Assert.Equal("This conversation reached its limit of 3 model turns (used 3). The run stopped.", run.StopMessage);
        Assert.Equal(3, run.Usage.ModelTurns);
        Assert.Contains(recorder.Events, e => e.Kind == AgentEventKinds.LimitReached && e.Detail == run.StopMessage);
        Assert.Equal(1, client.StepsLeft);                                                // the fourth step was never asked for
    }

    [Fact]
    public async Task A_tool_call_limit_stops_before_the_call_that_would_pass_it()
    {
        var product = new Product();
        var limits = new RunLimits(new LimitSet(ToolCalls: 2), LimitSet.None, LimitSet.None, null, new InMemoryUsageLedger());
        var client = new ScriptedChatClient(ScriptStep.Call("thing_rows").AndCall("thing_rows").AndCall("thing_rows"), ScriptStep.Say("never"));

        var run = await new AgentRunner(client, product.Tools()).RunAsync([Go()], Context, new AgentRunOptions { Limits = limits });

        Assert.Equal(AgentStop.LimitReached, run.Stop);
        Assert.Contains("2 tool calls", run.StopMessage);
        Assert.Equal(2, run.Usage.ToolCalls);
    }

    [Fact]
    public async Task A_token_limit_counts_what_the_provider_reports()
    {
        var limits = new RunLimits(new LimitSet(Tokens: 40), LimitSet.None, LimitSet.None, null, new InMemoryUsageLedger());
        var client = new ScriptedChatClient(Call(10, 5), Call(10, 5), Call(10, 5), ScriptStep.Say("never"));

        var run = await Runner(client).RunAsync([Go()], Context, new AgentRunOptions { Limits = limits });

        Assert.Equal(AgentStop.LimitReached, run.Stop);                                   // 15, 30, 45 tokens: stopped before the fourth turn
        Assert.Contains("40 tokens (used 45)", run.StopMessage);
    }

    [Fact]
    public void Cost_is_the_reported_usage_times_the_price_exactly()
    {
        var price = new ModelPrice(InputPerMillion: 3m, OutputPerMillion: 15m);

        Assert.Equal(0.0105m, price.CostOf(1000, 500));                                  // (1000 * 3 + 500 * 15) / 1,000,000
        Assert.Equal(0m, price.CostOf(0, 0));
        Assert.Equal(18m, price.CostOf(1_000_000, 1_000_000));
    }

    [Fact]
    public async Task A_cost_limit_stops_the_conversation_and_the_message_says_how_much_was_used()
    {
        var price = new ModelPrice(3m, 15m);
        var limits = new RunLimits(new LimitSet(Cost: 0.02m), LimitSet.None, LimitSet.None, price, new InMemoryUsageLedger());
        var client = new ScriptedChatClient(Call(1000, 500), Call(1000, 500), Call(1000, 500), ScriptStep.Say("never"));

        var run = await Runner(client).RunAsync([Go()], Context, new AgentRunOptions { Limits = limits });

        Assert.Equal(AgentStop.LimitReached, run.Stop);                                   // 0.0105, 0.021 >= 0.02: stopped before the third turn
        Assert.Equal("This conversation reached its limit of 0.02 in cost (used 0.021). The run stopped.", run.StopMessage);
        Assert.Equal(2, run.Usage.ModelTurns);
    }

    [Fact]
    public async Task A_person_and_the_install_are_limited_across_conversations_through_the_ledger()
    {
        var ledger = new InMemoryUsageLedger();
        var limits = new RunLimits(LimitSet.None, new LimitSet(Turns: 3), new LimitSet(Turns: 5), null, ledger);

        var first = await Runner(new ScriptedChatClient(Call(), Call(), ScriptStep.Say("done"))).RunAsync([Go()], Context, new AgentRunOptions { Limits = limits });
        limits.Commit(Context, first.Usage);
        Assert.Equal(AgentStop.Completed, first.Stop);                                    // 3 turns: at the person's limit, but the run had finished

        var second = await Runner(new ScriptedChatClient(ScriptStep.Say("never"))).RunAsync([Go()], Context with { ConversationId = "conv-2" }, new AgentRunOptions { Limits = limits });
        Assert.Equal(AgentStop.LimitReached, second.Stop);
        Assert.Equal("alice reached the per-person limit of 3 model turns (used 3). The run stopped.", second.StopMessage);

        // another person is not held by alice's limit but is by the install's
        var bob = Context with { Person = "bob", ConversationId = "conv-3" };
        var third = await Runner(new ScriptedChatClient(Call(), ScriptStep.Say("done"))).RunAsync([Go()], bob, new AgentRunOptions { Limits = limits });
        limits.Commit(bob, third.Usage);
        Assert.Equal(AgentStop.Completed, third.Stop);
        var fourth = await Runner(new ScriptedChatClient(ScriptStep.Say("never"))).RunAsync([Go()], bob with { ConversationId = "conv-4" }, new AgentRunOptions { Limits = limits });
        Assert.Equal(AgentStop.LimitReached, fourth.Stop);
        Assert.StartsWith("This install reached its limit of 5 model turns", fourth.StopMessage);
    }

    [Fact]
    public async Task The_threshold_writes_one_notification_not_one_per_turn()
    {
        var notifier = new CollectingNotifier();
        var limits = new RunLimits(new LimitSet(Turns: 10), LimitSet.None, LimitSet.None, null, new InMemoryUsageLedger(), notifier, notifyAtPercent: 50);
        var steps = Enumerable.Range(0, 7).Select(_ => Call()).Append(ScriptStep.Say("done")).ToArray();

        var run = await Runner(new ScriptedChatClient(steps)).RunAsync([Go()], Context, new AgentRunOptions { Limits = limits });

        Assert.Equal(AgentStop.Completed, run.Stop);
        var message = Assert.Single(notifier.Messages);                                   // 5 of 10 turns was the first to pass 50%
        Assert.Contains("this conversation has used 5 of its 10 model turns", message);
        Assert.Contains("alice", message);
    }

    [Fact]
    public async Task No_limits_means_no_stop()
    {
        var run = await Runner(new ScriptedChatClient(Call(), Call(), ScriptStep.Say("done"))).RunAsync([Go()], Context, new AgentRunOptions { Limits = new RunLimits(LimitSet.None, LimitSet.None, LimitSet.None, null, new InMemoryUsageLedger()) });

        Assert.Equal(AgentStop.Completed, run.Stop);
        Assert.True(LimitSet.None.IsNone);
    }
}
