using System.Text.Json;
using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataKit.Semantic;
using DbDataKit.Semantic.Abstractions;

namespace DbDataKit.Serving.Semantic;

/// <summary>
/// The combined result of a semantic request as an Arrow table. Its columns are the request's attributes and measures under the result's own names (whole numbers Int64, numbers Double, booleans, text for everything else); the engine's status, request hash,
/// level, warnings, ranges it resolved and the steps it left (<c>remaining</c>) are in the schema's metadata under <c>qis.*</c>, as JSON. A result with no rows of its own (the engine left every step) is an empty table with the remaining steps.
/// </summary>
internal sealed class SemanticAnswer : IServingAnswer
{
    public const string MetadataPrefix = "qis.";

    private readonly RecordBatch _batch;

    public SemanticAnswer(Result result, ServingProvenance provenance)
    {
        Provenance = provenance;
        var fields = new List<Field>();
        var arrays = new List<IArrowArray>();
        if (result.Combined is { } combined)
        {
            var rows = combined.Rows;
            foreach (var column in combined.Descriptor["schema"]!["fields"]!.AsArray())
            {
                var name = column!["name"]!.GetValue<string>();
                var type = column["type"]?.GetValue<string>();
                var values = rows.Select(r => r.TryGetValue(name, out var v) ? v : null).ToList();
                var (array, arrowType) = Build(type, values);
                fields.Add(new Field(name, arrowType, true));
                arrays.Add(array);
            }
        }
        var metadata = new Dictionary<string, string>
        {
            [MetadataPrefix + "status"] = result.Status,
            [MetadataPrefix + "level"] = result.EngineLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        if (result.RequestHash is not null) metadata[MetadataPrefix + "request_hash"] = result.RequestHash;
        if (result.ReasonCode is not null) metadata[MetadataPrefix + "reason_code"] = result.ReasonCode;
        Add(metadata, "warnings", result.Warnings);
        Add(metadata, "remaining", result.Remaining);
        Add(metadata, "resolved", result.Resolved);
        if (result.Combined is { } c) metadata[MetadataPrefix + "descriptor"] = c.Descriptor.ToJsonString();
        Schema = new Schema(fields, metadata);
        _batch = new RecordBatch(Schema, arrays, arrays.Count == 0 ? 0 : arrays[0].Length);
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

    private static void Add<T>(Dictionary<string, string> metadata, string key, List<T>? items)
    {
        if (items is { Count: > 0 }) metadata[MetadataPrefix + key] = JsonSerializer.SerializeToNode(items, QisJson.Options)!.ToJsonString();
    }

    private static (IArrowArray, IArrowType) Build(string? type, List<object?> values)
    {
        switch (type)
        {
            case "integer":
                var longs = new Int64Array.Builder();
                foreach (var v in values) if (v is null) longs.AppendNull(); else longs.Append(Convert.ToInt64(v, System.Globalization.CultureInfo.InvariantCulture));
                return (longs.Build(), Int64Type.Default);
            case "number":
                var doubles = new DoubleArray.Builder();
                foreach (var v in values) if (v is null) doubles.AppendNull(); else doubles.Append(Convert.ToDouble(v, System.Globalization.CultureInfo.InvariantCulture));
                return (doubles.Build(), DoubleType.Default);
            case "boolean":
                var bools = new BooleanArray.Builder();
                foreach (var v in values) if (v is null) bools.AppendNull(); else bools.Append(Convert.ToBoolean(v, System.Globalization.CultureInfo.InvariantCulture));
                return (bools.Build(), BooleanType.Default);
            default:
                var text = new StringArray.Builder();
                foreach (var v in values) if (v is null) text.AppendNull(); else text.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) ?? "");
                return (text.Build(), StringType.Default);
        }
    }
}
