using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace DbDataKit.Serving.Semantic;

/// <summary>
/// What a model offers, as an Arrow table of text columns (<c>kind</c>, <c>name</c>, <c>parent</c>, <c>type</c>, <c>description</c>, <c>properties</c> as JSON): the model, its dimensions, attributes, measures, hierarchies and saved queries.
/// Flight SQL's table listing shows tables and views only, so this is how a client that has no other tooling sees the rest.
/// </summary>
internal sealed class DescribeAnswer : IServingAnswer
{
    private static readonly string[] Names = ["kind", "name", "parent", "type", "description", "properties"];
    private readonly RecordBatch _batch;

    public DescribeAnswer(IReadOnlyList<ServingObject> objects, ServingProvenance provenance)
    {
        Provenance = provenance;
        Schema = new Schema([.. Names.Select(n => new Field(n, StringType.Default, true))], new Dictionary<string, string> { [SemanticAnswer.MetadataPrefix + "status"] = "described" });
        var columns = Names.Select(_ => new StringArray.Builder()).ToArray();
        foreach (var o in objects)
        {
            columns[0].Append(o.Kind.ToString().ToLowerInvariant());
            columns[1].Append(string.Join('.', o.Path.Skip(1).DefaultIfEmpty(o.Path[0])));
            columns[2].Append(o.Path.Count > 2 ? string.Join('.', o.Path.Skip(1).Take(o.Path.Count - 2)) : null);
            columns[3].Append(o.Properties?.GetValueOrDefault("type"));
            columns[4].Append(o.Description);
            columns[5].Append(o.Properties is { Count: > 0 } p ? new JsonObject(p.Select(kv => new KeyValuePair<string, JsonNode?>(kv.Key, kv.Value))).ToJsonString() : null);
        }
        _batch = new RecordBatch(Schema, columns.Select(c => (IArrowArray)c.Build()).ToArray(), objects.Count);
    }

    public Schema Schema { get; }

    public ServingProvenance Provenance { get; }

    public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return _batch;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _batch.Dispose();
        return ValueTask.CompletedTask;
    }
}
