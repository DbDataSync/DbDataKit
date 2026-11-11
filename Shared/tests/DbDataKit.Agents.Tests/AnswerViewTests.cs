using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Agents.Testing;
using DbDataKit.Cli;
using DbDataKit.Views;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>Plan 27a AG22: a view an agent returns is checked against the view schemas (inline data only, no address) and a refusal is a message the model can act on; a link in text stays text.</summary>
public sealed class AnswerViewTests
{
    private static readonly AgentRunContext Context = new("alice", "analyst", "1");

    private static readonly string[] Kinds = ["table", "matrix", "vega-lite"];

    private static JsonObject Data() => (JsonObject)JsonNode.Parse("""{"columns":["region","revenue"],"rows":[["EU",10],["US",20]]}""")!;

    private static JsonObject Table() => (JsonObject)JsonNode.Parse("""{"type":"table","title":"Revenue","encodings":{"columns":[{"field":"region"},{"field":"revenue"}]}}""")!;

    private static JsonObject Chart() => (JsonObject)JsonNode.Parse("""{"type":"chart","title":"Revenue","mark":"bar","encodings":{"x":"region","y":"revenue"}}""")!;

    private static JsonObject View(JsonObject widget, JsonObject? data = null) => new() { ["widget"] = widget, ["data"] = data ?? Data() };

    private static AgentTool Tool(params string[] kinds) => AnswerViewTool.Create(kinds, (view, allowed) =>
    {
        var check = AgentAnswerViews.Check(view, allowed);
        return new ViewVerdict(check.Ok, check.Message);
    });

    [Fact]
    public void A_table_and_a_chart_with_their_data_inline_are_accepted()
    {
        Assert.True(AgentAnswerViews.Check(View(Table()), Kinds).Ok);
        var chart = AgentAnswerViews.Check(View(Chart()), Kinds);
        Assert.True(chart.Ok, chart.Message);
        Assert.Equal("vega-lite", chart.Kind);
    }

    [Theory]
    [InlineData("""{"type":"table","encodings":{"columns":[{"field":"region"}]},"title":"See https://evil.example/x"}""", "address")]
    [InlineData("""{"type":"table","encodings":{"columns":[{"field":"region"}]},"url":"https://evil.example/x"}""", "address")]
    [InlineData("""{"type":"table","encodings":{"columns":[{"field":"region"}]},"title":"//evil.example/x"}""", "address")]
    [InlineData("""{"type":"chart","mark":"bar","encodings":{"x":"region","y":"revenue"},"data":{"url":"x.csv"}}""", "address")]
    [InlineData("""{"type":"table","encodings":{"columns":[{"field":"nope"}]}}""", "nope")]
    [InlineData("""{"type":"table","encodings":{"columns":[{"field":"region"}]},"query":{"mode":"explicit","semantic":{"model":"m","sql":"select 1"}}}""", "query")]
    [InlineData("""{"type":"embed","url":"x"}""", "address")]
    [InlineData("""{"type":"image","hash":"sha256:00"}""", "not one an answer may show")]
    public void A_view_with_an_address_a_query_or_a_field_the_data_lacks_is_refused_in_words(string widget, string fragment)
    {
        var check = AgentAnswerViews.Check(View((JsonObject)JsonNode.Parse(widget)!), Kinds);
        Assert.False(check.Ok);
        Assert.StartsWith("The view was not shown.", check.Message);
        Assert.Contains(fragment, check.Message);
    }

    [Fact]
    public void An_address_in_the_data_a_kind_the_agent_does_not_allow_and_bad_data_are_refused()
    {
        var withUrl = (JsonObject)JsonNode.Parse("""{"columns":["region","revenue"],"rows":[["see http://x.example",1]]}""")!;
        Assert.Contains("address", AgentAnswerViews.Check(View(Table(), withUrl), Kinds).Message);
        Assert.Contains("may return `table`, not `vega-lite`", AgentAnswerViews.Check(View(Chart()), ["table"]).Message);
        Assert.Contains("no `output:` kinds", AgentAnswerViews.Check(View(Table()), []).Message);
        Assert.Contains("values for 2 columns", AgentAnswerViews.Check(View(Table(), (JsonObject)JsonNode.Parse("""{"columns":["region","revenue"],"rows":[["EU"]]}""")!), Kinds).Message);
        Assert.Contains("`data` is missing", AgentAnswerViews.Check(new JsonObject { ["widget"] = Table() }, Kinds).Message);
    }

    [Fact]
    public async Task A_scripted_answer_with_a_url_in_a_view_is_refused_with_a_message_the_model_acts_on_and_a_link_in_text_stays_text()
    {
        var bad = new JsonObject { ["widget"] = (JsonObject)JsonNode.Parse("""{"type":"table","encodings":{"columns":[{"field":"region"}]},"url":"https://evil.example/data.csv"}""")!, ["data"] = Data() };
        var good = View(Table());
        var client = new ScriptedChatClient(
            ScriptStep.Call(AnswerViewTool.Name, bad),
            ScriptStep.Call(AnswerViewTool.Name, good),
            ScriptStep.Say("Revenue by region is above. More at https://docs.example/revenue."));

        var run = await new AgentRunner(client, [Tool(Kinds)]).RunAsync([new ChatMessage(ChatRole.User, "revenue by region")], Context);

        Assert.Equal(AgentStop.Completed, run.Stop);
        var first = Transcript.ResultTexts(client.Requests[1]).Single();
        Assert.Contains("error=\"true\"", first);
        Assert.Contains("The view was not shown.", first);
        Assert.Contains("an address", first);
        var second = Transcript.ResultTexts(client.Requests[2]).Last();
        Assert.Contains("The view is shown.", second);
        Assert.DoesNotContain("error=\"true\"", second);
        Assert.Contains("https://docs.example/revenue", run.Text);   // text is text: shown, never fetched, never refused
    }

    [Fact]
    public void The_tool_asks_for_nothing_and_names_only_the_kinds_the_agent_may_return()
    {
        var tool = Tool("table");
        Assert.Equal(Prompting.DoNotAsk, tool.DeclaredPrompting);
        Assert.Contains("table", tool.Description);
        Assert.DoesNotContain("chart", tool.Description);
        Assert.Empty(tool.Validate(View(Table())));
        Assert.NotEmpty(tool.Validate(new JsonObject { ["widget"] = Table() }));
    }
}
