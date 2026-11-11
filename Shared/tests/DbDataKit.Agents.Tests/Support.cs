using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Cli;
using Microsoft.Extensions.AI;

namespace DbDataKit.Agents.Tests;

/// <summary>A small product to bind tools from: commands of every kind of effect, and what ran.</summary>
internal sealed class Product
{
    public List<string> Ran { get; } = [];

    public NounRouter Router() => new("demo",
    [
        new Noun("thing", "Things",
        [
            MakeVerb("show", EffectClass.OfflineOnly, "<name>"),
            MakeVerb("rows", EffectClass.TargetReadOnly, ""),
            MakeVerb("edit", EffectClass.RepoFilesOnly, "<name>"),
            MakeVerb("deploy", EffectClass.TargetWrites, "<name>"),
            new Verb("notify", "Tell the channel", EffectClass.OfflineOnly, "", (a, c) => { Ran.Add("notify"); return Task.FromResult(0); }, DataSchemas.None, Traits.ReadsDefinitions | Traits.Outward),
        ]),
    ], "1");

    private Verb MakeVerb(string name, EffectClass effect, string usage) =>
        new(name, $"{name} a thing", effect, usage, async (a, c) =>
        {
            Ran.Add($"{name} {string.Join(' ', a)}".Trim());
            c.Data["name"] = a.FirstOrDefault();
            await c.Out.WriteLineAsync($"{name} {a.FirstOrDefault()}");
            return 0;
        }, DataSchemas.Object([("name", DataSchemas.String)]));

    public IReadOnlyList<AgentTool> Tools(bool allowWrites = true, bool refuseData = false) =>
        CommandTools.From(Router(), new McpOptions(new HashSet<string>(), allowWrites, RefuseData: refuseData));
}

internal static class Transcript
{
    /// <summary>The messages as lines a golden can hold: who said what, which tool was called with which arguments, and the first line of each result.</summary>
    public static string Render(IEnumerable<ChatMessage> messages)
    {
        var lines = new List<string>();
        foreach (var message in messages)
            foreach (var content in message.Contents)
                switch (content)
                {
                    case TextContent t: lines.Add($"{message.Role.Value}: {t.Text.Split('\n')[0]}"); break;
                    case FunctionCallContent c: lines.Add($"{message.Role.Value} calls {c.Name} {System.Text.Json.JsonSerializer.Serialize(c.Arguments)}"); break;
                    case FunctionResultContent r: lines.Add($"{message.Role.Value} result: {r.Result?.ToString()?.Split('\n')[0]}"); break;
                }
        return string.Join('\n', lines);
    }

    public static IEnumerable<string> ResultTexts(IEnumerable<ChatMessage> messages) =>
        messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(r => r.Result?.ToString() ?? "");
}

internal static class Args
{
    public static JsonObject Of(params string[] args) => new() { ["args"] = new JsonArray([.. args.Select(a => (JsonNode)JsonValue.Create(a)!)]) };
}

/// <summary>A counter whose count is the number of words, so a test can say exactly what is over.</summary>
internal sealed class WordCounter : ITokenCounter
{
    public int Count(string text) => text.Split([' ', '\n'], StringSplitOptions.RemoveEmptyEntries).Length;

    public string Basis => "a word count";
}
