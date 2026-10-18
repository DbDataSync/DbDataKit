using System.Text.Json;
using System.Text.Json.Nodes;
using Json.Schema;

namespace DbDataBuild.Tests.Conformance;

/// <summary>Every `--format json` document the suite sees must satisfy schemas/output.schema.json (and the metadata schema it refers to).</summary>
internal static class OutputSchemas
{
    private static readonly Lazy<JsonSchema> Output = new(() =>
    {
        var dir = Path.Combine(FindRoot(), "schemas");
        SchemaRegistry.Global.Register(JsonSchema.FromFile(Path.Combine(dir, "metadata.schema.json")));
        return JsonSchema.FromFile(Path.Combine(dir, "output.schema.json"));
    });

    private static string FindRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "DbDataBuild.sln"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("The repository root (DbDataBuild.sln) was not found above the test binaries.");
    }

    public static void Check(string command, string document)
    {
        var node = JsonNode.Parse(document) ?? throw new InvalidOperationException($"`{command}` printed no JSON document.");
        var result = Output.Value.Evaluate(JsonSerializer.SerializeToNode(node), new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return;
        var why = string.Join("\n", result.Details.Where(d => d.Errors != null).OrderBy(d => d.InstanceLocation.ToString().StartsWith("/data", StringComparison.Ordinal) ? 0 : 1).SelectMany(d => d.Errors!.Select(e => $"{d.InstanceLocation}: {e.Key}: {e.Value}")).Take(8));
        throw new Xunit.Sdk.XunitException($"`{command}` output does not satisfy output.schema.json:\n{why}\n{document}");
    }
}
