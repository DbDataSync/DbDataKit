using Apache.Arrow;
using Apache.Arrow.Types;

namespace DbDataKit.Serving.Semantic;

/// <summary>What a semantic request would run, without running it: one row per scan, with the catalog source, the engine and the text.</summary>
internal sealed class ExplainAnswer : IServingAnswer
{
    private readonly RecordBatch _batch;

    public ExplainAnswer(IReadOnlyList<ExplainedScan> scans, ServingProvenance provenance)
    {
        Provenance = provenance;
        Schema = new Schema([new Field("source", StringType.Default, false), new Field("engine", StringType.Default, false), new Field("text", StringType.Default, false)],
            new Dictionary<string, string> { [SemanticAnswer.MetadataPrefix + "status"] = "explained" });
        _batch = new RecordBatch(Schema,
        [
            new StringArray.Builder().AppendRange(scans.Select(s => s.Source)).Build(),
            new StringArray.Builder().AppendRange(scans.Select(s => s.Engine)).Build(),
            new StringArray.Builder().AppendRange(scans.Select(s => s.Text)).Build(),
        ], scans.Count);
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
