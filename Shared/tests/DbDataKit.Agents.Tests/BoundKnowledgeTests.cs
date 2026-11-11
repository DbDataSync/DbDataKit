using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using DbDataKit.Knowledge;
using Microsoft.Extensions.AI;
using static DbDataKit.Agents.Tests.WorkedExample;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a KN34: bound knowledge in the assembly, the worked example of plan 27 section 3.5a end to end with the scripted fake.</summary>
public sealed class BoundKnowledgeTests
{
    private static readonly KnowledgeSource[] Sources = [new("project", [K1, K2, K3, K4]), new("install", [K6])];

    private static NounRouter ProjectRouter() => new("demo",
    [
        new Noun("project", "A project", [new Verb("show", "Show a model", EffectClass.OfflineOnly, "<kind> <name>", async (a, c) =>
        {
            await c.Out.WriteLineAsync($"semantic model {a.LastOrDefault()}: measures revenue, margin");
            return 0;
        }, DataSchemas.None)]),
    ], "1");

    [Fact]
    public async Task The_worked_example_gives_k1_and_k2_at_the_start_and_k3_from_the_turn_revenue_appeared_and_never_k6()
    {
        var recorder = new InMemoryRecorder();
        var context = new AgentRunContext("alice", "build-helper", "3");
        var tracker = new InPlayTracker([Workspace]).When((call, outcome) =>
            call.Tool == "project_show" && outcome.Text.Contains("revenue") ? new[] { Revenue } : Array.Empty<InPlay>());
        var turns = new TurnContext(_ => new ContextInputs([], [new ContextSource("instruction", "role.md", "v1", "You are the Build helper.")],
            BoundKnowledgeProvider.Bind(Agent, Sources, tracker.Current), [], [], []), context, recorder);
        var client = new ScriptedChatClient(ScriptStep.Call("project_show", Args.Of("semantic", "Sales")), ScriptStep.Say("Added the measure."));

        var run = await new AgentRunner(client, CommandTools.From(ProjectRouter(), new McpOptions(new HashSet<string>())))
            .RunAsync([new ChatMessage(ChatRole.User, "add a monthly revenue fact by region")], context,
                new AgentRunOptions { InstructionsForTurn = turns.ForTurn, AfterCall = tracker.Observe, Recorder = recorder });

        Assert.Equal(AgentStop.Completed, run.Stop);
        var given = recorder.Events.Where(e => e.Kind == AgentEventKinds.ContextGiven).ToList();
        Assert.Equal(2, given.Count);
        Assert.Equal([1, 2], given.Select(g => g.Turn));
        Assert.Contains("bound-instruction/K1@", given[0].Detail);
        Assert.Contains("bound-guardrail/K2@", given[0].Detail);
        Assert.DoesNotContain("K3", given[0].Detail);
        Assert.Contains("bound-instruction/K3@", given[1].Detail);
        Assert.True(given[1].Detail!.IndexOf("K1@", StringComparison.Ordinal) < given[1].Detail!.IndexOf("K3@", StringComparison.Ordinal));   // broad to narrow
        Assert.All(given, g => { Assert.DoesNotContain("K6", g.Detail); Assert.DoesNotContain("K4", g.Detail); });
        // the model was sent exactly that text
        var first = client.Requests[0][0].Text;
        var second = client.Requests[1][0].Text;
        Assert.Contains("Facts are named fct_", first);
        Assert.Contains("Every dimension has an unknown member", first);
        Assert.DoesNotContain("net revenue", first);
        Assert.Contains("Revenue means net revenue", second);
        Assert.DoesNotContain("Say that an answer is generated", second);
        Assert.Equal(ChatRole.System, client.Requests[1][0].Role);
        Assert.Single(client.Requests[1], m => m.Role == ChatRole.System);                                        // replaced, not stacked
    }

    [Fact]
    public void The_agents_knowledge_settings_narrow_what_it_draws_on_and_the_install_can_give_an_item_to_everyone()
    {
        var narrow = AgentDefinitionLoader.Load("agent: 1\nname: build-helper\naudience: back-end\nmodel: { slot: default }\nknowledge: { from: [project], exclude: [K2] }", "a.yml").Definition!;
        var bound = BoundKnowledgeProvider.Bind(narrow, Sources, [Workspace, Revenue]);

        Assert.Equal(["K1", "K3"], bound.Instructions.Select(i => i.Id));
        Assert.Empty(bound.Guardrails);                                                            // K2 is excluded by id

        var onlyInstall = AgentDefinitionLoader.Load("agent: 1\nname: build-helper\naudience: back-end\nmodel: { slot: default }\nknowledge: { from: [install] }", "a.yml").Definition!;
        Assert.Empty(BoundKnowledgeProvider.Bind(onlyInstall, Sources, [Workspace]).All);          // K6 is for front-end
        var everyone = BoundKnowledgeProvider.Bind(onlyInstall, Sources, [Workspace], giveToEveryone: new HashSet<string> { "K6" });
        Assert.Equal(["K6"], everyone.Instructions.Select(i => i.Id));                              // the install gave it to every agent
    }

    [Fact]
    public void A_reference_the_agent_is_always_given_counts_as_a_context_part()
    {
        var withAlways = AgentDefinitionLoader.Load("agent: 1\nname: build-helper\naudience: back-end\nmodel: { slot: default }\nknowledge: { always: [K4] }", "a.yml").Definition!;

        var parts = BoundKnowledgeProvider.AlwaysGiven(withAlways, Sources);

        var part = Assert.Single(parts);
        Assert.Equal(("reference", "K4"), (part.Kind, part.Id));
        Assert.Contains("EMEA", part.Text);
    }

    [Fact]
    public void A_dropped_subject_orphans_an_item_and_it_is_no_longer_given()
    {
        var catalog = new SubjectCatalog().Add("build-project", "sales-warehouse", "folder", "models/marts").Add("semantic-model", "Sales", "measure", "revenue");
        Assert.Equal(["K1", "K3"], BoundKnowledgeProvider.Bind(Agent, Sources, [Workspace, Revenue], catalog).Instructions.Select(i => i.Id));

        catalog.Retire("semantic-model", "Sales");

        Assert.Equal(["K1"], BoundKnowledgeProvider.Bind(Agent, Sources, [Workspace, Revenue], catalog).Instructions.Select(i => i.Id));
    }
}
