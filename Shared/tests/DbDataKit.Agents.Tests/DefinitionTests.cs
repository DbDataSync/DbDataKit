using System.Text.Json;
using DbDataKit.Agents;
using DbDataKit.Cli;
using DbDataKit.Config.Yaml;
using Json.Schema;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG09: the strict readers of <c>agent/1</c> and <c>agent-policy/1</c>, the schemas, and <c>agent check</c>.</summary>
public sealed class DefinitionTests
{
    private const string Complete = """
        agent: 1
        name: build-helper
        title: Build helper
        description: Helps with Build projects
        owner: platform-team
        audience: back-end
        model: { slot: default }
        instructions: [instructions/role.md]
        skills: [dbdatabuild]
        tools:
          - command: project compile
          - command: connection deploy
            prompting: ask
          - action: add-comment
            prompting: do-not-ask
        scopes:
          build-projects: all
          semantic-models: [Sales]
          store-models: ["Customers.Orders:read", Customers]
        knowledge: { from: [install, project], exclude: [old], always: [howto.md] }
        memory: { person: true, workspace: true }
        subagents: [semantic-modeler]
        limits: { turns: 60, cost: 5.00 }
        output: [table, vega-lite]
        """;

    [Fact]
    public void A_complete_definition_loads()
    {
        var result = AgentDefinitionLoader.Load(Complete, "agents/build-helper/agent.yml");

        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        var d = result.Definition!;
        Assert.Equal(("build-helper", AgentAudience.BackEnd, "default"), (d.Name, d.Audience, d.ModelSlot));
        Assert.Equal(["instructions/role.md"], d.Instructions);
        Assert.Equal(["dbdatabuild"], d.Skills);
        Assert.Equal(["project_compile", "connection_deploy", "add_comment"], d.Tools.Select(t => t.ToolName));
        Assert.Equal([null, Prompting.Ask, Prompting.DoNotAsk], d.Tools.Select(t => t.Prompting));
        Assert.Equal(["command", "command", "action"], d.Tools.Select(t => t.Kind));
        Assert.Equal(["build-projects", "semantic-models", "store-models"], d.Scopes.Select(s => s.Kind));
        Assert.True(d.Scopes[0].All);
        Assert.Equal(["Customers.Orders:read", "Customers"], d.Scopes[2].Names);
        Assert.Equal(["install", "project"], d.Knowledge.From);
        Assert.Equal(["old"], d.Knowledge.Exclude);
        Assert.Equal(["howto.md"], d.Knowledge.Always);
        Assert.Equal(new AgentMemory(true, true), d.Memory);
        Assert.Equal(["semantic-modeler"], d.Subagents);
        Assert.Equal(new LimitSet(Turns: 60, Cost: 5.00m), d.Limits);
        Assert.Equal(["table", "vega-lite"], d.Output);
        Assert.Equal(11, d.Tools[0].Line);                                                       // a tool knows where it is written
    }

    [Theory]
    [InlineData("agent: 1\nname: Bad Name\naudience: back-end\nmodel: { slot: d }", 2, "not an agent name")]
    [InlineData("agent: 1\nname: a\naudience: middle\nmodel: { slot: d }", 3, "not an audience")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\nbogus: 1", 5, "bogus")]
    [InlineData("agent: 1\nname: a\naudience: back-end", 1, "model")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: default", 4, "`model` is a mapping")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\ntools:\n  - command: x\n    prompting: sometimes", 7, "not a prompting setting")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\ntools:\n  - command: x\n    action: y", 6, "either a `command` or an `action`")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\ntools:\n  - prompting: ask", 6, "names a `command` or an `action`")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\ntools:\n  - command: x\n  - command: x", 7, "more than once")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\nscopes: { warehouses: all }", 5, "warehouses")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\nscopes: { store-models: [\"A:sudo\"] }", 5, "not one")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\nlimits: { turns: 0 }", 5, "whole number")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\nlimits: { cost: -1 }", 5, "above zero")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\ninstructions: [../secret.md]", 5, "inside the agent's project")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\noutput: [pie]", 5, "not a view kind")]
    [InlineData("agent: 1\nname: a\naudience: back-end\nmodel: { slot: d }\nmemory: { person: maybe }", 5, "`true` or `false`")]
    public void A_bad_part_is_refused_with_file_line_and_words(string yaml, int line, string words)
    {
        var result = AgentDefinitionLoader.Load(yaml, "agent.yml");

        Assert.False(result.Ok);
        Assert.Null(result.Definition);
        var error = result.Diagnostics.First(d => d.Severity == ConfigSeverity.Error);
        Assert.Equal("agent.yml", error.Path);
        Assert.Equal(line, error.Line);
        Assert.Contains(words, error.Message + " " + error.Fix);
    }

    [Fact]
    public void A_newer_format_and_an_empty_file_are_refused_naming_the_file()
    {
        var newer = AgentDefinitionLoader.Load("agent: 2\nname: a\naudience: back-end\nmodel: { slot: d }", "agents/x/agent.yml").Diagnostics.Single();
        Assert.Equal(AgentDefinitionLoader.FormatNumber, newer.Code);
        Assert.Contains("agents/x/agent.yml", newer.Message);
        Assert.Contains("format 2", newer.Message);
        Assert.Contains("format 1", newer.Message);

        Assert.Equal(AgentDefinitionLoader.BadAgent, AgentDefinitionLoader.Load("", "a.yml").Diagnostics.Single().Code);
        Assert.Equal(AgentDefinitionLoader.FormatNumber, AgentDefinitionLoader.Load("name: a", "a.yml").Diagnostics.Single().Code);
    }

    // ---- agent check

    private static AgentCatalog Catalog(Func<string, string, bool>? scopes = null) => new(
        new Dictionary<string, Traits>
        {
            ["project_compile"] = Traits.ReadsDefinitions,
            ["connection_deploy"] = TraitTable.For(EffectClass.TargetWrites),
            ["add_comment"] = Traits.WritesData,
        },
        new HashSet<string> { "dbdatabuild" }, new HashSet<string> { "semantic-modeler", "build-helper" }, scopes ?? ((_, _) => true));

    [Fact]
    public void A_definition_whose_parts_all_exist_checks_clean()
    {
        var definition = AgentDefinitionLoader.Load(Complete, "agent.yml").Definition!;

        Assert.Empty(AgentChecker.Check(definition, "agent.yml", Catalog()));
    }

    [Fact]
    public void A_tool_a_skill_a_subagent_and_a_scope_that_do_not_exist_are_each_refused_where_they_are_written()
    {
        var yaml = Complete.Replace("project compile", "project compil").Replace("[dbdatabuild]", "[dbdatabild]").Replace("[semantic-modeler]", "[semantic-modeller]");
        var definition = AgentDefinitionLoader.Load(yaml, "agent.yml").Definition!;

        var diags = AgentChecker.Check(definition, "agent.yml", Catalog((kind, name) => name != "Sales"));

        Assert.Equal([AgentChecker.UnknownTool, AgentChecker.UnknownSkill, AgentChecker.UnknownSubagent, AgentChecker.UnresolvedScope], diags.Select(d => d.Code));
        var tool = diags[0];
        Assert.Equal(11, tool.Line);
        Assert.Contains("Did you mean `project compile`?", tool.Message);
        Assert.Contains("Did you mean `dbdatabuild`?", diags[1].Message);
        Assert.Contains("Did you mean `semantic-modeler`?", diags[2].Message);
        Assert.Contains("`Sales`", diags[3].Message);
        Assert.Equal(18, diags[3].Line);
    }

    [Fact]
    public void A_prompting_setting_below_the_policy_minimum_is_refused_with_a_suggestion()
    {
        var definition = AgentDefinitionLoader.Load(Complete, "agent.yml").Definition!;
        var policy = new AgentPolicy(new PromptingPolicy(new Dictionary<Traits, Prompting> { [Traits.WritesData] = Prompting.Ask }, new Dictionary<string, Prompting>()), PolicyLimits.None, DataRule.Allow);

        var diags = AgentChecker.Check(definition, "agent.yml", Catalog(), policy);

        var d = Assert.Single(diags);                                                         // add_comment says do-not-ask and writes data
        Assert.Equal(AgentChecker.BelowMinimum, d.Code);
        Assert.Equal(14, d.Line);
        Assert.Contains("`do-not-ask`", d.Message);
        Assert.Contains("minimum `ask`", d.Message);
        Assert.Contains("only an administrator can lower the minimum", d.Fix);
    }

    [Fact]
    public void An_agent_is_not_its_own_subagent_and_a_store_scope_is_resolved_without_its_operation()
    {
        var definition = AgentDefinitionLoader.Load(Complete.Replace("[semantic-modeler]", "[build-helper]"), "agent.yml").Definition!;
        var asked = new List<string>();

        var diags = AgentChecker.Check(definition, "agent.yml", Catalog((kind, name) => { asked.Add($"{kind}/{name}"); return true; }));

        Assert.Contains("cannot be its own subagent", Assert.Single(diags).Message);
        Assert.Contains("store-models/Customers.Orders", asked);                               // `:read` is not part of the name
    }

    // ---- agent-policy/1

    private const string Policy = """
        agent-policy: 1
        prompting:
          minimums:
            writes-definitions: ask-once
            writes-data: ask
          tools:
            connection_deploy: ask
        limits:
          conversation: { turns: 100, tool-calls: 300, tokens: 2000000, cost: 20.00 }
          person: { cost: 100.00 }
          install: { cost: 2000.00 }
          notify-at: 90
        data-rules:
          cloud-providers: { reads-data: refuse }
        """;

    [Fact]
    public void A_complete_policy_loads()
    {
        var result = AgentPolicyLoader.Load(Policy, "policy/agent-policy.yml");

        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        var p = result.Policy!;
        Assert.Equal(Prompting.AskOnce, p.Prompting.Minimums[Traits.WritesDefinitions]);
        Assert.Equal(Prompting.Ask, p.Prompting.Minimums[Traits.WritesData]);
        Assert.Equal(Prompting.Ask, p.Prompting.Tools["connection_deploy"]);
        Assert.Equal(new LimitSet(100, 300, 2_000_000, 20.00m), p.Limits.Conversation);
        Assert.Equal(new LimitSet(Cost: 100.00m), p.Limits.Person);
        Assert.Equal(new LimitSet(Cost: 2000.00m), p.Limits.Install);
        Assert.Equal(90, p.Limits.NotifyAtPercent);
        Assert.Equal(DataRule.Refuse, p.CloudReadsData);
        Assert.True(p.CloudFallback);
    }

    [Fact]
    public void A_policy_can_refuse_the_fall_through_to_a_cloud_provider_and_it_reads_back_and_is_described_as_a_tightening()
    {
        var tenant = AgentPolicyLoader.Load("agent-policy: 1\ndata-rules:\n  cloud-providers:\n    fallback: refuse\n", "p.yml").Policy!;
        Assert.False(tenant.CloudFallback);
        Assert.False(tenant.AllowsFallbackTo(cloud: true));
        Assert.True(tenant.AllowsFallbackTo(cloud: false));
        Assert.True(AgentPolicy.None.AllowsFallbackTo(cloud: true));
        Assert.False(AgentPolicyLoader.Load(PolicyEditing.Write(tenant), "p.yml").Policy!.CloudFallback);
        var tightens = PolicyEditing.Describe(AgentPolicy.None, tenant).Single();
        Assert.False(tightens.Loosens);
        Assert.True(PolicyEditing.Describe(tenant, AgentPolicy.None).Single().Loosens);
        var bad = AgentPolicyLoader.Load("agent-policy: 1\ndata-rules:\n  cloud-providers: { fallback: never }", "p.yml");
        Assert.Contains(bad.Diagnostics, d => d.Message.Contains("not a fallback rule"));
    }

    [Theory]
    [InlineData("agent-policy: 1\nprompting:\n  minimums:\n    writes-stuff: ask", 4, "not a trait")]
    [InlineData("agent-policy: 1\nprompting:\n  minimums:\n    writes-data: always", 4, "not a prompting setting")]
    [InlineData("agent-policy: 1\nprompting:\n  tools:\n    x: never", 4, "not a prompting setting")]
    [InlineData("agent-policy: 1\nlimits:\n  notify-at: 100", 3, "percentage")]
    [InlineData("agent-policy: 1\nlimits:\n  person: { turns: 0 }", 3, "whole number")]
    [InlineData("agent-policy: 1\ndata-rules:\n  cloud-providers: { reads-data: maybe }", 3, "not a data rule")]
    [InlineData("agent-policy: 1\nbogus: 1", 2, "bogus")]
    public void A_bad_policy_is_refused_with_file_line_and_words(string yaml, int line, string words)
    {
        var result = AgentPolicyLoader.Load(yaml, "policy.yml");

        Assert.False(result.Ok);
        var error = result.Diagnostics.First(d => d.Severity == ConfigSeverity.Error);
        Assert.Equal(("policy.yml", line), (error.Path, error.Line));
        Assert.Contains(words, error.Message + " " + error.Fix);
    }

    [Fact]
    public void A_trait_that_is_misspelt_suggests_the_nearest()
    {
        var error = AgentPolicyLoader.Load("agent-policy: 1\nprompting:\n  minimums:\n    write-data: ask", "p.yml").Diagnostics.Single();

        Assert.Contains("Did you mean `writes-data`?", error.Message);
    }

    // ---- the schemas agree with the readers

    [Fact]
    public void The_agent_schema_names_the_keys_the_reader_reads()
    {
        using var schema = JsonDocument.Parse(AgentDefinitionLoader.Schema());
        var root = schema.RootElement;
        string[] Props(JsonElement e) => [.. e.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order()];

        Assert.Equal(AgentDefinitionLoader.Keys.Order(), Props(root));
        Assert.Equal(AgentDefinitionLoader.ScopeKinds.Order(), Props(root.GetProperty("properties").GetProperty("scopes")));
        Assert.Equal(AgentDefinitionLoader.KnowledgeKeys.Order(), Props(root.GetProperty("properties").GetProperty("knowledge")));
        Assert.Equal(AgentDefinitionLoader.MemoryKeys.Order(), Props(root.GetProperty("properties").GetProperty("memory")));
        Assert.Equal(AgentDefinitionLoader.LimitKeys.Order(), Props(root.GetProperty("properties").GetProperty("limits")));
        Assert.Equal(AgentDefinitionLoader.ToolKeys.Order(), Props(root.GetProperty("properties").GetProperty("tools").GetProperty("items")));
        Assert.Equal(AgentDefinitionLoader.Audiences.Order(), root.GetProperty("properties").GetProperty("audience").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).Order());
        Assert.Equal(AgentDefinitionLoader.PromptingNames.Order(), root.GetProperty("properties").GetProperty("tools").GetProperty("items").GetProperty("properties").GetProperty("prompting").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).Order());
        Assert.Equal(AgentDefinitionLoader.OutputKinds.Order(), root.GetProperty("properties").GetProperty("output").GetProperty("items").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).Order());
    }

    [Fact]
    public void The_policy_schema_names_the_keys_and_traits_the_reader_reads()
    {
        using var schema = JsonDocument.Parse(AgentPolicyLoader.Schema());
        var root = schema.RootElement;
        string[] Props(JsonElement e) => [.. e.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order()];
        var minimums = root.GetProperty("properties").GetProperty("prompting").GetProperty("properties").GetProperty("minimums");

        Assert.Equal(AgentPolicyLoader.Keys.Order(), Props(root));
        Assert.Equal(AgentPolicyLoader.PromptingKeys.Order(), Props(root.GetProperty("properties").GetProperty("prompting")));
        Assert.Equal(AgentPolicyLoader.LimitKeys.Order(), Props(root.GetProperty("properties").GetProperty("limits")));
        Assert.Equal(Enum.GetValues<Traits>().Where(t => t != Traits.None).SelectMany(t => t.Names()).Order(), Props(minimums));
        Assert.Equal(AgentPolicyLoader.DataRuleNames.Order(), root.GetProperty("properties").GetProperty("data-rules").GetProperty("properties").GetProperty("cloud-providers").GetProperty("properties").GetProperty("reads-data").GetProperty("enum").EnumerateArray().Select(e => e.GetString()!).Order());
    }

    private static bool Valid(string schemaText, string json)
    {
        using var document = JsonDocument.Parse(json);
        return JsonSchema.FromText(schemaText).Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    [Fact]
    public void The_schemas_accept_what_the_readers_accept_and_refuse_what_they_refuse()
    {
        var agent = AgentDefinitionLoader.Schema();
        Assert.True(Valid(agent, """{"agent":1,"name":"a","audience":"back-end","model":{"slot":"d"},"tools":[{"command":"x","prompting":"ask"}],"scopes":{"build-projects":"all","semantic-models":["Sales"]},"limits":{"turns":5,"cost":1.5},"knowledge":{"from":["install"]}}"""));
        Assert.False(Valid(agent, """{"agent":1,"name":"A b","audience":"back-end","model":{"slot":"d"}}"""));
        Assert.False(Valid(agent, """{"agent":1,"name":"a","audience":"middle","model":{"slot":"d"}}"""));
        Assert.False(Valid(agent, """{"agent":1,"name":"a","audience":"back-end","model":{"slot":"d"},"tools":[{"command":"x","action":"y"}]}"""));
        Assert.False(Valid(agent, """{"agent":1,"name":"a","audience":"back-end","model":{"slot":"d"},"bogus":1}"""));

        var policy = AgentPolicyLoader.Schema();
        Assert.True(Valid(policy, """{"agent-policy":1,"prompting":{"minimums":{"writes-data":"ask"},"tools":{"x":"ask-once"}},"limits":{"person":{"cost":1},"notify-at":80},"data-rules":{"cloud-providers":{"reads-data":"refuse"}}}"""));
        Assert.False(Valid(policy, """{"agent-policy":1,"prompting":{"minimums":{"writes-stuff":"ask"}}}"""));
        Assert.False(Valid(policy, """{"agent-policy":1,"limits":{"notify-at":100}}"""));
    }
}
