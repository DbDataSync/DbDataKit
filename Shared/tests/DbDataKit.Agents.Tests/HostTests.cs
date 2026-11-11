using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using DbDataKit.Config.Yaml;
using DbDataKit.Knowledge;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>
/// Plan 27a AG14 (the session a host runs), AG15 (the approval desk), AG17 (the policy editor), AG30 (what each tool asks), AG31 (the model's space) and AG32 (skills as a screen shows them): everything a console screen shows is computed here, by
/// the functions the runtime itself uses, and tested without a host, a model or a network.
/// </summary>
public sealed class HostTests
{
    private const string Agent = """
        agent: 1
        name: helper
        audience: back-end
        model: { slot: default }
        tools:
          - command: thing show
          - command: thing edit
            prompting: do-not-ask
          - command: thing deploy
        """;

    private static AgentDefinition Definition(string yaml = Agent) => AgentDefinitionLoader.Load(yaml, "agent.yml").Definition!;

    // ---- AG30: what each tool will ask ----

    [Fact]
    public void Each_tool_shows_the_setting_that_applies_and_the_layer_that_sets_it_and_the_screen_agrees_with_the_gate()
    {
        var tools = new Product().Tools();
        var policy = new PromptingPolicy(new Dictionary<Traits, Prompting> { [Traits.WritesDefinitions] = Prompting.Ask }, new Dictionary<string, Prompting> { ["thing_show"] = Prompting.AskOnce });
        var settings = new Dictionary<string, Prompting> { ["thing_edit"] = Prompting.DoNotAsk, ["thing_rows"] = Prompting.Ask };

        var rows = ToolPromptingReport.Build(tools, policy, settings).ToDictionary(r => r.Tool);

        Assert.Equal((Prompting.AskOnce, "install policy: this tool"), (rows["thing_show"].Effective, rows["thing_show"].Layer));
        Assert.Equal((Prompting.Ask, "agent"), (rows["thing_rows"].Effective, rows["thing_rows"].Layer));
        Assert.Equal(Prompting.Ask, rows["thing_edit"].Effective);
        Assert.StartsWith("install policy: minimum for writes-definitions", rows["thing_edit"].Layer);
        Assert.Equal("traits", rows["thing_deploy"].Layer);
        foreach (var tool in tools)
            Assert.Equal(PromptingResolver.Resolve(tool, policy, settings.TryGetValue(tool.Name, out var s) ? s : null), rows[tool.Name].Effective);
    }

    [Fact]
    public void A_setting_below_what_a_higher_layer_needs_is_a_warning_that_names_the_layer_and_raising_it_clears_it()
    {
        var edit = new Product().Tools().Single(t => t.Name == "thing_edit");
        var policy = new PromptingPolicy(new Dictionary<Traits, Prompting> { [Traits.WritesDefinitions] = Prompting.Ask }, new Dictionary<string, Prompting>());

        var low = ToolPromptingReport.Build([edit], policy, new Dictionary<string, Prompting> { ["thing_edit"] = Prompting.DoNotAsk })[0];
        var raised = ToolPromptingReport.Build([edit], policy, new Dictionary<string, Prompting> { ["thing_edit"] = Prompting.Ask })[0];

        Assert.Contains("install policy: minimum for writes-definitions", low.Warning);
        Assert.Contains("'ask' applies", low.Warning);
        Assert.Null(raised.Warning);
        Assert.Contains("first time", ToolPromptingReport.Meaning(Prompting.AskOnce));
    }

    // ---- AG31: the model's space ----

    [Fact]
    public void The_space_names_what_is_largest_when_over_and_is_allowed_again_when_shortened()
    {
        var counter = new WordCounter();
        var big = ContextSource.Of("instructions", "role.md", string.Join(' ', Enumerable.Repeat("word", 400)));
        var small = ContextSource.Of("instructions", "role.md", "be brief");
        var skill = new SkillOffer("dbdatabuild", "Build helper", "v1");

        var over = ModelSpace.Build("helper", ModelSpace.AlwaysGiven([big], new Product().Tools(), [skill], [], BoundKnowledge.Empty), 400, counter);
        var ok = ModelSpace.Build("helper", ModelSpace.AlwaysGiven([small], new Product().Tools(), [skill], [], BoundKnowledge.Empty), 4000, counter);

        Assert.True(over.Over);
        Assert.Contains("instructions role.md (400 tokens)", over.Refusal);
        Assert.Equal("instructions", over.Groups[0].Name);
        Assert.Equal("a word count", over.Basis);
        Assert.False(ok.Over);
        Assert.Null(ok.Refusal);
        Assert.Contains(ok.Groups, g => g.Name == "tool");
        Assert.Contains(ok.Groups, g => g.Name == "skill");
    }

    // ---- AG32: skills ----

    [Fact]
    public void A_skill_shows_its_description_and_size_exports_as_a_file_and_one_that_does_not_fit_is_refused_by_name()
    {
        const string text = "---\nname: dbdatabuild\ndescription: Build projects\n---\nUse compile before deploy.\n";
        var (skill, _) = SkillReader.Read(text, "skills/dbdatabuild/SKILL.md");
        var (huge, _) = SkillReader.Read($"---\nname: huge\ndescription: Too long\n---\n{string.Join(' ', Enumerable.Repeat("word", 500))}\n", "skills/huge/SKILL.md");
        var counter = new WordCounter();

        var views = SkillViews.Build([(skill!, text), (huge!, "x")], 300, 50, 20, counter).ToDictionary(v => v.Name);

        Assert.True(views["dbdatabuild"].Fits);
        Assert.Equal([".claude/skills/dbdatabuild/SKILL.md"], views["dbdatabuild"].ExportFiles.Keys);
        Assert.Equal(text, views["dbdatabuild"].ExportFiles[".claude/skills/dbdatabuild/SKILL.md"]);
        Assert.Equal("Build projects", views["dbdatabuild"].Description);
        Assert.False(views["huge"].Fits);
        Assert.Contains("'huge'", views["huge"].Refusal);
    }

    // ---- AG17: the policy as a file, and what a change means ----

    [Fact]
    public void A_policy_written_by_the_editor_is_read_back_equal()
    {
        var policy = new AgentPolicy(
            new PromptingPolicy(new Dictionary<Traits, Prompting> { [Traits.WritesData] = Prompting.Ask, [Traits.Deletes] = Prompting.Ask }, new Dictionary<string, Prompting> { ["connection_deploy"] = Prompting.AskOnce }),
            new PolicyLimits(new LimitSet(Turns: 50, Cost: 2.5m), new LimitSet(Tokens: 1_000_000), new LimitSet(ToolCalls: 5000, Cost: 100m), 70), DataRule.Refuse);

        var text = PolicyEditing.Write(policy);
        var back = AgentPolicyLoader.Load(text, "agent-policy.yml");

        Assert.True(back.Ok, string.Join('\n', back.Diagnostics));
        Assert.Equal(policy.Prompting.Minimums.OrderBy(m => m.Key), back.Policy!.Prompting.Minimums.OrderBy(m => m.Key));
        Assert.Equal(policy.Prompting.Tools, back.Policy.Prompting.Tools);
        Assert.Equal(policy.Limits, back.Policy.Limits);
        Assert.Equal(DataRule.Refuse, back.Policy.CloudReadsData);
        Assert.Equal(text, PolicyEditing.Write(back.Policy));
    }

    [Fact]
    public void An_empty_policy_is_only_the_format_line_and_reads()
    {
        var text = PolicyEditing.Write(AgentPolicy.None);
        Assert.Equal("agent-policy: 1\n", text);
        Assert.True(AgentPolicyLoader.Load(text, "p.yml").Ok);
    }

    [Fact]
    public void A_lower_minimum_a_raised_limit_and_a_relaxed_data_rule_loosen_and_say_what_that_means()
    {
        var strict = new AgentPolicy(new PromptingPolicy(new Dictionary<Traits, Prompting> { [Traits.WritesData] = Prompting.Ask }, new Dictionary<string, Prompting>()),
            new PolicyLimits(new LimitSet(Turns: 10), LimitSet.None, LimitSet.None, 80), DataRule.Refuse);
        var loose = new AgentPolicy(new PromptingPolicy(new Dictionary<Traits, Prompting> { [Traits.WritesData] = Prompting.AskOnce }, new Dictionary<string, Prompting>()),
            new PolicyLimits(new LimitSet(Turns: 20), LimitSet.None, LimitSet.None, 90), DataRule.Allow);

        var changes = PolicyEditing.Describe(strict, loose);

        Assert.All(changes, c => Assert.True(c.Loosens));
        Assert.Contains(changes, c => c.Text.StartsWith("Lowers the minimum for writes-data from ask to ask-once") && c.Text.Contains("asks once per conversation instead of every time"));
        Assert.Contains(changes, c => c.Text.Contains("Raises the conversation limit on model turns from 10 to 20"));
        Assert.Contains(changes, c => c.Text.Contains("cloud provider") && c.Text.Contains("rows and values can reach the provider"));
        Assert.Contains(changes, c => c.Setting == "notify at");
    }

    [Fact]
    public void A_stricter_policy_has_no_loosening_and_an_unchanged_one_has_no_change()
    {
        var none = AgentPolicy.None;
        var strict = new AgentPolicy(new PromptingPolicy(new Dictionary<Traits, Prompting> { [Traits.WritesData] = Prompting.Ask }, new Dictionary<string, Prompting>()), new PolicyLimits(new LimitSet(Turns: 10), LimitSet.None, LimitSet.None, 80), DataRule.Ask);

        Assert.DoesNotContain(PolicyEditing.Describe(none, strict), c => c.Loosens);
        Assert.Equal(3, PolicyEditing.Describe(none, strict).Count);
        Assert.Empty(PolicyEditing.Describe(strict, strict));
        Assert.Contains(PolicyEditing.Describe(strict, none), c => c.Loosens && c.Text.StartsWith("Lowers the minimum for writes-data from ask to none"));
    }

    // ---- AG15: the approval desk ----

    private sealed class Ledger : IApprovalLedger
    {
        public List<Guid> Ids { get; } = [];

        public List<string> Answers { get; } = [];

        public List<Guid> Expiries { get; } = [];

        public string AllowedPerson { get; set; } = "ada";

        public Guid Requested(string? conversationId, ApprovalPrompt prompt) { var id = Guid.NewGuid(); Ids.Add(id); return id; }

        public bool Answered(Guid id, bool approved, string by, string? note, bool anyPerson)
        {
            if (by != AllowedPerson && !anyPerson) return false;
            Answers.Add($"{by}:{approved}");
            return true;
        }

        public void Expired(Guid id) => Expiries.Add(id);
    }

    private static ApprovalPrompt Prompt() => new("thing_deploy", "command thing deploy", Args.Of("x"), Traits.WritesData, "ada", "helper", "v1");

    [Fact]
    public async Task A_waiting_step_is_listed_and_answered_only_by_the_person_it_is_for()
    {
        var ledger = new Ledger();
        var desk = new ApprovalDesk(ledger);
        var asking = desk.ApproverFor("c1").AskAsync(Prompt(), CancellationToken.None);
        var pending = Assert.Single(desk.Pending());
        Assert.Equal("c1", pending.ConversationId);
        Assert.Empty(desk.Pending("bob"));

        Assert.False(desk.Answer(pending.Id, true, "bob"));
        Assert.False(asking.IsCompleted);
        Assert.True(desk.Answer(pending.Id, true, "ada", "ok"));

        var decision = await asking;
        Assert.True(decision.Approved);
        Assert.Empty(desk.Pending());
        Assert.False(desk.Answer(pending.Id, false, "ada"), "the first answer stands");
    }

    [Fact]
    public async Task A_step_nobody_answers_in_time_is_a_no_and_is_expired_in_the_record()
    {
        var ledger = new Ledger();
        var desk = new ApprovalDesk(ledger, TimeSpan.FromMilliseconds(50));

        var decision = await desk.ApproverFor("c1").AskAsync(Prompt(), CancellationToken.None);

        Assert.False(decision.Approved);
        Assert.Contains("nobody answered", decision.Note);
        Assert.Single(ledger.Expiries);
        Assert.Empty(desk.Pending());
    }

    // ---- AG14: a session ----

    private static AgentSession Session(IChatClient client, IConversationLog log, IApprover? approver, AgentPolicy? policy = null, string yaml = Agent, ModelPrice? price = null, IUsageLedger? ledger = null) =>
        new(new AgentRunContext("ada", "helper", "v7", "c1"), Definition(yaml), client, new Product().Tools(), new AgentSessionOptions
        { Policy = policy ?? AgentPolicy.None, Log = log, Approver = approver, Price = price, Model = "scripted", Ledger = ledger });

    [Fact]
    public async Task A_message_runs_the_agent_and_the_log_gets_the_words_a_record_per_model_turn_and_the_events_naming_person_agent_and_version()
    {
        var log = new InMemoryConversationLog();
        var client = new ScriptedChatClient(ScriptStep.Call("thing_show", Args.Of("widget")).WithUsage(100, 10), ScriptStep.Say("It is a widget.").WithUsage(150, 20));
        var session = Session(client, log, null, price: new ModelPrice(1m, 2m));

        var result = await session.SendAsync("what is widget?");

        Assert.Equal(AgentStop.Completed, result.Stop);
        Assert.Equal(["user", "assistant"], log.Texts.Select(t => t.Role));
        Assert.Equal("It is a widget.", log.Texts[1].Content);
        Assert.Equal(2, log.Turns.Count);
        Assert.Equal((100L, 10L, "scripted"), (log.Turns[0].In, log.Turns[0].Out, log.Turns[0].Model));
        Assert.Equal((100 * 1m + 10 * 2m) / 1_000_000m, log.Turns[0].Cost);
        Assert.StartsWith("platform/platform-rules@", log.Turns[0].Record);
        Assert.Equal(("open", "c1"), (log.Statuses[^1].Status, log.Statuses[^1].Conversation));
        Assert.All(log.Recorder.Events, e => Assert.Equal(("ada", "helper", "v7"), (e.Context.Person, e.Context.Agent, e.Context.AgentVersion)));
        Assert.Contains(log.Recorder.Events, e => e.Kind == AgentEventKinds.ToolCalled && e.Tool == "thing_show");
        Assert.Contains(log.Recorder.Events, e => e.Kind == AgentEventKinds.ContextGiven);
    }

    [Fact]
    public async Task A_write_waits_for_the_person_and_a_denied_one_does_not_run()
    {
        var product = new Product();
        var log = new InMemoryConversationLog();
        var approver = new ScriptedApprover(false);
        var client = new ScriptedChatClient(ScriptStep.Call("thing_deploy", Args.Of("prod")), ScriptStep.Say("I could not deploy."));
        var session = new AgentSession(new AgentRunContext("ada", "helper", "v7", "c1"), Definition(), client, product.Tools(),
            new AgentSessionOptions { Log = log, Approver = approver });

        var result = await session.SendAsync("deploy prod");

        Assert.Equal(AgentStop.Completed, result.Stop);
        Assert.Empty(product.Ran);
        Assert.Contains("deploy", Assert.Single(approver.Asked).Preview);
        Assert.Contains(log.Recorder.Events, e => e.Kind == AgentEventKinds.Asked);
        Assert.Contains(log.Recorder.Events, e => e.Kind == AgentEventKinds.Denied);
    }

    [Fact]
    public async Task An_approved_write_runs_and_the_next_message_goes_on_from_the_conversation_so_far()
    {
        var product = new Product();
        var log = new InMemoryConversationLog();
        var client = new ScriptedChatClient(ScriptStep.Call("thing_deploy", Args.Of("prod")), ScriptStep.Say("Deployed."), ScriptStep.Say("Yes, to prod."));
        var session = new AgentSession(new AgentRunContext("ada", "helper", "v7", "c1"), Definition(), client, product.Tools(),
            new AgentSessionOptions { Log = log, Approver = new ScriptedApprover(true) });

        await session.SendAsync("deploy prod");
        await session.SendAsync("did it go to prod?");

        Assert.Equal(["deploy prod"], product.Ran);
        var second = client.Requests[^1];
        Assert.Contains(second, m => m.Role == ChatRole.User && m.Text == "deploy prod");
        Assert.Contains(second, m => m.Role == ChatRole.User && m.Text == "did it go to prod?");
        Assert.Contains(second.SelectMany(m => m.Contents), c => c is FunctionResultContent);
    }

    [Fact]
    public async Task An_unattended_session_runs_only_what_does_not_ask()
    {
        var product = new Product();
        var log = new InMemoryConversationLog();
        var client = new ScriptedChatClient(ScriptStep.Call("thing_deploy", Args.Of("prod")), ScriptStep.Say("It needs a person."));
        var session = new AgentSession(new AgentRunContext("ada", "helper", "v7", "c1"), Definition(), client, product.Tools(), new AgentSessionOptions { Log = log });

        await session.SendAsync("deploy prod");

        Assert.Empty(product.Ran);
        Assert.Contains(log.Recorder.Events, e => e.Kind == AgentEventKinds.ToolRefused && e.Detail!.Contains("unattended"));
    }

    [Fact]
    public async Task A_limit_in_the_agent_or_the_policy_ends_the_run_with_the_sentence_and_the_status_says_so()
    {
        var log = new InMemoryConversationLog();
        var client = new ScriptedChatClient(ScriptStep.Call("thing_show", Args.Of("a")), ScriptStep.Call("thing_show", Args.Of("b")), ScriptStep.Say("never"));
        var policy = AgentPolicy.None with { Limits = new PolicyLimits(new LimitSet(Turns: 1), LimitSet.None, LimitSet.None, 80) };
        var session = Session(client, log, null, policy);

        var result = await session.SendAsync("go");

        Assert.Equal(AgentStop.LimitReached, result.Stop);
        Assert.Contains("This conversation reached its limit of 1 model turns", result.StopMessage);
        Assert.Equal("limit-reached", log.Statuses[^1].Status);
    }

    [Fact]
    public async Task The_agents_own_limit_narrows_the_installs()
    {
        var yaml = Agent + "\nlimits: { turns: 1 }\n";
        var log = new InMemoryConversationLog();
        var client = new ScriptedChatClient(ScriptStep.Call("thing_show", Args.Of("a")), ScriptStep.Say("never"));
        var policy = AgentPolicy.None with { Limits = new PolicyLimits(new LimitSet(Turns: 50), LimitSet.None, LimitSet.None, 80) };

        var result = await Session(client, log, null, policy, yaml).SendAsync("go");

        Assert.Equal(AgentStop.LimitReached, result.Stop);
    }

    [Fact]
    public async Task A_model_that_fails_leaves_the_history_as_it_was_and_the_status_failed()
    {
        var log = new InMemoryConversationLog();
        var session = Session(new ScriptedChatClient(), log, null);

        await Assert.ThrowsAnyAsync<Exception>(() => session.SendAsync("hello"));

        Assert.Empty(session.History);
        Assert.Equal("failed", log.Statuses[^1].Status);
    }

    [Fact]
    public async Task A_conversation_resumed_from_kept_words_goes_on_with_them()
    {
        var log = new InMemoryConversationLog();
        var client = new ScriptedChatClient(ScriptStep.Say("Still here."));
        var session = Session(client, log, null);
        session.Resume([("user", "hi"), ("assistant", "hello"), ("tool", "ignored")]);

        await session.SendAsync("and now?");

        var sent = client.Requests[0].Where(m => m.Role != ChatRole.System).Select(m => m.Text).ToList();
        Assert.Equal(["hi", "hello", "and now?"], sent);
    }
}
