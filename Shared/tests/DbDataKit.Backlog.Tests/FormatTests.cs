using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config.Yaml;
using Json.Schema;

namespace DbDataKit.Backlog.Tests;

/// <summary>Plan 27a BK01: the strict readers of <c>backlog-workflow/1</c> and <c>work-item/1</c> name file, line and column, and their JSON Schemas agree with them.</summary>
public sealed class FormatTests
{
    private static readonly JsonSchema WorkflowSchema = JsonSchema.FromText(BacklogWorkflows.WorkflowSchema());
    private static readonly JsonSchema ItemSchema = JsonSchema.FromText(BacklogWorkflows.WorkItemSchema());

    private static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMapping m => new JsonObject(m.Entries.Select(e => KeyValuePair.Create<string, JsonNode?>(e.Key.Value, ToJson(e.Value)))),
        YamlSequence s => new JsonArray(s.Items.Select(ToJson).ToArray()),
        YamlScalar { Quoted: false, Value: "true" or "false" } b => JsonValue.Create(b.Value == "true"),
        YamlScalar { Quoted: false } sc when decimal.TryParse(sc.Value, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var n) => JsonValue.Create(n),
        YamlScalar sc => JsonValue.Create(sc.Value),
        _ => null,
    };

    private static bool Accepts(JsonSchema schema, string yaml)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, "x.yml", diagnostics)!;
        Assert.Empty(diagnostics);
        using var document = JsonDocument.Parse(ToJson(root)!.ToJsonString());
        return schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private const string MinimalWorkflow = """
        schema: backlog-workflow/1
        states:
          - { id: open, category: todo }
          - { id: finished, category: done, requires: [resolution] }
        initial: open
        transitions:
          - { from: open, to: finished, role: member }
        """;

    [Fact]
    public void The_default_workflow_loads_and_is_the_one_the_library_describes()
    {
        var workflow = BacklogWorkflows.Default;

        Assert.Equal(["backlog", "ready", "in-progress", "review", "done", "cancelled"], workflow.States.Select(s => s.Id));
        Assert.Equal("backlog", workflow.Initial);
        Assert.Equal(["assignee"], workflow.State("review")!.Requires);
        Assert.Equal(["resolution"], workflow.State("cancelled")!.Requires);
        Assert.Equal(["ready", "cancelled"], workflow.Targets("backlog", BacklogRole.Member));
        Assert.Equal(["in-progress", "cancelled"], workflow.Targets("done", BacklogRole.Lead));
        Assert.Equal(["cancelled"], workflow.Targets("done", BacklogRole.Member));
        Assert.Empty(workflow.Targets("cancelled", BacklogRole.Viewer));
        Assert.True(Accepts(WorkflowSchema, BacklogWorkflows.DefaultText()), "the default workflow is valid against its own schema");
    }

    [Fact]
    public void A_minimal_workflow_loads_and_the_schema_accepts_it()
    {
        var result = WorkflowLoader.Load(MinimalWorkflow);

        Assert.True(result.Ok, string.Join("; ", result.Diagnostics));
        Assert.Equal(["open", "finished"], result.Workflow!.States.Select(s => s.Id));
        Assert.Equal("open", result.Workflow.States[0].Title);
        Assert.True(Accepts(WorkflowSchema, MinimalWorkflow));
    }

    public static TheoryData<string, string, int, string> BadWorkflows => new()
    {
        { "schema: backlog-workflow/2\nstates: []\n", "DBD-110", 1, "version 1" },
        { "schema: other/1\n", "DBD-BKL-001", 1, "other/1" },
        { "- a\n", "DBD-BKL-001", 1, "mapping" },
        { MinimalWorkflow + "\nextra: 1\n", "DBD-104", 8, "Unknown key `extra`" },
        { MinimalWorkflow.Replace("initial: open", "initial: opne"), "DBD-BKL-002", 5, "not a state" },
        { MinimalWorkflow.Replace("{ id: finished, category: done, requires: [resolution] }", "{ id: finished, category: done, requires: [resolutoin] }"), "DBD-BKL-002", 4, "Did you mean `resolution`" },
        { MinimalWorkflow.Replace("category: todo", "category: waiting"), "DBD-BKL-002", 3, "todo, doing or done" },
        { MinimalWorkflow.Replace("{ id: finished, category: done, requires: [resolution] }", "{ id: open, category: done }"), "DBD-BKL-002", 4, "declared twice" },
        { MinimalWorkflow.Replace("category: done", "category: doing"), "DBD-BKL-002", 3, "`done` category" },
        { MinimalWorkflow.Replace("to: finished", "to: finnished"), "DBD-BKL-003", 7, "Did you mean `finished`" },
        { MinimalWorkflow.Replace("role: member", "role: admin"), "DBD-BKL-003", 7, "member or a lead" },
        { MinimalWorkflow.Replace("from: open, to: finished", "from: open, to: open"), "DBD-BKL-003", 7, "to itself" },
        { MinimalWorkflow.Replace("transitions:\n  - { from: open, to: finished, role: member }", "transitions: []"), "DBD-BKL-004", 3, "cannot be reached" },
    };

    [Theory, MemberData(nameof(BadWorkflows))]
    public void A_bad_workflow_is_refused_with_the_file_the_line_and_what_is_wrong(string yaml, string code, int line, string text)
    {
        var result = WorkflowLoader.Load(yaml, "bad/workflow.yml");

        Assert.False(result.Ok);
        var diagnostic = result.Diagnostics.First(d => d.Code == code);
        Assert.Equal("bad/workflow.yml", diagnostic.Path);
        Assert.Contains(text, diagnostic.Message, StringComparison.Ordinal);
        Assert.True(diagnostic.Line is > 0, diagnostic.ToString());
        Assert.Equal(line, diagnostic.Line);
    }

    private const string MinimalItems = """
        schema: work-item/1
        project: DATA
        items:
          - { id: e1, type: epic, title: Orders }
          - { id: s1, type: story, title: Load, epic: e1, priority: high, estimate: 1.5, assignee: ada, labels: [etl, nightly], description: Text }
        """;

    [Fact]
    public void A_work_item_file_loads_with_epics_first_and_the_schema_accepts_it()
    {
        var result = WorkItemLoader.Load(MinimalItems.Replace("- { id: e1", "- { id: s0, type: task, title: Loose }\n  - { id: e1"));

        Assert.True(result.Ok, string.Join("; ", result.Diagnostics));
        Assert.Equal("DATA", result.File!.Project);
        Assert.Equal(["e1", "s0", "s1"], result.File.Items.Select(i => i.Id));
        Assert.Equal((1.5m, "high", "e1"), (result.File.Items[2].Estimate, result.File.Items[2].Priority, result.File.Items[2].Epic));
        Assert.Equal(["etl", "nightly"], result.File.Items[2].Labels);
        Assert.True(Accepts(ItemSchema, MinimalItems));
    }

    public static TheoryData<string, string, int, string> BadItemFiles => new()
    {
        { MinimalItems.Replace("work-item/1", "work-item/3"), "DBD-110", 1, "version 1" },
        { MinimalItems.Replace("project: DATA", "project: data"), "DBD-BKL-010", 2, "project key" },
        { MinimalItems + "\n  - { id: s1, type: task, title: Twice }\n", "DBD-BKL-011", 6, "used twice" },
        { MinimalItems.Replace("type: story", "type: storey"), "DBD-BKL-011", 5, "Did you mean `story`" },
        { MinimalItems.Replace("epic: e1", "epic: nope"), "DBD-BKL-011", 5, "not an item of type epic" },
        { MinimalItems.Replace("{ id: e1, type: epic, title: Orders }", "{ id: e1, type: epic, title: Orders, epic: e1 }"), "DBD-BKL-011", 4, "cannot belong to an epic" },
        { MinimalItems.Replace("estimate: 1.5", "estimate: 1.3"), "DBD-BKL-011", 5, "steps of a half" },
        { MinimalItems.Replace("priority: high", "priority: urgent"), "DBD-BKL-011", 5, "priority" },
        { MinimalItems.Replace("labels: [etl, nightly]", "labels: [ETL]"), "DBD-BKL-011", 5, "not a label" },
        { MinimalItems.Replace("title: Load", "titel: Load"), "DBD-104", 5, "Did you mean `title`" },
    };

    [Theory, MemberData(nameof(BadItemFiles))]
    public void A_bad_work_item_file_is_refused_with_the_file_the_line_and_what_is_wrong(string yaml, string code, int line, string text)
    {
        var result = WorkItemLoader.Load(yaml, "items/bad.yml");

        Assert.False(result.Ok);
        var diagnostic = result.Diagnostics.First(d => d.Code == code);
        Assert.Equal("items/bad.yml", diagnostic.Path);
        Assert.Contains(text, diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal(line, diagnostic.Line);
    }

    [Theory]
    [InlineData("schema: backlog-workflow/1\nstates: [{ id: a, category: sometimes }]\ninitial: a\ntransitions: []\n")]
    [InlineData("schema: backlog-workflow/1\nstates: []\ninitial: a\ntransitions: []\n")]
    [InlineData("schema: backlog-workflow/1\nstates: [{ id: a, category: todo, requires: [colour] }]\ninitial: a\ntransitions: []\n")]
    [InlineData("schema: backlog-workflow/1\nstates: [{ id: a, category: todo }]\ninitial: a\ntransitions: [{ from: a, to: a, role: boss }]\n")]
    public void The_workflow_schema_refuses_what_the_reader_refuses(string yaml) => Assert.False(Accepts(WorkflowSchema, yaml));

    [Theory]
    [InlineData("schema: work-item/1\nproject: DATA\nitems: [{ id: a, type: thing, title: T }]\n")]
    [InlineData("schema: work-item/1\nproject: DATA\nitems: [{ id: a, type: task, title: T, estimate: 1.3 }]\n")]
    [InlineData("schema: work-item/1\nproject: DATA\nitems: [{ id: a, type: task, title: T, color: red }]\n")]
    [InlineData("schema: work-item/1\nproject: data\nitems: []\n")]
    public void The_work_item_schema_refuses_what_the_reader_refuses(string yaml) => Assert.False(Accepts(ItemSchema, yaml));
}
