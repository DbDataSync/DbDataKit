using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// The direct-load pair — the <c>PassThrough</c> cache and <c>MsSqlBulkInsert</c> — against a real SQL Server:
/// rows go from the reader into the target with nothing staged, behind a delete or a truncate, with the
/// clear and the load in one transaction or not.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MsSqlBulkInsertWriterTests(MsSqlTestDatabase db) : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly BatchReloadReader _reader = new(MsSqlDialect.Instance, MsSqlValueBinding.Instance);
    private readonly PassThroughStagingProvider _cache = new();
    private readonly MsSqlBulkInsertWriter _writer = new();
    private SqlConnection _source = null!;
    private SqlConnection _target = null!;
    private string _sourceTable = null!;
    private string _targetTable = null!;

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Region", TargetColumn = "Region" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
    ];

    private static List<CachedColumn> Columns(bool identity = false) =>
    [
        new("Id", "int", false, true, identity),
        new("Region", "nvarchar(20)", false, false, false),
        new("Name", "nvarchar(50)", false, false, false),
    ];

    public async Task InitializeAsync()
    {
        _source = db.OpenConnection();
        _target = db.OpenConnection();
        var suffix = Guid.NewGuid().ToString("N");
        _sourceTable = $"DirectSrc_{suffix}";
        _targetTable = $"DirectTgt_{suffix}";
        await ExecuteAsync($"CREATE TABLE dbo.[{_sourceTable}] (Id INT NOT NULL PRIMARY KEY, Region NVARCHAR(20) NOT NULL, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync($"CREATE TABLE dbo.[{_targetTable}] (Id INT NOT NULL PRIMARY KEY, Region NVARCHAR(20) NOT NULL, Name NVARCHAR(50) NOT NULL);");
        await ExecuteAsync(
            $"INSERT dbo.[{_sourceTable}] VALUES (1,'EU','a'),(2,'EU','b'),(3,'US','c');");
    }

    public Task DisposeAsync()
    {
        _source.Dispose();
        _target.Dispose();
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _target.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<List<int>> TargetIdsAsync()
    {
        await using var cmd = _target.CreateCommand();
        cmd.CommandText = $"SELECT Id FROM dbo.[{_targetTable}] ORDER BY Id;";
        var ids = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync()) ids.Add(r.GetInt32(0));
        return ids;
    }

    private async Task<(long Written, long Read, StagedChangeSet Staged)> LoadAsync(
        Dictionary<string, string>? options = null, BatchReloadSegment? segment = null)
    {
        options ??= [];
        if (segment is not null)
            options[SegmentSerializer.SegmentOptionKey] = SegmentSerializer.Serialize(segment);

        var read = await _reader.ReadChangesAsync(
            _source, new SourceTableRef { ConnectionName = "s", Database = db.DatabaseName, Schema = "dbo", Table = _sourceTable },
            null, ReadIntent.InitialLoad, Mappings, "direct",
            [new("Id", "int", false, true, false), new("Region", "nvarchar(20)", false, false, false), new("Name", "nvarchar(50)", false, false, false)],
            [], new Dictionary<string, IReadOnlyList<CachedColumn>>(), options, CancellationToken.None);

        var target = new TableRef { ConnectionName = "t", Database = db.DatabaseName, Schema = "dbo", Table = _targetTable };
        var staged = await _cache.StageAsync(_target, target, read.Rows, Mappings, "direct", [], options, CancellationToken.None);
        var written = await _writer.ApplyAsync(_target, target, staged, Mappings, "direct", Columns(), options, CancellationToken.None);
        return (written.RowsWritten, staged.RowCount, staged);
    }

    [Fact]
    public async Task Delete_ReplacesTheTarget_WithoutStagingAnything()
    {
        await ExecuteAsync($"INSERT dbo.[{_targetTable}] VALUES (99,'XX','stale');");

        var (written, read, staged) = await LoadAsync();

        Assert.Equal(3, written);
        Assert.Equal(3, read);
        Assert.NotNull(staged.Stream);
        Assert.Equal([1, 2, 3], await TargetIdsAsync());
    }

    [Fact]
    public async Task Truncate_EmptiesTheTarget_ThenLoads()
    {
        await ExecuteAsync($"INSERT dbo.[{_targetTable}] VALUES (99,'XX','stale');");

        await LoadAsync(new() { [MsSqlBulkInsertWriter.ClearOption] = "Truncate" });

        Assert.Equal([1, 2, 3], await TargetIdsAsync());
    }

    [Fact]
    public async Task Delete_OfASegment_LeavesTheOtherSegmentsRows()
    {
        await ExecuteAsync($"INSERT dbo.[{_targetTable}] VALUES (10,'EU','old-eu'),(20,'US','old-us');");

        await LoadAsync(segment: new ListSegment("Region", ["EU"]));

        // The EU row that was there is replaced by the source's EU rows; US is untouched.
        Assert.Equal([1, 2, 20], await TargetIdsAsync());
    }

    [Fact]
    public async Task Truncate_OfAWholeTableLoad_WorksWhenTheWorkItemCarriesAFullSegment()
    {
        // An unsegmented Bulk Load is dispatched with an explicit "full" segment, not with no segment.
        await ExecuteAsync($"INSERT dbo.[{_targetTable}] VALUES (99,'XX','stale');");

        await LoadAsync(new() { [MsSqlBulkInsertWriter.ClearOption] = "Truncate" }, new FullSegment());

        Assert.Equal([1, 2, 3], await TargetIdsAsync());
    }

    [Fact]
    public async Task Truncate_OfASegmentedLoad_IsRefused_BeforeAnythingIsDeleted()
    {
        await ExecuteAsync($"INSERT dbo.[{_targetTable}] VALUES (20,'US','keep');");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(
            new() { [MsSqlBulkInsertWriter.ClearOption] = "Truncate" }, new ListSegment("Region", ["EU"])));

        Assert.Contains("segments", ex.Message);
        Assert.Contains("Delete", ex.Message);
        Assert.Equal([20], await TargetIdsAsync());
    }

    [Fact]
    public async Task Truncate_OfATableAForeignKeyReferences_NamesTheSetting()
    {
        await ExecuteAsync($"CREATE TABLE dbo.[{_targetTable}_child] (Id INT NOT NULL PRIMARY KEY, ParentId INT NOT NULL REFERENCES dbo.[{_targetTable}](Id));");
        await ExecuteAsync($"INSERT dbo.[{_targetTable}] VALUES (99,'XX','kept');");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(
            new() { [MsSqlBulkInsertWriter.ClearOption] = "Truncate" }));

        Assert.Contains("foreign key", ex.Message);
        Assert.Contains("clear", ex.Message);
        Assert.Equal([99], await TargetIdsAsync());
    }

    [Fact]
    public async Task WithATransaction_AFailedLoad_LeavesThePreviousRows()
    {
        await ExecuteAsync($"INSERT dbo.[{_targetTable}] VALUES (99,'XX','stale');");
        // A source row too long for the target column fails the bulk insert after the clear has run.
        await ExecuteAsync($"ALTER TABLE dbo.[{_sourceTable}] ALTER COLUMN Name NVARCHAR(200) NOT NULL;");
        await ExecuteAsync($"UPDATE dbo.[{_sourceTable}] SET Name = REPLICATE('x', 120) WHERE Id = 3;");

        await Assert.ThrowsAnyAsync<Exception>(() => LoadAsync(new() { [MsSqlBulkInsertWriter.ClearOption] = "Truncate" }));

        Assert.Equal([99], await TargetIdsAsync());
    }

    [Fact]
    public async Task WithoutATransaction_AFailedLoad_LeavesTheTargetCleared()
    {
        await ExecuteAsync($"INSERT dbo.[{_targetTable}] VALUES (99,'XX','stale');");
        await ExecuteAsync($"ALTER TABLE dbo.[{_sourceTable}] ALTER COLUMN Name NVARCHAR(200) NOT NULL;");
        await ExecuteAsync($"UPDATE dbo.[{_sourceTable}] SET Name = REPLICATE('x', 120) WHERE Id = 3;");

        await Assert.ThrowsAnyAsync<Exception>(() => LoadAsync(new()
        {
            [MsSqlBulkInsertWriter.ClearOption] = "Truncate",
            [MsSqlBulkInsertWriter.TransactionOption] = "false",
        }));

        Assert.DoesNotContain(99, await TargetIdsAsync());
    }

    [Fact]
    public async Task ARegularStagedSet_IsRefused_WithTheCacheToUse()
    {
        var target = new TableRef { ConnectionName = "t", Database = db.DatabaseName, Schema = "dbo", Table = _targetTable };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _writer.ApplyAsync(
            _target, target, new StagedChangeSet("#Staging_x", 3), Mappings, "direct", Columns(), new Dictionary<string, string>(),
            CancellationToken.None));

        Assert.Contains("PassThrough", ex.Message);
    }

    [Fact]
    public async Task ANonInsertRow_FailsThePass()
    {
        var target = new TableRef { ConnectionName = "t", Database = db.DatabaseName, Schema = "dbo", Table = _targetTable };
        var schema = new ChangeSchema(["Id", "Region", "Name"]);
        async IAsyncEnumerable<ChangeRow> Rows()
        {
            yield return new ChangeRow(ChangeOperation.Update, schema, [1, "EU", "a"]);
            await Task.CompletedTask;
        }

        var staged = await _cache.StageAsync(_target, target, Rows(), Mappings, "direct", [], new Dictionary<string, string>(), CancellationToken.None);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _writer.ApplyAsync(
            _target, target, staged, Mappings, "direct", Columns(), new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains("only inserts", ex.Message);
    }

    [Fact]
    public async Task AnUnrecognisedClearValue_IsAnErrorNamingTheSetting()
    {
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(new() { [MsSqlBulkInsertWriter.ClearOption] = "Drop" }));
        Assert.Contains("clear", ex.Message);
    }

    [Fact]
    public void TheDriver_OffersBothHalvesOfThePair()
    {
        var driver = new MsSqlDriver();
        Assert.Contains(driver.StagingProviders, p => p.Kind == GenericDriverKinds.PassThrough);
        Assert.Contains(driver.Writers, w => w is IPassThroughWriter && w.Kind == MsSqlDriverKinds.BulkInsert);
    }
}
