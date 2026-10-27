using System.Text.Json.Nodes;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// Which schema describes which file of a project (the same table as <c>.vscode/settings.json</c>, which a test compares), and the schemas themselves, for the console's forms (plan 13, W05).
/// The first pattern that matches a path wins, so the folder file is listed before the model glob that would also match it.
/// </summary>
internal static class FileSchemas
{
    /// <param name="Pattern">A project-relative path pattern: <c>**</c> crosses folders, <c>*</c> does not.</param>
    /// <param name="Schema">The schema's file name under <c>schemas/</c>.</param>
    /// <param name="Kind">What the file is called on screen.</param>
    public sealed record Association(string Pattern, string Schema, string Kind);

    public static readonly IReadOnlyList<Association> All =
    [
        new("models/**/_dbdatabuild.yml", "folder.schema.json", "folder settings"),
        new("models/**/*.yml", "model.schema.json", "model"),
        new("tests/models/**/*.yml", "model-test.schema.json", "model test"),
        new("dbdatabuild.yml", "config.schema.json", "project"),
        new("connections/*.yaml", "connection.schema.json", "connection"),
        new("answers.yml", "answers.schema.json", "answers"),
    ];

    /// <summary>The document the management API serves: the associations and every schema by file name.</summary>
    public static JsonObject Document()
    {
        var schemas = new JsonObject();
        foreach (var (path, content) in AgentKitCommand.Files().Where(f => f.Path.StartsWith("schemas/", StringComparison.Ordinal) && f.Path != "schemas/output.schema.json"))
            schemas[path["schemas/".Length..]] = JsonNode.Parse(content);
        var associations = new JsonArray(All.Select(a => (JsonNode)new JsonObject { ["pattern"] = a.Pattern, ["schema"] = a.Schema, ["kind"] = a.Kind }).ToArray());
        return new JsonObject { ["associations"] = associations, ["schemas"] = schemas };
    }
}
