using Apache.Arrow;
using Apache.Arrow.Types;

namespace DbDataKit.Serving.Semantic;

/// <summary>The members of an attribute as an Arrow table of one column, <c>member</c>; <c>qis.truncated</c> in the schema metadata says whether there were more than the limit.</summary>
internal sealed class MembersAnswer : IServingAnswer
{
    private readonly RecordBatch _batch;

    public MembersAnswer(IReadOnlyList<string> members, bool truncated, ServingProvenance provenance)
    {
        Provenance = provenance;
        Schema = new Schema([new Field("member", StringType.Default, false)], new Dictionary<string, string>
        {
            [SemanticAnswer.MetadataPrefix + "status"] = "members",
            [SemanticAnswer.MetadataPrefix + "truncated"] = truncated ? "true" : "false",
        });
        _batch = new RecordBatch(Schema, [new StringArray.Builder().AppendRange(members).Build()], members.Count);
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
