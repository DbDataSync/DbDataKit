using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using Xunit;

namespace DbDataSync.Drivers.MsSql.Tests;

/// <summary>
/// Phase 132's guarantee, on <see cref="BatchInsertStagingProvider"/> → <see cref="Scd2Writer"/> against a real SQL Server target:
/// a key changed more than once between reads stages more than one row in a single pass, and each must become its own version, in
/// source order, without colliding on the target's primary key (before phase 132 they all computed the same pass-wide surrogate key).
/// <para>
/// The incremental pass is fed hand-built <see cref="ChangeRow"/>s carrying the two columns a CDC read produces
/// (<see cref="ChangeOrdering.OrderingColumn"/>, <see cref="ChangeOrdering.ChangedAtColumn"/>) with values the test chooses. These
/// tests used to drive a real SQL Server CDC capture to produce those rows and wait for it to map two commits to two distinct times;
/// that capture machinery (the Agent job, the log reader, <c>cdc.lsn_time_mapping</c>) stalled on CI for reasons unrelated to the
/// writer, through about eight attempted fixes (architecture/planning/todo/follow-up-phase-154-scd2-cdc-timestamp-mapping-race.md).
/// The writer never touches CDC: it consumes the ordering and the change time. So they are supplied directly, which also makes the
/// cases CDC cannot reliably produce (two changes with the identical change time) testable on purpose. That the reader emits correct
/// ordering and times from a real capture is <c>MsSqlCdcReaderTests</c>' job.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class Scd2OrderedChangesIntegrationTests(MsSqlTestDatabase db)
    : IClassFixture<MsSqlTestDatabase>, IAsyncLifetime
{
    private readonly BatchInsertStagingProvider _staging = new(MsSqlDialect.Instance, MsSqlCatalog.Instance);
    private readonly Scd2Writer _writer = new(MsSqlDialect.Instance, MsSqlCatalog.Instance);

    private SqlConnection _targetConnection = null!;
    private string _targetTable = null!;

    private static readonly List<ColumnMapping> Mappings =
    [
        new() { SourceColumn = "Id", TargetColumn = "Id" },
        new() { SourceColumn = "Name", TargetColumn = "Name" },
    ];

    private const string MappingName = "scd2-ordered-changes";

    /// <summary>What a CDC read produces for these mappings: the mapped columns, then the two ordering columns.</summary>
    private static readonly ChangeSchema OrderedSchema =
        new(["Id", "Name", ChangeOrdering.OrderingColumn, ChangeOrdering.ChangedAtColumn]);

    private static readonly ChangeSchema PlainSchema = new(["Id", "Name"]);

    /// <summary>The change times are real change times, so they come after the initial load, whose versions open at the pass's own clock
    /// time. A fixed instant far enough ahead of any run keeps them after it without depending on the clock.</summary>
    private static readonly DateTime T0 = new(2099, 1, 1, 12, 0, 0, DateTimeKind.Unspecified);
    private static DateTime At(int seconds) => T0.AddSeconds(seconds);

    /// <summary>A fixed-width hex ordering value, as CDC's (start_lsn, seqval) renders: sortable as text, unique per change.</summary>
    private static string Order(int sequence) => sequence.ToString("X40");

    private static ChangeRow Change(ChangeOperation op, int id, string name, int sequence, DateTime changedAt) =>
        new(op, OrderedSchema, [id, name, Order(sequence), changedAt]);

    public async Task InitializeAsync()
    {
        _targetConnection = db.OpenConnection();
        _targetTable = $"Scd2OrderedTgt_{Guid.NewGuid():N}";

        await ExecuteAsync(_targetConnection, $"""
            CREATE TABLE dbo.[{_targetTable}] (
                {HistorizedColumns.SurrogateKey} VARCHAR(200) NOT NULL PRIMARY KEY,
                Id INT NOT NULL,
                Name NVARCHAR(50) NOT NULL,
                {HistorizedColumns.ValidFrom} DATETIME2 NOT NULL,
                {HistorizedColumns.ValidTo} DATETIME2 NULL,
                {HistorizedColumns.IsCurrent} BIT NOT NULL);
            """);
    }

    public Task DisposeAsync()
    {
        _targetConnection.Dispose();
        return Task.CompletedTask;
    }

    private static async Task ExecuteAsync(SqlConnection connection, string sql)
    {
        await using var cmd = connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private TableRef Target() => new()
    {
        ConnectionName = "tgt", Database = db.DatabaseName, Schema = "dbo", Table = _targetTable,
    };

    private static List<CachedColumn> TargetColumns() =>
    [
        new(HistorizedColumns.SurrogateKey, "varchar(200)", false, true, false),
        new("Id", "int", false, false, false),
        new("Name", "nvarchar(50)", false, false, false),
        new(HistorizedColumns.ValidFrom, "datetime2", false, false, false),
        new(HistorizedColumns.ValidTo, "datetime2", true, false, false),
        new(HistorizedColumns.IsCurrent, "bit", false, false, false),
    ];

    private static Dictionary<string, string> WriterOptions() =>
        new() { [Scd2Writer.NaturalKeyOption] = "Id" };

    private async Task<(StagedChangeSet Staged, WriteResult Written)> ApplyAsync(IEnumerable<ChangeRow> rows)
    {
        var staged = await _staging.StageAsync(
            _targetConnection, Target(), ToAsyncEnumerable(rows), Mappings, MappingName, TargetColumns(),
            new Dictionary<string, string>(), CancellationToken.None);
        try
        {
            var written = await _writer.ApplyAsync(
                _targetConnection, Target(), staged, Mappings, MappingName, TargetColumns(),
                WriterOptions(), CancellationToken.None);
            return (staged, written);
        }
        finally
        {
            await _staging.CleanupAsync(_targetConnection, staged, CancellationToken.None);
        }
    }

    /// <summary>The initial load, as the Bulk Load pipeline writes it: a plain scan with no ordering columns, so one version per key.</summary>
    private async Task InitialLoadAsync(params (int Id, string Name)[] rows)
    {
        var (staged, _) = await ApplyAsync(rows.Select(r => new ChangeRow(ChangeOperation.Insert, PlainSchema, [r.Id, r.Name])));
        Assert.False(staged.HasChangeOrdering);
    }

    private static async IAsyncEnumerable<ChangeRow> ToAsyncEnumerable(IEnumerable<ChangeRow> rows)
    {
        await Task.CompletedTask;
        foreach (var row in rows)
            yield return row;
    }

    private sealed record Version(string VersionKey, string Name, DateTime ValidFrom, DateTime? ValidTo, bool IsCurrent);

    private async Task<List<Version>> ReadVersionsAsync(int id)
    {
        await using var cmd = _targetConnection.CreateCommand();
        cmd.CommandText = $"""
            SELECT {HistorizedColumns.SurrogateKey}, Name, {HistorizedColumns.ValidFrom}, {HistorizedColumns.ValidTo}, {HistorizedColumns.IsCurrent}
            FROM dbo.[{_targetTable}]
            WHERE Id = {id}
            ORDER BY {HistorizedColumns.ValidFrom}, {HistorizedColumns.SurrogateKey};
            """;
        var results = new List<Version>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new Version(
                reader.GetString(0), reader.GetString(1), reader.GetDateTime(2),
                reader.IsDBNull(3) ? null : reader.GetDateTime(3), reader.GetBoolean(4)));
        }
        return results;
    }

    /// <summary>
    /// The whole point of the phase, in one pass: a duplicate key with a real value transition every time (Id 1), a duplicate key
    /// whose middle change touches no mapped column (Id 3), and a singleton key in the very same pass (Id 2), proving the bulk path
    /// still carries every key the duplicate machinery does not have to touch, unaffected.
    /// </summary>
    [Fact]
    public async Task APassWithDuplicateAndSingletonKeys_AppliesEveryKeyCorrectly_WithNoPkViolation()
    {
        await InitialLoadAsync((1, "a"), (2, "p"), (3, "x"));

        var (staged, written) = await ApplyAsync(
        [
            // Two updates to Id 1 between reads, the scenario that crashed before phase 132.
            Change(ChangeOperation.Update, 1, "b", sequence: 1, At(10)),
            Change(ChangeOperation.Update, 1, "c", sequence: 2, At(20)),
            // A singleton key in the same pass.
            Change(ChangeOperation.Update, 2, "q", sequence: 3, At(30)),
            // Three changes to Id 3; the middle one touched only the unmapped Note column, so the mapped values repeat.
            Change(ChangeOperation.Update, 3, "y", sequence: 4, At(40)),
            Change(ChangeOperation.Update, 3, "y", sequence: 5, At(50)),
            Change(ChangeOperation.Update, 3, "z", sequence: 6, At(60)),
        ]);
        Assert.True(staged.HasChangeOrdering);

        // Id 1: two ordered versions produced by this pass, each transition closing and opening at its own change's time, not the
        // pass time and not each other's.
        var id1 = await ReadVersionsAsync(1);
        Assert.Equal(["a", "b", "c"], id1.Select(v => v.Name));
        Assert.Equal([false, false, true], id1.Select(v => v.IsCurrent));
        Assert.Equal(At(10), id1[0].ValidTo);
        Assert.Equal(At(10), id1[1].ValidFrom);
        Assert.Equal(At(20), id1[1].ValidTo);
        Assert.Equal(At(20), id1[2].ValidFrom);
        Assert.Null(id1[2].ValidTo);
        // The collision fix: OrderingColumn|naturalKey, not the pass-wide prefix.
        Assert.Equal($"{Order(1)}|1", id1[1].VersionKey);
        Assert.Equal($"{Order(2)}|1", id1[2].VersionKey);

        // Id 2: unaffected by the duplicate-key machinery; a singleton row in this pass still gets the pass-wide prefix format.
        var id2 = await ReadVersionsAsync(2);
        Assert.Equal(["p", "q"], id2.Select(v => v.Name));
        Assert.Equal([false, true], id2.Select(v => v.IsCurrent));
        Assert.Matches(@"^\d{17}-2$", id2[1].VersionKey);

        // Id 3: three staged changes, two versions opened. The middle one repeats the mapped values, so it is not a transition and
        // opens nothing.
        var id3 = await ReadVersionsAsync(3);
        Assert.Equal(["x", "y", "z"], id3.Select(v => v.Name));
        Assert.Equal([false, false, true], id3.Select(v => v.IsCurrent));
        Assert.Equal(At(40), id3[0].ValidTo);
        Assert.Equal(At(40), id3[1].ValidFrom);
        Assert.Equal(At(60), id3[1].ValidTo);
        Assert.Equal(At(60), id3[2].ValidFrom);
        Assert.Equal($"{Order(4)}|3", id3[1].VersionKey);
        Assert.Equal($"{Order(6)}|3", id3[2].VersionKey);

        // Versions opened this pass: Id 1's two, Id 2's one, Id 3's two; five, not the six staged rows.
        Assert.Equal(5, written.RowsWritten);
    }

    /// <summary>
    /// Phase 145's two edge cases, both real defects available in a window-function rewrite.
    /// <para>
    /// **A duplicate key whose first staged row is a delete** (Id 4, deleted and re-inserted between reads). The delete has no version
    /// of its own to open, but it is still what ends the version the target already had, so it stays in the ordering while being
    /// excluded from the insert. The row after it must open whatever its values are, since there is no open version left to match.
    /// </para>
    /// <para>
    /// **A duplicate key whose last staged row is a delete** (Id 5, updated and then deleted). The key ends the pass with no open
    /// version at all: the version this pass opened is opened already closed, by the delete that follows it.
    /// </para>
    /// </summary>
    [Fact]
    public async Task ADuplicateKeyStartingOrEndingInADelete_LeavesTheSameVersionsTheRowByRowLoopDid()
    {
        await InitialLoadAsync((4, "d0"), (5, "e0"));

        var (staged, _) = await ApplyAsync(
        [
            // Id 4: gone, then back.
            Change(ChangeOperation.Delete, 4, "d0", sequence: 1, At(10)),
            Change(ChangeOperation.Insert, 4, "d1", sequence: 2, At(20)),
            // Id 5: changed, then gone.
            Change(ChangeOperation.Update, 5, "e1", sequence: 3, At(30)),
            Change(ChangeOperation.Delete, 5, "e1", sequence: 4, At(40)),
        ]);
        Assert.True(staged.HasChangeOrdering);

        // Id 4: the delete ends 'd0' at its own time and opens nothing; the insert opens 'd1' as the current version. The gap
        // between the two is the period the row did not exist at the source, which is the point of an SCD2 target.
        var id4 = await ReadVersionsAsync(4);
        Assert.Equal(["d0", "d1"], id4.Select(v => v.Name));
        Assert.Equal([false, true], id4.Select(v => v.IsCurrent));
        Assert.Equal(At(10), id4[0].ValidTo);
        Assert.Equal(At(20), id4[1].ValidFrom);
        Assert.Null(id4[1].ValidTo);
        Assert.Equal($"{Order(2)}|4", id4[1].VersionKey);

        // Id 5: two versions, neither current. The update is one transition (ends 'e0' and begins 'e1' at the same moment) and the
        // version it opened is opened already closed, by the delete that follows it.
        var id5 = await ReadVersionsAsync(5);
        Assert.Equal(["e0", "e1"], id5.Select(v => v.Name));
        Assert.All(id5, v => Assert.False(v.IsCurrent));
        Assert.Equal(At(30), id5[0].ValidTo);
        Assert.Equal(At(30), id5[1].ValidFrom);
        Assert.Equal(At(40), id5[1].ValidTo);
    }

    /// <summary>
    /// Two changes to one key with the identical change time, told apart only by their ordering value. Legitimate (CDC maps commits to
    /// a coarse, interpolated time, so two commits can share one) and the case the CDC-driven version of these tests could not set up
    /// reliably. The versions must still come out in source order, with the middle one zero-length, and without a key collision.
    /// </summary>
    [Fact]
    public async Task TwoChangesToOneKeyAtTheSameInstant_AreOrderedByTheOrderingValueAlone()
    {
        await InitialLoadAsync((1, "a"));

        await ApplyAsync(
        [
            Change(ChangeOperation.Update, 1, "b", sequence: 1, At(10)),
            Change(ChangeOperation.Update, 1, "c", sequence: 2, At(10)),
        ]);

        var versions = await ReadVersionsAsync(1);
        Assert.Equal(["a", "b", "c"], versions.Select(v => v.Name));
        Assert.Equal([false, false, true], versions.Select(v => v.IsCurrent));
        Assert.Equal(At(10), versions[0].ValidTo);
        Assert.Equal(At(10), versions[1].ValidFrom);
        Assert.Equal(At(10), versions[1].ValidTo);
        Assert.Equal(At(10), versions[2].ValidFrom);
        Assert.Null(versions[2].ValidTo);
        Assert.Equal($"{Order(1)}|1", versions[1].VersionKey);
        Assert.Equal($"{Order(2)}|1", versions[2].VersionKey);
    }
}
