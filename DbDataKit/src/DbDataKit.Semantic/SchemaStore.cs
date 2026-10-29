using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Nodes;
using Json.Schema;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Semantic;

/// <summary>
/// The contract schemas (embedded in this assembly, <c>schemas/*.schema.json</c>), loaded once and registered by $id so they can $ref each other.
/// </summary>
public static class SchemaStore
{
    private static readonly object EvaluationGate = new();
    private static readonly Lazy<Dictionary<string, JsonSchema>> All = new(Load);

    /// <summary>The schema named like its file without <c>.schema.json</c>: <c>semantic_request</c>, <c>result</c>, <c>catalog</c>, <c>view</c> and so on.</summary>
    public static JsonSchema Get(string name) => All.Value.TryGetValue(name, out var schema)
        ? schema
        : throw new KeyNotFoundException($"No schema named '{name}'; the embedded schemas are {string.Join(", ", All.Value.Keys.Order())}.");

    private static Dictionary<string, JsonSchema> Load()
    {
        var assembly = typeof(SchemaStore).Assembly;
        var all = new Dictionary<string, JsonSchema>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".schema.json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            var schema = JsonSchema.FromText(reader.ReadToEnd());
            SchemaRegistry.Global.Register(schema);
            var file = resource[..^".schema.json".Length];
            all[file[(file.LastIndexOf('.') + 1)..]] = schema;
        }
        return all;
    }

    /// <summary>Validates a document and returns its errors under the given code.</summary>
    public static List<QisError> Errors(JsonNode doc, JsonSchema schema, string code, string spec)
    {
        // A schema builds parts of itself the first time it is used, and the schemas $ref each other: two roots that share a referenced schema race on its lazy state
        // even when each is locked on its own (seen in CI as a oneOf that matched two branches). One lock for all of them; an evaluation is short.
        EvaluationResults result;
        lock (EvaluationGate) result = schema.Evaluate(doc, new EvaluationOptions { OutputFormat = OutputFormat.List });
        if (result.IsValid) return [];
        return result.Details
            .Where(d => d.HasErrors)
            .SelectMany(d => d.Errors!.Select(e => new QisError(spec, code, "$" + d.InstanceLocation, e.Value)))
            .ToList();
    }
}
