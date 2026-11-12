using System.Data.Common;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using DbDataSync.Drivers.MsSql;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataSync.Drivers.Postgres.Tests;

/// <summary>
/// Plan 28c E17: a unit's data and its state row on the target commit together or not at all, on SQL Server and PostgreSQL, through the real staging providers and the real
/// writers. The "kill" is the server ending the unit's session between the state row's write and the commit (the process of the unit going away at the worst moment); what
/// the target holds afterwards is read back on another connection.
/// </summary>
[Trait("Category", "Integration")]
public abstract class TargetUnitStateTestsBase : IAsyncLifetime
{
    private DbConnection _connection = null!;

    protected abstract DbConnection OpenConnection();
    protected abstract IStagingProvider Staging { get; }
    protected abstract IReadOnlyList<IChangeWriter> Writers { get; }
    protected abstract UnitStateEngine Engine { get; }
    protected abstract string IntType { get; }
    protected abstract string TextType { get; }
    protected abstract string Qualified(string table);
    protected abstract TableRef TargetRef(string table);
    protected abstract Task KillSessionOfAsync(DbConnection victim);

    public async Task InitializeAsync() => _connection = await Task.Run(OpenConnection);

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private async Task<object?> ScalarAsync(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var command = _connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<string> NewTableAsync()
    {
        var table = $"us_{Guid.NewGuid():N}";
        await ExecuteAsync($"CREATE TABLE {Qualified(table)} (id {IntType} NOT NULL PRIMARY KEY, name {TextType} NULL)");
        return table;
    }

    private static readonly CachedColumn[] Columns = [new("id", "int", false, true, false), new("name", "varchar(100)", true, false, false)];

    private CachedColumn[] ColumnsForEngine() => [new("id", IntType, false, true, false), new("name", TextType, true, false, false)];

    private static async IAsyncEnumerable<ChangeRow> Rows(ChangeSchema schema, int count)
    {
        for (var i = 1; i <= count; i++) yield return new ChangeRow(ChangeOperation.Insert, schema, [i, $"row {i}"]);
        await Task.CompletedTask;
    }

    private async Task<(StagedChangeSet Staged, TableRef Target, List<ColumnMapping> Mappings, CachedColumn[] Columns)> StageAsync(DbConnection connection, string table, int rows)
    {
        var schema = new ChangeSchema(["id", "name"]);
        var mappings = new List<ColumnMapping> { new() { SourceColumn = "id", TargetColumn = "id" }, new() { SourceColumn = "name", TargetColumn = "name" } };
        var columns = ColumnsForEngine();
        var target = TargetRef(table);
        var staged = await Staging.StageAsync(connection, target, Rows(schema, rows), mappings, "us", columns.ToList(), new Dictionary<string, string>(), CancellationToken.None);
        return (staged, target, mappings, columns);
    }

    private static UnitDraft Draft(string replication, long rows, string after, string? before = null) =>
        new(Guid.NewGuid(), replication, "m", "src/key", before, after, DateTimeOffset.UtcNow, rows, Guid.NewGuid());

    public static IEnumerable<object[]> WriterKinds() =>
    [
        [GenericDriverKinds.ChangeApply],
        [GenericDriverKinds.DeleteInsert],
    ];

    [Theory]
    [MemberData(nameof(WriterKinds))]
    public async Task A_unit_and_its_state_row_commit_together(string writerKind)
    {
        var state = new TargetUnitState(Engine);
        await state.EnsureAsync(_connection, CancellationToken.None);
        await state.EnsureAsync(_connection, CancellationToken.None);   // twice: nothing to do the second time
        var table = await NewTableAsync();
        var replication = $"rep-{Guid.NewGuid():N}";
        var writer = Writers.Single(w => w.Kind == writerKind);
        Assert.True(writer.SupportsUnitState);

        var (staged, target, mappings, columns) = await StageAsync(_connection, table, 3);
        var draft = Draft(replication, 3, "pos-1");
        UnitStateScope.Scope scope;
        using (scope = UnitStateScope.Begin(state.CommitOf(draft)))
            await writer.ApplyAsync(_connection, target, staged, mappings, "us", columns, new Dictionary<string, string>(), CancellationToken.None);
        await Staging.CleanupAsync(_connection, staged, CancellationToken.None);

        Assert.Equal(1, scope.Fired);
        Assert.Equal(3, Convert.ToInt32(await ScalarAsync(_connection, $"SELECT COUNT(*) FROM {Qualified(table)}")));
        var latest = await state.LatestAsync(_connection, replication, "m", "src/key", CancellationToken.None);
        Assert.NotNull(latest);
        Assert.Equal("pos-1", latest!.WatermarkAfter);
        Assert.Equal(3, latest.RowsStaged);
        Assert.Equal(draft.UnitId, latest.UnitId);
        Assert.Null(await state.LatestAsync(_connection, replication, "other", "src/key", CancellationToken.None));
    }

    [Theory]
    [MemberData(nameof(WriterKinds))]
    public async Task A_unit_killed_between_the_state_write_and_the_commit_leaves_neither(string writerKind)
    {
        var state = new TargetUnitState(Engine);
        await state.EnsureAsync(_connection, CancellationToken.None);
        var table = await NewTableAsync();
        var replication = $"rep-{Guid.NewGuid():N}";
        var writer = Writers.Single(w => w.Kind == writerKind);

        await using var victim = OpenConnection();
        var (staged, target, mappings, columns) = await StageAsync(victim, table, 5);
        var kill = new KillAfterStateWrite(state.CommitOf(Draft(replication, 5, "pos-1")), KillSessionOfAsync);
        using (UnitStateScope.Begin(kill))
        {
            // the data is written, the state row is written, and the session ends before the commit: the writer fails
            await Assert.ThrowsAnyAsync<Exception>(() => writer.ApplyAsync(victim, target, staged, mappings, "us", columns, new Dictionary<string, string>(), CancellationToken.None));
        }

        Assert.True(kill.Killed, "the hook ran: the state row was written in the open transaction before the session was ended");
        Assert.Equal(0, Convert.ToInt32(await ScalarAsync(_connection, $"SELECT COUNT(*) FROM {Qualified(table)}")));
        Assert.Null(await state.LatestAsync(_connection, replication, "m", "src/key", CancellationToken.None));
        Assert.Empty(await state.ReadAfterAsync(_connection, replication, 0, 10, CancellationToken.None));
    }

    [Fact]
    public async Task A_failed_write_leaves_no_state_row()
    {
        var state = new TargetUnitState(Engine);
        await state.EnsureAsync(_connection, CancellationToken.None);
        var table = await NewTableAsync();
        await ExecuteAsync($"INSERT INTO {Qualified(table)} (id, name) VALUES (1, 'taken')");
        var replication = $"rep-{Guid.NewGuid():N}";
        var writer = Writers.Single(w => w.Kind == GenericDriverKinds.DeleteInsert);
        // DeleteInsert with a scope that keeps the old row would not fail; an insert of a duplicate key through a writer that does not delete does: stage the same key twice
        var schema = new ChangeSchema(["id", "name"]);
        var mappings = new List<ColumnMapping> { new() { SourceColumn = "id", TargetColumn = "id" }, new() { SourceColumn = "name", TargetColumn = "name" } };
        var columns = ColumnsForEngine();
        var target = TargetRef(table);
        async IAsyncEnumerable<ChangeRow> Duplicates()
        {
            yield return new ChangeRow(ChangeOperation.Insert, schema, [7, "a"]);
            yield return new ChangeRow(ChangeOperation.Insert, schema, [7, "b"]);
            await Task.CompletedTask;
        }

        var staged = await Staging.StageAsync(_connection, target, Duplicates(), mappings, "us", columns.ToList(), new Dictionary<string, string>(), CancellationToken.None);
        using (UnitStateScope.Begin(state.CommitOf(Draft(replication, 2, "pos-2"))))
            await Assert.ThrowsAnyAsync<DbException>(() => writer.ApplyAsync(_connection, target, staged, mappings, "us", columns, new Dictionary<string, string>(), CancellationToken.None));

        Assert.Equal(1, Convert.ToInt32(await ScalarAsync(_connection, $"SELECT COUNT(*) FROM {Qualified(table)}")));   // only the row that was there
        Assert.Null(await state.LatestAsync(_connection, replication, "m", "src/key", CancellationToken.None));
    }

    [Fact]
    public async Task Units_are_numbered_in_order_and_the_stream_reads_them_after_a_number()
    {
        var state = new TargetUnitState(Engine);
        await state.EnsureAsync(_connection, CancellationToken.None);
        var replication = $"rep-{Guid.NewGuid():N}";
        for (var i = 1; i <= 3; i++) await state.WriteAsync(_connection, null, Draft(replication, i, $"pos-{i}", i == 1 ? null : $"pos-{i - 1}"), CancellationToken.None);

        var all = await state.ReadAfterAsync(_connection, replication, 0, 10, CancellationToken.None);
        Assert.Equal(["pos-1", "pos-2", "pos-3"], all.Select(u => u.WatermarkAfter));
        Assert.True(all[0].Sequence < all[1].Sequence && all[1].Sequence < all[2].Sequence);
        var after = await state.ReadAfterAsync(_connection, replication, all[0].Sequence, 10, CancellationToken.None);
        Assert.Equal(["pos-2", "pos-3"], after.Select(u => u.WatermarkAfter));
        Assert.Equal("pos-3", (await state.LatestAsync(_connection, replication, "m", "src/key", CancellationToken.None))!.WatermarkAfter);
        Assert.True(await state.HoldsAnyAsync(_connection, replication, CancellationToken.None));
        Assert.False(await state.HoldsAnyAsync(_connection, "nobody", CancellationToken.None));
        Assert.Equal("pos-2", all[2].WatermarkBefore);
    }

    [Fact]
    public async Task A_writer_with_no_scope_writes_no_state_row()
    {
        var state = new TargetUnitState(Engine);
        await state.EnsureAsync(_connection, CancellationToken.None);
        var table = await NewTableAsync();
        var writer = Writers.Single(w => w.Kind == GenericDriverKinds.ChangeApply);
        var (staged, target, mappings, columns) = await StageAsync(_connection, table, 2);
        await writer.ApplyAsync(_connection, target, staged, mappings, "us", columns, new Dictionary<string, string>(), CancellationToken.None);
        await Staging.CleanupAsync(_connection, staged, CancellationToken.None);
        Assert.False(UnitStateScope.Active);
        Assert.Equal(2, Convert.ToInt32(await ScalarAsync(_connection, $"SELECT COUNT(*) FROM {Qualified(table)}")));
    }

    private sealed class KillAfterStateWrite(IUnitStateCommit inner, Func<DbConnection, Task> kill) : IUnitStateCommit
    {
        public bool Killed { get; private set; }

        public async Task BeforeCommitAsync(DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
        {
            await inner.BeforeCommitAsync(connection, transaction, cancellationToken);
            await kill(connection);
            Killed = true;
        }
    }
}

public sealed class SqlServerTargetUnitStateTests(MsSqlScratchDatabase db) : TargetUnitStateTestsBase, IClassFixture<MsSqlScratchDatabase>
{
    private static readonly MsSqlDriver Driver = new();

    protected override DbConnection OpenConnection() => db.OpenConnection();
    protected override IStagingProvider Staging { get; } = Driver.StagingProviders.Single(p => p.Kind == GenericDriverKinds.StagingTable);
    protected override IReadOnlyList<IChangeWriter> Writers { get; } = Driver.Writers;
    protected override UnitStateEngine Engine => UnitStateEngine.SqlServer;
    protected override string IntType => "int";
    protected override string TextType => "nvarchar(100)";
    protected override string Qualified(string table) => $"dbo.[{table}]";
    protected override TableRef TargetRef(string table) => new() { ConnectionName = "t", Database = db.DatabaseName, Schema = "dbo", Table = table };

    protected override async Task KillSessionOfAsync(DbConnection victim)
    {
        var spid = ((SqlConnection)victim).ServerProcessId;
        await using var other = new SqlConnection(MsSqlScratchDatabase.ServerConnectionString);
        await other.OpenAsync();
        await using var command = other.CreateCommand();
        command.CommandText = $"KILL {spid}";
        await command.ExecuteNonQueryAsync();
        await Task.Delay(300);
    }
}

public sealed class PostgresTargetUnitStateTests(PostgresTestDatabase db) : TargetUnitStateTestsBase, IClassFixture<PostgresTestDatabase>
{
    private static readonly PostgresDriver Driver = new();

    protected override DbConnection OpenConnection() => db.OpenConnection();
    protected override IStagingProvider Staging { get; } = Driver.StagingProviders.Single(p => p.Kind == GenericDriverKinds.StagingTable);
    protected override IReadOnlyList<IChangeWriter> Writers { get; } = Driver.Writers;
    protected override UnitStateEngine Engine => UnitStateEngine.Postgres;
    protected override string IntType => "integer";
    protected override string TextType => "varchar(100)";
    protected override string Qualified(string table) => $"public.\"{table}\"";
    protected override TableRef TargetRef(string table) => new() { ConnectionName = "t", Database = db.DatabaseName, Schema = "public", Table = table };

    protected override async Task KillSessionOfAsync(DbConnection victim)
    {
        var pid = ((NpgsqlConnection)victim).ProcessID;
        await using var other = new NpgsqlConnection(PostgresTestDatabase.ServerConnectionString);
        await other.OpenAsync();
        await using var command = other.CreateCommand();
        command.CommandText = $"SELECT pg_terminate_backend({pid})";
        await command.ExecuteScalarAsync();
        await Task.Delay(300);
    }
}
