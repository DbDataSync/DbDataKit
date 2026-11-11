using System.Text.Json.Nodes;
using DbDataKit.Cli;

namespace DbDataKit.Agents;

/// <summary>What a host's check of a view says (the library that knows the view schemas, <c>DbDataKit.Views</c>, is the host's to reference): whether it may be shown and, when not, a message the model can act on.</summary>
public sealed record ViewVerdict(bool Ok, string Message);

/// <summary>
/// The tool <c>show_view</c> (plan 27a AG22): how an agent returns a table, a matrix or a chart in an answer. The view is the widget of a dashboard with its data <b>inline</b>; it is checked against the view schemas (<c>DbDataKit.Views</c>, the closed subset, the
/// kinds the agent's <c>output:</c> allows, no address anywhere) before it is shown, and a refusal comes back to the model in words so it can correct the call. What is shown is what was checked: the host draws the arguments of an accepted call. Nothing in a view is fetched, and
/// a link in the answer's <i>text</i> is only text.
/// </summary>
public static class AnswerViewTool
{
    public const string Name = "show_view";

    public static AgentTool Create(IReadOnlyList<string> allowedKinds, Func<JsonObject, IReadOnlyList<string>, ViewVerdict> check)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("widget", "data"),
            ["properties"] = new JsonObject
            {
                ["widget"] = new JsonObject { ["type"] = "object", ["description"] = $"A dashboard widget: `type` ({string.Join(", ", allowedKinds.Select(AnswerKindName))}), `title`, `encodings` naming columns of `data`. No query and no address." },
                ["data"] = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = "The values, inline: { \"columns\": [\"region\", \"revenue\"], \"rows\": [[\"EU\", 10]] }.",
                },
            },
        };
        return new AgentTool(Name, $"Show the person a view in your answer: a {string.Join(", ", allowedKinds.Select(AnswerKindName))} made from data you give inline. The data and the widget are checked first; nothing is fetched from an address.",
            Traits.None, schema, "agent view", (arguments, _) =>
            {
                var verdict = check(arguments, allowedKinds);
                return Task.FromResult(new ToolOutcome(verdict.Message, !verdict.Ok));
            })
        { DeclaredPrompting = Prompting.DoNotAsk };
    }

    private static string AnswerKindName(string kind) => kind == "vega-lite" ? "chart" : kind;
}
