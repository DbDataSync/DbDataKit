using System.Text.Json.Nodes;
using DbDataBuild.Core;

namespace DbDataBuild.Cli.Mcp;

/// <summary>What the server offers to read: the skill an agent follows, the JSON Schemas of every file and command output (the agent kit, embedded in the executable), and the long explanation of each diagnostic code.</summary>
internal static class Resources
{
    private const string Skill = "dbdatabuild://skill", SchemaPrefix = "dbdatabuild://schemas/", ExplainPrefix = "dbdatabuild://explain/";

    public static IEnumerable<JsonNode> Listed()
    {
        yield return Entry(Skill, "skill", "How to work in a dbdatabuild project: the rules, the loop, how to write models and read plans", "text/markdown");
        foreach (var (path, _) in AgentKitCommand.Files().Where(f => f.Path.StartsWith("schemas/", StringComparison.Ordinal)))
            yield return Entry(SchemaPrefix + path["schemas/".Length..], path["schemas/".Length..], $"JSON Schema {path["schemas/".Length..]}", "application/json");
    }

    public static IEnumerable<JsonNode> Templates()
    {
        yield return new JsonObject { ["uriTemplate"] = ExplainPrefix + "{code}", ["name"] = "explain", ["description"] = "The long explanation of a diagnostic code (DDB-214): what it means, what is supported, how to fix it", ["mimeType"] = "text/plain" };
    }

    public static JsonObject Read(string? uri)
    {
        if (uri == null) throw new McpException(-32602, "resources/read needs a uri");
        var files = AgentKitCommand.Files();
        string? text = null, mime = "text/plain";
        if (uri == Skill) { text = files.FirstOrDefault(f => f.Path == "SKILL.md").Content; mime = "text/markdown"; }
        else if (uri.StartsWith(SchemaPrefix, StringComparison.Ordinal)) { text = files.FirstOrDefault(f => f.Path == "schemas/" + uri[SchemaPrefix.Length..]).Content; mime = "application/json"; }
        else if (uri.StartsWith(ExplainPrefix, StringComparison.Ordinal) && DiagnosticCatalog.All.FirstOrDefault(d => string.Equals(d.Code, uri[ExplainPrefix.Length..], StringComparison.OrdinalIgnoreCase)) is { } d)
            text = $"{d.Code}  {d.Title}\n\n{d.Explanation}\n\nSupported: {d.Supported}\nFix: {d.Fix}\n";
        if (text == null) throw new McpException(-32002, $"Resource not found: {uri}");
        return new JsonObject { ["contents"] = new JsonArray(new JsonObject { ["uri"] = uri, ["mimeType"] = mime, ["text"] = text }) };
    }

    private static JsonObject Entry(string uri, string name, string description, string mime) =>
        new() { ["uri"] = uri, ["name"] = name, ["description"] = description, ["mimeType"] = mime };
}
