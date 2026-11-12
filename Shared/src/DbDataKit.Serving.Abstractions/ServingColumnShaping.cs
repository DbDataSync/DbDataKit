using System.Runtime.CompilerServices;
using Apache.Arrow;
using DbDataKit.Access;

namespace DbDataKit.Serving;

/// <summary>
/// Applies a caller's column rules (plan 22a GP13) to the rows of an entity read: a hidden column is not in the answer, a nulled column is NULL with its type, a masked one is the fixed mask or the keyed hash of its value. The treatments
/// of the entity come from the entity model's <c>access</c> section (the entity is the dataset, its attribute the column) and are resolved for the caller by <see cref="ColumnTreatments.For"/>. The host puts this in front of the store
/// module's entity service, so every module that reads an entity gets the same answer.
/// </summary>
public sealed class ColumnShapedEntities(IServingEntities inner, Func<ServingContext, string, ColumnTreatments> treatments, Func<byte[]> maskKey) : IServingEntities
{
    public async Task<IServingAnswer> ReadAsync(ServingContext context, string entity, DateTimeOffset? asOf, CancellationToken cancellationToken = default)
    {
        var answer = await inner.ReadAsync(context, entity, asOf, cancellationToken).ConfigureAwait(false);
        var resolved = treatments(context, entity);
        return resolved.IsEmpty ? answer : new ColumnShapedAnswer(answer, resolved, entity, maskKey);
    }

    public Task<ServingWriteResult> WriteAsync(ServingContext context, string entity, IAsyncEnumerable<RecordBatch> rows, CancellationToken cancellationToken = default) =>
        inner.WriteAsync(context, entity, rows, cancellationToken);
}

/// <summary>An answer with a caller's column treatments applied batch by batch; the schema is the inner one without the hidden columns.</summary>
public sealed class ColumnShapedAnswer : IServingAnswer
{
    private readonly IServingAnswer _inner;
    private readonly ColumnTreatments _treatments;
    private readonly string _dataset;
    private readonly Func<byte[]> _key;
    private readonly List<int> _kept = [];

    public ColumnShapedAnswer(IServingAnswer inner, ColumnTreatments treatments, string dataset, Func<byte[]> key)
    {
        _inner = inner;
        _treatments = treatments;
        _dataset = dataset;
        _key = key;
        var fields = new List<Field>();
        for (var i = 0; i < inner.Schema.FieldsList.Count; i++)
        {
            var field = inner.Schema.FieldsList[i];
            if (treatments.Of(dataset, field.Name)?.Treatment == ColumnTreatment.Hide) continue;
            _kept.Add(i);
            fields.Add(treatments.Of(dataset, field.Name) is null ? field : new Field(field.Name, field.DataType, nullable: true, field.Metadata));
        }

        Schema = new Schema(fields, inner.Schema.Metadata);
    }

    public Schema Schema { get; }

    public ServingProvenance Provenance => _inner.Provenance;

    public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var batch in _inner.ReadBatchesAsync(cancellationToken).ConfigureAwait(false))
        {
            var arrays = new List<IArrowArray>();
            foreach (var index in _kept)
            {
                var name = batch.Schema.FieldsList[index].Name;
                arrays.Add(_treatments.Of(_dataset, name) is { } treatment ? Treat(batch.Column(index), treatment, name) : batch.Column(index));
            }

            yield return new RecordBatch(Schema, arrays, batch.Length);
        }
    }

    private IArrowArray Treat(IArrowArray array, ColumnTreatmentOf treatment, string column)
    {
        if (treatment.Treatment == ColumnTreatment.Mask && array is StringArray text)
        {
            var builder = new StringArray.Builder();
            var key = treatment.Mask == MaskKind.Hash ? _key() : [];
            for (var i = 0; i < text.Length; i++)
            {
                if (treatment.Mask == MaskKind.Fixed) builder.Append(ColumnMasks.FixedValue);
                else if (text.IsNull(i)) builder.AppendNull();
                else builder.Append(ColumnMasks.Hash(key, _dataset, column, text.GetString(i)));
            }

            return builder.Build();
        }

        return Nulls(array);
    }

    /// <summary>An array of the same type and length with every value NULL. The data buffers are zeroed, not kept: a value must not travel in a buffer that a reader ignores.</summary>
    internal static IArrowArray Nulls(IArrowArray array)
    {
        var data = array.Data;
        var buffers = data.Buffers.Select((b, i) => i == 0 ? new ArrowBuffer(new byte[(data.Length + 7) / 8]) : new ArrowBuffer(new byte[b.Length])).ToArray();
        var children = data.Children?.Select(c => Nulls(ArrowArrayFactory.BuildArray(c)).Data).ToArray();
        return ArrowArrayFactory.BuildArray(new ArrayData(data.DataType, data.Length, data.Length, data.Offset, buffers, children));
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
