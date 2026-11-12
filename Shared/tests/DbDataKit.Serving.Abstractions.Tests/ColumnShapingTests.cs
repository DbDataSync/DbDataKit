using Apache.Arrow;
using Apache.Arrow.Ipc;
using Apache.Arrow.Types;
using DbDataKit.Access;
using Xunit;

namespace DbDataKit.Serving.Tests;

/// <summary>Plan 22a GP13: the column treatments applied to an entity read, and the cache key that holds them.</summary>
public class ColumnShapingTests
{
    private static readonly ServingContext Context = new(new ServingCaller("u1", "ivy", new HashSet<string> { "interns" }, new Dictionary<string, string>()));

    private static AccessSection Section(string columns)
    {
        var diagnostics = new List<DbDataKit.Config.Yaml.ConfigDiagnostic>();
        return AccessSectionReader.ReadText("columns:\n" + columns, "model.yaml", diagnostics)!;
    }

    private sealed class Entities : IServingEntities
    {
        public Task<IServingAnswer> ReadAsync(ServingContext context, string entity, DateTimeOffset? asOf, CancellationToken cancellationToken = default)
        {
            var schema = new Schema.Builder().Field(new Field("id", Int64Type.Default, false)).Field(new Field("email", StringType.Default, true)).Field(new Field("salary", DoubleType.Default, true)).Field(new Field("secret", StringType.Default, true)).Build();
            var batch = new RecordBatch(schema, [new Int64Array.Builder().Append(1).Append(2).Build(), new StringArray.Builder().Append("ann@example.com").AppendNull().Build(), new DoubleArray.Builder().Append(10.5).Append(20).Build(), new StringArray.Builder().Append("topsecret").Append("x").Build()], 2);
            return Task.FromResult<IServingAnswer>(new Answer(batch));
        }

        public Task<ServingWriteResult> WriteAsync(ServingContext context, string entity, IAsyncEnumerable<RecordBatch> rows, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class Answer(RecordBatch batch) : IServingAnswer
    {
        public Schema Schema => batch.Schema;

        public ServingProvenance Provenance { get; } = new("entity", null, "prod", "customers");

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return batch;
            await Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task<(Schema Schema, RecordBatch Batch)> Read(IServingEntities entities, bool secretHidden = false)
    {
        await using var answer = await entities.ReadAsync(Context, "customers", null);
        var batches = new List<RecordBatch>();
        await foreach (var b in answer.ReadBatchesAsync()) batches.Add(b);
        // through the wire format, as a client would receive it: a value in a buffer that is not exposed would still travel
        using var stream = new MemoryStream();
        using (var writer = new ArrowStreamWriter(stream, answer.Schema, leaveOpen: true)) { foreach (var b in batches) await writer.WriteRecordBatchAsync(b); await writer.WriteEndAsync(); }
        var bytes = stream.ToArray();
        if (secretHidden) Assert.DoesNotContain("topsecret", System.Text.Encoding.UTF8.GetString(bytes), StringComparison.Ordinal);
        stream.Position = 0;
        using var reader = new ArrowStreamReader(stream);
        return (reader.Schema, await reader.ReadNextRecordBatchAsync() ?? throw new InvalidOperationException());
    }

    [Fact]
    public async Task An_entity_read_hides_nulls_and_masks_columns_for_the_caller()
    {
        var section = Section("""
              - { subject: interns, dataset: customers, column: secret, treatment: hide }
              - { subject: interns, dataset: customers, column: salary, treatment: "null" }
              - { subject: interns, dataset: customers, column: email, treatment: mask, mask: hash }
            """);
        var entities = new ColumnShapedEntities(new Entities(), (c, entity) => ColumnTreatments.For(section, c.Caller.Id, c.Caller.Name, c.Caller.Roles), () => [7, 7, 7]);

        var (schema, batch) = await Read(entities, secretHidden: true);

        Assert.Equal(["id", "email", "salary"], schema.FieldsList.Select(f => f.Name));                // the hidden column is not in the answer
        Assert.IsType<DoubleType>(schema.GetFieldByName("salary").DataType);                           // the nulled one keeps its type
        Assert.True(batch.Column(2).IsNull(0) && batch.Column(2).IsNull(1));
        var email = (StringArray)batch.Column(1);
        Assert.Matches("^#[0-9a-f]{16}$", email.GetString(0));
        Assert.True(email.IsNull(1));
        Assert.Equal(1L, ((Int64Array)batch.Column(0)).GetValue(0));
    }

    [Fact]
    public async Task A_fixed_mask_is_one_constant_and_a_caller_with_no_rule_gets_the_answer_as_it_is()
    {
        var section = Section("          - { subject: interns, dataset: customers, column: email, treatment: mask }\n");
        var shaped = new ColumnShapedEntities(new Entities(), (c, _) => ColumnTreatments.For(section, c.Caller.Id, c.Caller.Name, c.Caller.Roles), () => []);
        var (_, masked) = await Read(shaped);
        Assert.Equal(["********", "********"], [((StringArray)masked.Column(1)).GetString(0), ((StringArray)masked.Column(1)).GetString(1)]);

        var other = new ColumnShapedEntities(new Entities(), (_, _) => ColumnTreatments.None, () => []);
        await using var answer = await other.ReadAsync(Context, "customers", null);
        Assert.Equal(4, answer.Schema.FieldsList.Count);
    }

    [Fact]
    public void The_cache_key_holds_the_access_fingerprint_in_every_scope()
    {
        var request = new ServingRequest.Sql("semantic:sales", "select 1");
        foreach (var scope in new[] { ServingCacheScope.Caller, ServingCacheScope.Rules, ServingCacheScope.Shared })
        {
            string Key(string? access) => ServingCacheKey.Build(Context, request, new ServingCachePlan(TimeSpan.FromMinutes(1), scope, "1", null, access));
            Assert.NotEqual(Key(null), Key("abc"));
            Assert.NotEqual(Key("abc"), Key("abd"));
            Assert.Equal(Key("abc"), Key("abc"));
            Assert.Equal(Key(null), Key("-".Length == 0 ? "" : null));
        }
    }
}
