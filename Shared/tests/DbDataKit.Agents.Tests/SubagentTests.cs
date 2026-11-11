using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG20: a subagent is a tool; its reach is the parent's narrowed by its own; its asks go through the parent's conversation; its use counts to the parent; depth three is refused; the transcript nests.</summary>
public sealed class SubagentTests
{
    private static AgentDefinition Def(string name, string tools, string subagents = "") =>
        AgentDefinitionLoader.Load($"agent: 1\nname: {name}\naudience: back-end\nmodel: {{ slot: default }}\ntools:\n{tools}{(subagents.Length > 0 ? $"subagents: [{subagents}]\n" : "")}", "agent.yml").Definition!;

    private sealed class Setup
    {
        public Product Product { get; } = new();
        public Dictionary<string, ScriptedChatClient> Models { get; } = [];
        public Dictionary<string, AgentDefinition> Definitions { get; } = [];
        public InMemoryRecorder Recorder { get; } = new();

        public SubagentSetup? Resolve(string name) => !Definitions.TryGetValue(name, out var d) ? null
            : new SubagentSetup(d, "2", Models[name], $"You are {name}.", Product.Tools().Where(t => d.Tools.Any(r => r.ToolName == t.Name)).ToList());

        public IReadOnlyList<AgentTool> For(AgentDefinition parent, IReadOnlyList<AgentTool> parentTools, AgentRunContext context, IToolGate? gate = null) =>
            SubagentTools.For(parent, parentTools, context, Resolve, _ => gate, gate, null, Recorder);
    }

    private static readonly AgentRunContext Context = new("alice", "lead", "1");

    [Fact]
    public async Task A_subagent_appears_as_a_tool_and_answers_with_its_own_instructions_and_only_the_task_and_context_it_was_given()
    {
        var s = new Setup();
        s.Definitions["lead"] = Def("lead", "  - command: thing show\n", "reader");
        s.Definitions["reader"] = Def("reader", "  - command: thing show\n");
        s.Models["reader"] = new ScriptedChatClient(ScriptStep.Call("thing_show", Args.Of("widget")), ScriptStep.Say("It is a widget."));
        var parentTools = s.Product.Tools().Where(t => t.Name == "thing_show").ToList();
        var tools = parentTools.Concat(s.For(s.Definitions["lead"], parentTools, Context)).ToList();
        Assert.Equal(["thing_show", "agent_reader"], tools.Select(t => t.Name));

        var lead = new ScriptedChatClient(ScriptStep.Call("agent_reader", new { task = "describe the widget", context = "the user is looking at the sales project" }), ScriptStep.Say("The reader says it is a widget."));
        var run = await new AgentRunner(lead, tools).RunAsync([new ChatMessage(ChatRole.User, "what is the widget?")], Context, new AgentRunOptions { Recorder = s.Recorder });

        Assert.Equal(AgentStop.Completed, run.Stop);
        Assert.Equal(["show widget"], s.Product.Ran);
        var seen = s.Models["reader"].Requests[0];
        Assert.Equal("You are reader.", seen[0].Text);
        Assert.Contains("describe the widget", seen[1].Text);
        Assert.Contains("the user is looking at the sales project", seen[1].Text);
        Assert.Equal(2, seen.Count);   // nothing else of the parent's conversation
        Assert.Contains("It is a widget.", Transcript.ResultTexts(lead.Requests[1]).Single());
    }

    [Fact]
    public async Task A_subagent_cannot_call_a_tool_its_parent_could_not()
    {
        var s = new Setup();
        s.Definitions["lead"] = Def("lead", "  - command: thing show\n", "worker");
        s.Definitions["worker"] = Def("worker", "  - command: thing show\n  - command: thing deploy\n");
        s.Models["worker"] = new ScriptedChatClient(ScriptStep.Call("thing_deploy", Args.Of("prod")), ScriptStep.Say("Could not deploy."));
        var parentTools = s.Product.Tools().Where(t => t.Name == "thing_show").ToList();
        var tools = s.For(s.Definitions["lead"], parentTools, Context);

        var lead = new ScriptedChatClient(ScriptStep.Call("agent_worker", new { task = "deploy it" }), ScriptStep.Say("done"));
        await new AgentRunner(lead, [.. parentTools, .. tools]).RunAsync([new ChatMessage(ChatRole.User, "go")], Context, new AgentRunOptions { Recorder = s.Recorder });

        Assert.Empty(s.Product.Ran);
        var told = Transcript.ResultTexts(s.Models["worker"].Requests[1]).Single();
        Assert.Contains("There is no tool named 'thing_deploy'", told);
        Assert.Contains(s.Recorder.Events, e => e.Kind == "subagent-narrowed" && e.Detail!.Contains("thing_deploy"));
    }

    [Fact]
    public async Task A_step_a_subagent_would_take_asks_through_the_parents_conversation_and_its_use_counts_to_the_parent()
    {
        var s = new Setup();
        s.Definitions["lead"] = Def("lead", "  - command: thing deploy\n", "worker");
        s.Definitions["worker"] = Def("worker", "  - command: thing deploy\n");
        s.Models["worker"] = new ScriptedChatClient(ScriptStep.Call("thing_deploy", Args.Of("prod")).WithUsage(100, 10), ScriptStep.Say("Deployed.").WithUsage(150, 5));
        var parentTools = s.Product.Tools().Where(t => t.Name == "thing_deploy").ToList();
        var approver = new ScriptedApprover(true);
        var gate = new ApprovalGate(approver, PromptingPolicy.None, null, s.Recorder);
        var tools = s.For(s.Definitions["lead"], parentTools, Context, gate);

        var lead = new ScriptedChatClient(ScriptStep.Call("agent_worker", new { task = "deploy to prod" }).WithUsage(200, 20), ScriptStep.Say("It is deployed.").WithUsage(300, 8));
        var run = await new AgentRunner(lead, [.. parentTools, .. tools]).RunAsync([new ChatMessage(ChatRole.User, "deploy")], Context, new AgentRunOptions { Gate = gate, Recorder = s.Recorder });

        Assert.Equal(["deploy prod"], s.Product.Ran);
        var asked = Assert.Single(approver.Asked);               // one desk: the person is asked once, by the subagent's step
        Assert.Equal("worker", asked.Agent);
        Assert.Equal("alice", asked.Person);
        // the parent's own two turns and the subagent's two, its tool call, its tokens
        Assert.Equal(4, run.Usage.ModelTurns);
        Assert.Equal(2, run.Usage.ToolCalls);                      // the call of the subagent and the deploy inside it
        Assert.Equal(200 + 300 + 100 + 150, run.Usage.InputTokens);
        Assert.Equal(20 + 8 + 10 + 5, run.Usage.OutputTokens);
    }

    [Fact]
    public async Task A_denied_step_in_a_subagent_does_not_run_and_the_parent_is_told_what_the_subagent_said()
    {
        var s = new Setup();
        s.Definitions["lead"] = Def("lead", "  - command: thing deploy\n", "worker");
        s.Definitions["worker"] = Def("worker", "  - command: thing deploy\n");
        s.Models["worker"] = new ScriptedChatClient(ScriptStep.Call("thing_deploy", Args.Of("prod")), ScriptStep.Say("The person did not approve the deploy."));
        var parentTools = s.Product.Tools().Where(t => t.Name == "thing_deploy").ToList();
        var gate = new ApprovalGate(new ScriptedApprover(false), PromptingPolicy.None);
        var tools = s.For(s.Definitions["lead"], parentTools, Context, gate);

        var lead = new ScriptedChatClient(ScriptStep.Call("agent_worker", new { task = "deploy" }), ScriptStep.Say("Not deployed."));
        await new AgentRunner(lead, [.. parentTools, .. tools]).RunAsync([new ChatMessage(ChatRole.User, "deploy")], Context, new AgentRunOptions { Gate = gate });

        Assert.Empty(s.Product.Ran);
        Assert.Contains("did not approve", Transcript.ResultTexts(lead.Requests[1]).Single());
    }

    [Fact]
    public async Task Depth_three_is_refused_with_a_message_and_the_transcript_nests()
    {
        var s = new Setup();
        foreach (var (name, next) in new[] { ("a", "b"), ("b", "c"), ("c", "d"), ("d", "") })
        {
            s.Definitions[name] = Def(name, "  - command: thing show\n", next);
        }
        s.Models["b"] = new ScriptedChatClient(ScriptStep.Call("agent_c", new { task = "go on" }), ScriptStep.Say("b done"));
        s.Models["c"] = new ScriptedChatClient(ScriptStep.Call("agent_d", new { task = "and on" }), ScriptStep.Say("c done"));
        s.Models["d"] = new ScriptedChatClient(ScriptStep.Say("never asked"));
        var parentTools = s.Product.Tools().Where(t => t.Name == "thing_show").ToList();
        var top = new AgentRunContext("alice", "a", "1");
        var tools = s.For(s.Definitions["a"], parentTools, top);

        var lead = new ScriptedChatClient(ScriptStep.Call("agent_b", new { task = "start" }), ScriptStep.Say("a done"));
        var run = await new AgentRunner(lead, [.. parentTools, .. tools]).RunAsync([new ChatMessage(ChatRole.User, "go")], top, new AgentRunOptions { Recorder = s.Recorder });

        Assert.Equal(AgentStop.Completed, run.Stop);
        Assert.Empty(s.Models["d"].Requests);                                                            // never asked
        Assert.Contains("nest at most 2 levels deep", Transcript.ResultTexts(s.Models["c"].Requests[1]).Single());
        var started = s.Recorder.Events.Where(e => e.Kind == "subagent-started").ToList();
        Assert.Equal([("b", "a", 1), ("c", "b", 2)], started.Select(e => (e.Context.Agent, e.Context.ParentAgent!, e.Context.Depth)));
        Assert.All(s.Recorder.Events, e => Assert.Equal("alice", e.Context.Person));
        Assert.All(started, e => Assert.Equal("2", e.Context.AgentVersion));
    }

    [Fact]
    public async Task A_subagent_that_does_not_exist_is_a_result_not_an_exception()
    {
        var s = new Setup();
        s.Definitions["lead"] = Def("lead", "  - command: thing show\n", "ghost");
        var tools = s.For(s.Definitions["lead"], [], Context);
        var lead = new ScriptedChatClient(ScriptStep.Call("agent_ghost", new { task = "x" }), ScriptStep.Say("ok"));
        var run = await new AgentRunner(lead, tools).RunAsync([new ChatMessage(ChatRole.User, "go")], Context);
        Assert.Equal(AgentStop.Completed, run.Stop);
        Assert.Contains("not available", Transcript.ResultTexts(lead.Requests[1]).Single());
    }
}
