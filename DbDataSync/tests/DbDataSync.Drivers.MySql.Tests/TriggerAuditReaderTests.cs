using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using MySqlConnector;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MySql.Tests;

/// <summary>
/// The **same** reader as <c>DbDataSync.Drivers.Postgres.Tests.TriggerAuditReaderTests</c> and
/// <c>DbDataSync.Drivers.MsSql.Tests.TriggerAuditReaderTests</c>, against a third and fourth engine,
/// asserting the same things — and, uniquely to this driver, run against both MySQL and MariaDB from
/// one shared test body via <typeparamref name="TFixture"/>. That double run is the empirical half of
/// <c>change-tracking-mysql-and-mariadb-triggers.md</c>'s "one implementation covers both" claim: not
/// only is the DDL text identical (<see cref="MySqlTriggerAuditStatementTests"/>), running it against
/// real servers of both families produces the same reader behaviour from both.
/// </summary>
[Trait("Category", "Integration")]
public abstract class MySqlFamilyTriggerAuditReaderTestsBase<TFixture> : IClassFixture<TFixture>, IAsyncLifetime
    where TFixture : MySqlFamilyTestDatabase
{
    private readonly TFixture _db;
    private readonly TriggerAuditReader _reader = new(MySqlDialect.Instance, MySqlCatalog.Instance, MySqlTriggerInstall.Instance);
    private MySqlConnection _connection = null!;
    private string _tableName = null!;

    protected MySqlFamilyTriggerAuditReaderTestsBase(TFixture db) => _db = db;

    public async Task InitializeAsync()
    {
        _connection = _db.OpenConnection();
        _tableName = $"trg_probe_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE `{_tableName}` (
                `Id` INT NOT NULL PRIMARY KEY,
                `Name` VARCHAR(50) NOT NULL
            ) ENGINE=InnoDB;
            """);

        await ExecuteAsync(MySqlTriggerAudit.CreateShadowTable(_db.DatabaseName, _tableName, ["`Id` INT NOT NULL"]));
        await ExecuteAsync(MySqlTriggerAudit.CreateTriggers(_db.DatabaseName, _tableName, ["Id"]));
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(string sql)
    {
        // MySqlTriggerAudit.CreateTriggers returns several statements separated by blank lines; batching
        // more than one CREATE TRIGGER per round trip is unsupported by MySqlConnector's default text
        // protocol, so each is run separately.
        foreach (var statement in sql.Split(";\n", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var trimmed = statement.Trim();
            if (trimmed.Length == 0)
                continue;
            await using var cmd = _connection.CreateCommand();
            cmd.CommandText = trimmed;
            await cmd.ExecuteNonQueryAsync();
        }
    }

    private SourceTableRef Source() => new()
    {
        ConnectionName = "test", Database = _db.DatabaseName, Schema = _db.DatabaseName, Table = _tableName,
    };

    private const string MappingName = "trigger-audit-probe";

    private static List<CachedColumn> Columns() =>
    [
        new("Id", "int", false, true, false),
        new("Name", "varchar(50)", false, false, false),
    ];

    /// <summary>As every other engine's identically-named helper: a null watermark captures this
    /// reader's position rather than full-loading it — see the Postgres suite's own doc comment on the
    /// pattern this repeats.</summary>
    private async Task<ReadResult> ReadAsync(string? watermark, IReadOnlyDictionary<string, string>? options = null)
    {
        if (watermark is null)
        {
            var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
            var captured = await capturing.CapturePositionAsync(
                _connection, Source(), options ?? new Dictionary<string, string>(), CancellationToken.None);
            return new ReadResult(EmptyRows(), captured.Position, new ReadDiagnostics());
        }

        return await _reader.ReadChangesAsync(
            _connection, Source(), watermark, ReadIntent.Changes,
            [], MappingName, Columns(), [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), options ?? new Dictionary<string, string>(),
            CancellationToken.None);
    }

    private static async IAsyncEnumerable<ChangeRow> EmptyRows()
    {
        await Task.CompletedTask;
        yield break;
    }

    private static async Task<List<ChangeRow>> CollectAsync(IAsyncEnumerable<ChangeRow> rows)
    {
        var list = new List<ChangeRow>();
        await foreach (var row in rows)
            list.Add(row);
        return list;
    }

    private async Task<long> ShadowRowCountAsync()
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM `{TriggerAuditStatement.ShadowTableName(_tableName)}`;";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AnInitialLoad_CapturesThePosition_AndDoesNotFullLoad()
    {
        await ExecuteAsync($"INSERT INTO `{_tableName}` (`Id`, `Name`) VALUES (1, 'Alice'), (2, 'Bob');");

        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var captured = await capturing.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(captured.Position));

        await ExecuteAsync($"INSERT INTO `{_tableName}` (`Id`, `Name`) VALUES (3, 'Carol');");

        var rows = await CollectAsync((await ReadAsync(captured.Position)).Rows);

        var inserted = Assert.Single(rows);
        Assert.Equal(ChangeOperation.Insert, inserted.Operation);
        Assert.Equal(3, (int)inserted["Id"]!);
        Assert.Equal("Carol", (string)inserted["Name"]!);
    }

    [Fact]
    public async Task EachOperation_ComesBackAsItself()
    {
        await ExecuteAsync($"INSERT INTO `{_tableName}` (`Id`, `Name`) VALUES (1, 'Alice'), (2, 'Bob');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"INSERT INTO `{_tableName}` (`Id`, `Name`) VALUES (3, 'Carol');");
        await ExecuteAsync($"UPDATE `{_tableName}` SET `Name` = 'Robert' WHERE `Id` = 2;");
        await ExecuteAsync($"DELETE FROM `{_tableName}` WHERE `Id` = 1;");

        var rows = await CollectAsync((await ReadAsync(start)).Rows);

        Assert.Equal(3, rows.Count);
        Assert.Equal(ChangeOperation.Insert, Assert.Single(rows, r => (int)r["Id"]! == 3).Operation);
        Assert.Equal(ChangeOperation.Update, Assert.Single(rows, r => (int)r["Id"]! == 2).Operation);
        Assert.Equal(ChangeOperation.Delete, Assert.Single(rows, r => (int)r["Id"]! == 1).Operation);
    }

    /// <summary>The phase 12 bug's shape, on this engine too — a delete's key comes from the shadow
    /// row, because the base row is gone and the LEFT JOIN would make it NULL.</summary>
    [Fact]
    public async Task ADeletedRowKeepsItsKey()
    {
        await ExecuteAsync($"INSERT INTO `{_tableName}` (`Id`, `Name`) VALUES (7, 'Gone');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"DELETE FROM `{_tableName}` WHERE `Id` = 7;");

        var row = Assert.Single(await CollectAsync((await ReadAsync(start)).Rows));

        Assert.Equal(ChangeOperation.Delete, row.Operation);
        Assert.Equal(7, row["Id"]);
    }

    [Fact]
    public async Task ManyUpdatesToOneKey_CollapseToOneRow()
    {
        await ExecuteAsync($"INSERT INTO `{_tableName}` (`Id`, `Name`) VALUES (1, 'a');");
        var start = (await ReadAsync(null)).NewWatermark;

        for (var i = 0; i < 50; i++)
            await ExecuteAsync($"UPDATE `{_tableName}` SET `Name` = 'v{i}' WHERE `Id` = 1;");

        var row = Assert.Single(await CollectAsync((await ReadAsync(start)).Rows));

        Assert.Equal("v49", row["Name"]);
        Assert.Equal(51, await ShadowRowCountAsync());
    }

    [Fact]
    public async Task WithTheOption_AcknowledgementPrunesWhatHasBeenApplied()
    {
        var options = new Dictionary<string, string> { [TriggerAuditReader.PruneOption] = "true" };

        await ExecuteAsync($"INSERT INTO `{_tableName}` (`Id`, `Name`) VALUES (1, 'Alice');");
        var start = (await ReadAsync(null, options)).NewWatermark;
        await _reader.AcknowledgeAsync(_connection, Source(), start, options, CancellationToken.None);

        Assert.Equal(0, await ShadowRowCountAsync());
    }

    /// <summary>A composite key, since the shadow table carries every key column — worth a test rather
    /// than an assumption, the same reasoning the Postgres and MSSQL suites already gave this case.
    /// Also the one test that exercises all three triggers' composite-key VALUES lists at once.</summary>
    [Fact]
    public async Task ACompositeKey_CollapsesAndIdentifiesADeleteByEveryPart()
    {
        var composite = $"trg_composite_{Guid.NewGuid():N}";
        await ExecuteAsync($"""
            CREATE TABLE `{composite}` (
                `Region` VARCHAR(10) NOT NULL,
                `Id` INT NOT NULL,
                `Name` VARCHAR(50) NOT NULL,
                PRIMARY KEY (`Region`, `Id`)
            ) ENGINE=InnoDB;
            """);
        await ExecuteAsync(MySqlTriggerAudit.CreateShadowTable(
            _db.DatabaseName, composite, ["`Region` VARCHAR(10) NOT NULL", "`Id` INT NOT NULL"]));
        await ExecuteAsync(MySqlTriggerAudit.CreateTriggers(_db.DatabaseName, composite, ["Region", "Id"]));

        var source = new SourceTableRef
        {
            ConnectionName = "test", Database = _db.DatabaseName, Schema = _db.DatabaseName, Table = composite,
        };
        List<CachedColumn> compositeColumns =
        [
            new("Region", "varchar(10)", false, true, false),
            new("Id", "int", false, true, false),
            new("Name", "varchar(50)", false, false, false),
        ];

        async Task<ReadResult> Read(string? watermark)
        {
            if (watermark is null)
            {
                var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
                var captured = await capturing.CapturePositionAsync(
                    _connection, source, new Dictionary<string, string>(), CancellationToken.None);
                return new ReadResult(EmptyRows(), captured.Position, new ReadDiagnostics());
            }

            return await _reader.ReadChangesAsync(
                _connection, source, watermark, ReadIntent.Changes,
                [], MappingName, compositeColumns, [],new Dictionary<string, IReadOnlyList<CachedColumn>>(), new Dictionary<string, string>(),
                CancellationToken.None);
        }

        await ExecuteAsync($"INSERT INTO `{composite}` VALUES ('north', 1, 'a'), ('south', 1, 'b');");
        var start = (await Read(null)).NewWatermark;

        await ExecuteAsync($"UPDATE `{composite}` SET `Name` = 'c' WHERE `Region` = 'north';");
        await ExecuteAsync($"UPDATE `{composite}` SET `Name` = 'd' WHERE `Region` = 'north';");
        await ExecuteAsync($"DELETE FROM `{composite}` WHERE `Region` = 'south';");

        var rows = await CollectAsync((await Read(start)).Rows);

        Assert.Equal(2, rows.Count);
        var updated = Assert.Single(rows, r => r.Operation == ChangeOperation.Update);
        Assert.Equal("north", updated["Region"]);
        Assert.Equal("d", updated["Name"]);

        var deleted = Assert.Single(rows, r => r.Operation == ChangeOperation.Delete);
        Assert.Equal("south", deleted["Region"]);
        Assert.Equal(1, deleted["Id"]);
    }

    // ---- CW02a: a primary key update is a delete of the old key and the new row -------------------

    private string Q => $"`{_tableName}`";

    private async Task<List<ChangeRow>> ChangesSinceAsync(string start) =>
        await CollectAsync((await ReadAsync(start)).Rows);

    /// <summary>The case of the row: <c>UPDATE orders SET id = 2 WHERE id = 1</c> used to leave the row
    /// with id 1 in the target for good.</summary>
    [Fact]
    public async Task AKeyUpdate_IsADeleteOfTheOldKeyAndTheNewRow()
    {
        await ExecuteAsync($"INSERT INTO {Q} (`Id`, `Name`) VALUES (1, 'Alice');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"UPDATE {Q} SET `Id` = 2 WHERE `Id` = 1;");

        var rows = await ChangesSinceAsync(start);

        Assert.Equal(2, rows.Count);
        Assert.Equal(ChangeOperation.Delete, Assert.Single(rows, r => (int)r["Id"]! == 1).Operation);
        var moved = Assert.Single(rows, r => (int)r["Id"]! == 2);
        Assert.NotEqual(ChangeOperation.Delete, moved.Operation);
        Assert.Equal("Alice", moved["Name"]);
    }

    /// <summary>An update that leaves the key alone is still one row, never a delete beside it.</summary>
    [Fact]
    public async Task AnUpdateThatKeepsTheKey_IsStillOneRow()
    {
        await ExecuteAsync($"INSERT INTO {Q} (`Id`, `Name`) VALUES (1, 'Alice');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"UPDATE {Q} SET `Name` = 'Alicia' WHERE `Id` = 1;");

        var row = Assert.Single(await ChangesSinceAsync(start));
        Assert.Equal(ChangeOperation.Update, row.Operation);
    }

    /// <summary>Two rows swap keys in one transaction (through a free key, as a primary key requires): both
    /// keys still exist afterwards, so neither may come out as a delete.</summary>
    [Fact]
    public async Task AKeySwapOfTwoRows_InOneTransaction_LeavesBothKeysAsRows()
    {
        await ExecuteAsync($"INSERT INTO {Q} (`Id`, `Name`) VALUES (1, 'one'), (2, 'two');");
        var start = (await ReadAsync(null)).NewWatermark;

        await using (var transaction = await _connection.BeginTransactionAsync())
        {
            foreach (var statement in new[]
            {
                $"UPDATE {Q} SET `Id` = 0 WHERE `Id` = 1",
                $"UPDATE {Q} SET `Id` = 1 WHERE `Id` = 2",
                $"UPDATE {Q} SET `Id` = 2 WHERE `Id` = 0",
            })
            {
                await using var cmd = _connection.CreateCommand();
                cmd.Transaction = transaction;
                cmd.CommandText = statement;
                await cmd.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }

        var rows = await ChangesSinceAsync(start);

        Assert.Equal(ChangeOperation.Delete, Assert.Single(rows, r => (int)r["Id"]! == 0).Operation);
        var one = Assert.Single(rows, r => (int)r["Id"]! == 1);
        var two = Assert.Single(rows, r => (int)r["Id"]! == 2);
        Assert.NotEqual(ChangeOperation.Delete, one.Operation);
        Assert.NotEqual(ChangeOperation.Delete, two.Operation);
        Assert.Equal("two", one["Name"]);
        Assert.Equal("one", two["Name"]);
    }

    [Fact]
    public async Task AKeyUpdateFollowedByAnUpdateOfTheNewRow_CarriesTheFinalValues()
    {
        await ExecuteAsync($"INSERT INTO {Q} (`Id`, `Name`) VALUES (1, 'Alice');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"UPDATE {Q} SET `Id` = 2 WHERE `Id` = 1;");
        await ExecuteAsync($"UPDATE {Q} SET `Name` = 'Final' WHERE `Id` = 2;");

        var rows = await ChangesSinceAsync(start);

        Assert.Equal(2, rows.Count);
        Assert.Equal(ChangeOperation.Delete, Assert.Single(rows, r => (int)r["Id"]! == 1).Operation);
        Assert.Equal("Final", Assert.Single(rows, r => (int)r["Id"]! == 2)["Name"]);
    }

    [Fact]
    public async Task ADeleteAndAnInsertOfTheSameKey_CollapseToTheLastOperation()
    {
        await ExecuteAsync($"INSERT INTO {Q} (`Id`, `Name`) VALUES (1, 'old');");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"DELETE FROM {Q} WHERE `Id` = 1;");
        await ExecuteAsync($"INSERT INTO {Q} (`Id`, `Name`) VALUES (1, 'new');");

        var row = Assert.Single(await ChangesSinceAsync(start));
        Assert.NotEqual(ChangeOperation.Delete, row.Operation);
        Assert.Equal("new", row["Name"]);
    }

    [Fact]
    public async Task ADeleteRecordWhileTheRowExists_IsReadAsTheRow()
    {
        await ExecuteAsync($"INSERT INTO {Q} (`Id`, `Name`) VALUES (5, 'here');");
        var start = (await ReadAsync(null)).NewWatermark;
        await ExecuteAsync($"INSERT INTO `{TriggerAuditStatement.ShadowTableName(_tableName)}` (`DS_Op`, `Id`) VALUES ('D', 5);");

        var row = Assert.Single(await ChangesSinceAsync(start));

        Assert.NotEqual(ChangeOperation.Delete, row.Operation);
        Assert.Equal("here", row["Name"]);
    }

    // ---- CW02a: a trigger of the old text is detected, named, and replaced -------------------------

    /// <summary>The update trigger as it was before CW02a: it records the new key only.</summary>
    private Task InstallOldUpdateTriggerAsync() => ExecuteAsync($"""
        DROP TRIGGER IF EXISTS `ds_trg_{_tableName}_au`;
        CREATE TRIGGER `ds_trg_{_tableName}_au` AFTER UPDATE ON `{_tableName}`
        FOR EACH ROW INSERT INTO `DS_Changes_{_tableName}` (`DS_Op`, `Id`) VALUES ('U', NEW.`Id`);
        """);

    [Fact]
    public async Task ATriggerOfTheOldText_IsOutdated_TheCurrentOneIsCurrent_AMissingOneIsAbsent()
    {
        Assert.Equal(TriggerAuditInstall.Current,
            await MySqlTriggerInstall.InspectAsync(_connection, _db.DatabaseName, _tableName, CancellationToken.None));

        await InstallOldUpdateTriggerAsync();
        Assert.Equal(TriggerAuditInstall.Outdated,
            await MySqlTriggerInstall.InspectAsync(_connection, _db.DatabaseName, _tableName, CancellationToken.None));

        Assert.Equal(TriggerAuditInstall.Absent,
            await MySqlTriggerInstall.InspectAsync(_connection, _db.DatabaseName, "no_such_table", CancellationToken.None));
    }

    [Fact]
    public async Task APassOverATriggerOfTheOldText_FailsNamingTheTableAndTheWayOut_UntilItIsReplaced()
    {
        await ExecuteAsync($"INSERT INTO {Q} (`Id`, `Name`) VALUES (1, 'Alice');");
        var start = (await ReadAsync(null)).NewWatermark;
        await InstallOldUpdateTriggerAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(start));
        Assert.Contains(_tableName, failure.Message);
        Assert.Contains("Enable source change capture", failure.Message);

        var request = new ProvisioningRequest(
            ProvisioningActions.EnableSourceChangeCapture,
            new TableRef { ConnectionName = "c", Database = _db.DatabaseName, Schema = _db.DatabaseName, Table = _tableName },
            GenericDriverKinds.TriggerAudit, new Dictionary<string, string>(), []);
        var plan = await MySqlProvisioner.PlanAsync(_connection, request, CancellationToken.None);
        var step = Assert.Single(plan.Steps);
        Assert.StartsWith("Replace the change triggers", step.Title);
        await ExecuteAsync(step.CommandText);

        Assert.Equal(ProvisioningState.Satisfied,
            (await MySqlProvisioner.PlanAsync(_connection, request, CancellationToken.None)).State);
        await ExecuteAsync($"UPDATE {Q} SET `Id` = 2 WHERE `Id` = 1;");
        Assert.Equal(2, (await ChangesSinceAsync(start)).Count);
    }
}

public sealed class MySqlTriggerAuditReaderTests(MySqlTestDatabase db)
    : MySqlFamilyTriggerAuditReaderTestsBase<MySqlTestDatabase>(db);

public sealed class MariaDbTriggerAuditReaderTests(MariaDbTestDatabase db)
    : MySqlFamilyTriggerAuditReaderTestsBase<MariaDbTestDatabase>(db);
