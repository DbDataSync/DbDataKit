using DbDataKit.Dialect;
using DbDataSync.Drivers.Abstractions;
using Xunit;

namespace DbDataSync.Drivers.Generic.Tests;

/// <summary>
/// The seam between Sync's rows and the shared bulk writers (plan 13, A14 and A15): what the writers are handed is the staging table's columns in the
/// staging table's order, with the operation marker last. No engine needed.
/// </summary>
public sealed class StagingBatchesTests
{
    private static async IAsyncEnumerable<ChangeRow> Rows(ChangeSchema schema, int count, bool withOrdering = false)
    {
        for (var i = 0; i < count; i++)
        {
            var values = withOrdering ? new object?[] { i, "n" + i, "ord" + i, new DateTime(2026, 1, 1) } : new object?[] { i, i % 2 == 0 ? null : "n" + i };
            yield return new ChangeRow(i % 3 == 0 ? ChangeOperation.Delete : i % 3 == 1 ? ChangeOperation.Insert : ChangeOperation.Update, schema, values);
        }
        await Task.CompletedTask;
    }

    private static readonly IReadOnlyDictionary<string, string> Native = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Id"] = "int", ["Name"] = "nvarchar(50)", ["Odd"] = "geography",
    };

    [Fact]
    public void TheSchema_IsTheMappedColumnsThenTheOperationMarker_AndATypeTheDialectCannotMapIsCarriedAsItIs()
    {
        var schema = StagingBatches.Schema(EngineId.SqlServer, ["Id", "Name", "Odd"], Native, includeChangeOrdering: false, "__Operation");

        Assert.Equal(["Id", "Name", "Odd", "__Operation"], schema.Columns.Select(c => c.Name));
        Assert.Equal(CanonicalKind.Int32, schema[0].Type.Kind);
        Assert.Equal(CanonicalKind.String, schema[1].Type.Kind);
        Assert.Equal(CanonicalKind.Unmappable, schema[2].Type.Kind);
    }

    [Fact]
    public void TheSchema_CarriesBothOrderingColumnsBeforeTheMarker()
    {
        var schema = StagingBatches.Schema(EngineId.Postgres, ["Id"], new Dictionary<string, string> { ["Id"] = "integer" }, includeChangeOrdering: true, "__Operation");

        Assert.Equal(["Id", ChangeOrdering.OrderingColumn, ChangeOrdering.ChangedAtColumn, "__Operation"], schema.Columns.Select(c => c.Name));
    }

    [Fact]
    public async Task Batches_ReadCellsBySourceOrdinal_NullsAsDbNull_AndTheOperationAsItsMarker()
    {
        // the reader's layout is not the staging layout: the target columns are mapped from differently named, differently ordered source columns
        var source = new ChangeSchema(["src_name", "src_id"]);
        async IAsyncEnumerable<ChangeRow> Mixed()
        {
            yield return new ChangeRow(ChangeOperation.Insert, source, ["a", 1]);
            yield return new ChangeRow(ChangeOperation.Update, source, [null, 2]);
            yield return new ChangeRow(ChangeOperation.Delete, source, [DBNull.Value, 3]);
            await Task.CompletedTask;
        }
        var schema = StagingBatches.Schema(EngineId.SqlServer, ["Id", "Name"], Native, false, "__Operation");
        var counter = new StagingBatches.Counter();

        var read = new List<object?[]>();
        await foreach (var batch in StagingBatches.Batch(
                           Mixed(), schema, ["Id", "Name"], new Dictionary<string, string> { ["Id"] = "src_id", ["Name"] = "src_name" }, false, counter))
        {
            using var reader = batch.AsDataReader();
            while (reader.Read())
                read.Add([reader.GetValue(0), reader.GetValue(1), reader.GetValue(2), reader.IsDBNull(1), reader.IsDBNull(2)]);
        }

        Assert.Equal(3, counter.Rows);
        Assert.Equal(1, read[0][0]);
        Assert.Equal("a", read[0][1]);
        Assert.Equal("I", read[0][2]);
        Assert.Equal(true, read[1][3]);
        Assert.Equal(DBNull.Value, read[1][1]);
        Assert.Equal("U", read[1][2]);
        Assert.Equal(true, read[2][3]);
        Assert.Equal("D", read[2][2]);
        Assert.Equal(false, read[0][4]);   // the marker is never null
    }

    [Fact]
    public async Task Batches_SplitAtTheBatchSize_AndKeepEveryRowInOrder()
    {
        var source = new ChangeSchema(["Id", "Name"]);
        var schema = StagingBatches.Schema(EngineId.SqlServer, ["Id", "Name"], Native, false, "__Operation");
        var counter = new StagingBatches.Counter();
        var total = StagingBatches.BatchRows * 2 + 5;

        var sizes = new List<int>();
        var ids = new List<int>();
        await foreach (var batch in StagingBatches.Batch(
                           Rows(source, total), schema, ["Id", "Name"], new Dictionary<string, string> { ["Id"] = "Id", ["Name"] = "Name" }, false, counter))
        {
            sizes.Add(batch.Rows);
            using var reader = batch.AsDataReader();
            while (reader.Read()) ids.Add(reader.GetInt32(0));
        }

        Assert.Equal([StagingBatches.BatchRows, StagingBatches.BatchRows, 5], sizes);
        Assert.Equal(Enumerable.Range(0, total), ids);
        Assert.Equal(total, counter.Rows);
    }

    [Fact]
    public async Task AnEmptyChangeSet_YieldsNoBatches()
    {
        var schema = StagingBatches.Schema(EngineId.SqlServer, ["Id"], Native, false, "__Operation");
        var counter = new StagingBatches.Counter();

        var batches = await StagingBatches.Batch(
            Rows(new ChangeSchema(["Id", "Name"]), 0), schema, ["Id"], new Dictionary<string, string> { ["Id"] = "Id" }, false, counter).ToListAsync();

        Assert.Empty(batches);
        Assert.Equal(0, counter.Rows);
    }

    [Fact]
    public async Task AMappingNamingAColumnTheReaderDoesNotProduce_FailsNamingIt()
    {
        var schema = StagingBatches.Schema(EngineId.SqlServer, ["Id"], Native, false, "__Operation");
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await StagingBatches.Batch(
                Rows(new ChangeSchema(["Id", "Name"]), 1), schema, ["Id"], new Dictionary<string, string> { ["Id"] = "Missing" }, false, new StagingBatches.Counter()).ToListAsync());

        Assert.Contains("'Missing'", ex.Message);
    }

    [Fact]
    public async Task TheOrderingColumns_AreReadFromTheSelfMappedSourceColumns()
    {
        var source = new ChangeSchema(["Id", "Name", ChangeOrdering.OrderingColumn, ChangeOrdering.ChangedAtColumn]);
        var schema = StagingBatches.Schema(EngineId.SqlServer, ["Id", "Name"], Native, true, "__Operation");

        var batch = (await StagingBatches.Batch(
            Rows(source, 1, withOrdering: true), schema, ["Id", "Name"], new Dictionary<string, string> { ["Id"] = "Id", ["Name"] = "Name" }, true, new StagingBatches.Counter()).ToListAsync()).Single();
        using var reader = batch.AsDataReader();
        Assert.True(reader.Read());

        Assert.Equal("ord0", reader.GetValue(2));
        Assert.Equal(new DateTime(2026, 1, 1), reader.GetValue(3));
        Assert.Equal("D", reader.GetValue(4));
    }
}
