using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Oracle.ManagedDataAccess.Client;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Oracle.Tests;

/// <summary>
/// The **same** reader as the Postgres, SQL Server and MySQL/MariaDB suites, against a fifth engine,
/// asserting the same things — the whole argument for this mechanism, proven a third time. Only the
/// setup differs: Oracle takes one trigger with an inline PL/SQL body branching on
/// <c>INSERTING</c>/<c>UPDATING</c>/<c>DELETING</c>, which <see cref="OracleTriggerAuditStatementTests"/>
/// checks the shape of and this file proves actually works against a live server.
/// </summary>
[Trait("Category", "Integration")]
public sealed class TriggerAuditReaderTests(OracleTestDatabase db) : IClassFixture<OracleTestDatabase>, IAsyncLifetime
{
    private readonly TriggerAuditReader _reader = new(OracleDialect.Instance, OracleCatalog.Instance, OracleTriggerInstall.Instance);
    private readonly List<string> _tablesToDrop = [];
    private OracleConnection _connection = null!;
    private string _tableName = null!;

    public async Task InitializeAsync()
    {
        _connection = db.OpenConnection();
        _tableName = $"TRG_PROBE_{Guid.NewGuid():N}";

        await ExecuteAsync($"""
            CREATE TABLE "{_tableName}" (
                "Id" NUMBER NOT NULL PRIMARY KEY,
                "Name" VARCHAR2(50) NOT NULL
            )
            """);
        _tablesToDrop.Add(_tableName);

        await ExecuteAsync(OracleTriggerAudit.CreateShadowTable(db.SchemaName, _tableName, ["\"Id\" NUMBER NOT NULL"]));
        _tablesToDrop.Add(TriggerAuditStatement.ShadowTableName(_tableName));
        await ExecuteAsync(OracleTriggerAudit.CreateTrigger(db.SchemaName, _tableName, ["Id"]));
    }

    public async Task DisposeAsync()
    {
        // Best-effort: dropping the base table drops its trigger with it, so only the two tables need
        // an explicit DROP. Order matters — the shadow table has no dependency on the base one, but
        // dropping it second keeps the intent (base table's own lifecycle owns the trigger) obvious.
        foreach (var table in _tablesToDrop)
        {
            try
            {
                await ExecuteAsync($"DROP TABLE \"{table}\" PURGE");
            }
            catch
            {
                // Best-effort cleanup; a failure here should not fail the test that already ran.
            }
        }
        _connection.Dispose();
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    private SourceTableRef Source() => new()
    {
        ConnectionName = "test", Database = "FREEPDB1", Schema = db.SchemaName, Table = _tableName,
    };

    private const string MappingName = "trigger-audit-probe";

    private static List<CachedColumn> Columns() =>
    [
        new("Id", "NUMBER", false, true, false),
        new("Name", "VARCHAR2(50)", false, false, false),
    ];

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
        cmd.CommandText = $"SELECT COUNT(*) FROM \"{TriggerAuditStatement.ShadowTableName(_tableName)}\"";
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    [Fact]
    public async Task AnInitialLoad_CapturesThePosition_AndDoesNotFullLoad()
    {
        await ExecuteAsync($"INSERT INTO \"{_tableName}\" (\"Id\", \"Name\") VALUES (1, 'Alice')");
        await ExecuteAsync($"INSERT INTO \"{_tableName}\" (\"Id\", \"Name\") VALUES (2, 'Bob')");

        var capturing = Assert.IsAssignableFrom<IPositionCapturing>(_reader);
        var captured = await capturing.CapturePositionAsync(
            _connection, Source(), new Dictionary<string, string>(), CancellationToken.None);
        Assert.False(string.IsNullOrEmpty(captured.Position));

        await ExecuteAsync($"INSERT INTO \"{_tableName}\" (\"Id\", \"Name\") VALUES (3, 'Carol')");

        var rows = await CollectAsync((await ReadAsync(captured.Position)).Rows);

        var inserted = Assert.Single(rows);
        Assert.Equal(ChangeOperation.Insert, inserted.Operation);
        Assert.Equal(3m, Convert.ToDecimal(inserted["Id"]));
        Assert.Equal("Carol", (string)inserted["Name"]!);
    }

    [Fact]
    public async Task EachOperation_ComesBackAsItself()
    {
        await ExecuteAsync($"INSERT INTO \"{_tableName}\" (\"Id\", \"Name\") VALUES (1, 'Alice')");
        await ExecuteAsync($"INSERT INTO \"{_tableName}\" (\"Id\", \"Name\") VALUES (2, 'Bob')");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"INSERT INTO \"{_tableName}\" (\"Id\", \"Name\") VALUES (3, 'Carol')");
        await ExecuteAsync($"UPDATE \"{_tableName}\" SET \"Name\" = 'Robert' WHERE \"Id\" = 2");
        await ExecuteAsync($"DELETE FROM \"{_tableName}\" WHERE \"Id\" = 1");

        var rows = await CollectAsync((await ReadAsync(start)).Rows);

        Assert.Equal(3, rows.Count);
        Assert.Equal(ChangeOperation.Insert, Assert.Single(rows, r => Convert.ToDecimal(r["Id"]) == 3m).Operation);
        Assert.Equal(ChangeOperation.Update, Assert.Single(rows, r => Convert.ToDecimal(r["Id"]) == 2m).Operation);
        Assert.Equal(ChangeOperation.Delete, Assert.Single(rows, r => Convert.ToDecimal(r["Id"]) == 1m).Operation);
    }

    [Fact]
    public async Task ADeletedRowKeepsItsKey()
    {
        await ExecuteAsync($"INSERT INTO \"{_tableName}\" (\"Id\", \"Name\") VALUES (7, 'Gone')");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"DELETE FROM \"{_tableName}\" WHERE \"Id\" = 7");

        var row = Assert.Single(await CollectAsync((await ReadAsync(start)).Rows));

        Assert.Equal(ChangeOperation.Delete, row.Operation);
        Assert.Equal(7m, Convert.ToDecimal(row["Id"]));
    }

    [Fact]
    public async Task ManyUpdatesToOneKey_CollapseToOneRow()
    {
        await ExecuteAsync($"INSERT INTO \"{_tableName}\" (\"Id\", \"Name\") VALUES (1, 'a')");
        var start = (await ReadAsync(null)).NewWatermark;

        for (var i = 0; i < 50; i++)
            await ExecuteAsync($"UPDATE \"{_tableName}\" SET \"Name\" = 'v{i}' WHERE \"Id\" = 1");

        var row = Assert.Single(await CollectAsync((await ReadAsync(start)).Rows));

        Assert.Equal("v49", row["Name"]);
        Assert.Equal(51, await ShadowRowCountAsync());
    }

    [Fact]
    public async Task WithTheOption_AcknowledgementPrunesWhatHasBeenApplied()
    {
        var options = new Dictionary<string, string> { [TriggerAuditReader.PruneOption] = "true" };

        await ExecuteAsync($"INSERT INTO \"{_tableName}\" (\"Id\", \"Name\") VALUES (1, 'Alice')");
        var start = (await ReadAsync(null, options)).NewWatermark;
        await _reader.AcknowledgeAsync(_connection, Source(), start, options, CancellationToken.None);

        Assert.Equal(0, await ShadowRowCountAsync());
    }

    [Fact]
    public async Task ACompositeKey_CollapsesAndIdentifiesADeleteByEveryPart()
    {
        var composite = $"TRG_COMPOSITE_{Guid.NewGuid():N}";
        await ExecuteAsync($"""
            CREATE TABLE "{composite}" (
                "Region" VARCHAR2(10) NOT NULL,
                "Id" NUMBER NOT NULL,
                "Name" VARCHAR2(50) NOT NULL,
                PRIMARY KEY ("Region", "Id")
            )
            """);
        _tablesToDrop.Add(composite);
        await ExecuteAsync(OracleTriggerAudit.CreateShadowTable(
            db.SchemaName, composite, ["\"Region\" VARCHAR2(10) NOT NULL", "\"Id\" NUMBER NOT NULL"]));
        _tablesToDrop.Add(TriggerAuditStatement.ShadowTableName(composite));
        await ExecuteAsync(OracleTriggerAudit.CreateTrigger(db.SchemaName, composite, ["Region", "Id"]));

        var source = new SourceTableRef
        {
            ConnectionName = "test", Database = "FREEPDB1", Schema = db.SchemaName, Table = composite,
        };
        List<CachedColumn> compositeColumns =
        [
            new("Region", "VARCHAR2(10)", false, true, false),
            new("Id", "NUMBER", false, true, false),
            new("Name", "VARCHAR2(50)", false, false, false),
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

        await ExecuteAsync($"INSERT INTO \"{composite}\" VALUES ('north', 1, 'a')");
        await ExecuteAsync($"INSERT INTO \"{composite}\" VALUES ('south', 1, 'b')");
        var start = (await Read(null)).NewWatermark;

        await ExecuteAsync($"UPDATE \"{composite}\" SET \"Name\" = 'c' WHERE \"Region\" = 'north'");
        await ExecuteAsync($"UPDATE \"{composite}\" SET \"Name\" = 'd' WHERE \"Region\" = 'north'");
        await ExecuteAsync($"DELETE FROM \"{composite}\" WHERE \"Region\" = 'south'");

        var rows = await CollectAsync((await Read(start)).Rows);

        Assert.Equal(2, rows.Count);
        var updated = Assert.Single(rows, r => r.Operation == ChangeOperation.Update);
        Assert.Equal("north", updated["Region"]);
        Assert.Equal("d", updated["Name"]);

        var deleted = Assert.Single(rows, r => r.Operation == ChangeOperation.Delete);
        Assert.Equal("south", deleted["Region"]);
        Assert.Equal(1m, Convert.ToDecimal(deleted["Id"]));
    }

    // ---- CW02a: a primary key update is a delete of the old key and the new row -------------------

    private string Q => $"\"{_tableName}\"";

    private async Task<List<ChangeRow>> ChangesSinceAsync(string start) =>
        await CollectAsync((await ReadAsync(start)).Rows);

    private static int Id(ChangeRow row) => Convert.ToInt32(row["Id"]);

    [Fact]
    public async Task AKeyUpdate_IsADeleteOfTheOldKeyAndTheNewRow()
    {
        await ExecuteAsync($"INSERT INTO {Q} (\"Id\", \"Name\") VALUES (1, 'Alice')");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"UPDATE {Q} SET \"Id\" = 2 WHERE \"Id\" = 1");

        var rows = await ChangesSinceAsync(start);

        Assert.Equal(2, rows.Count);
        Assert.Equal(ChangeOperation.Delete, Assert.Single(rows, r => Id(r) == 1).Operation);
        var moved = Assert.Single(rows, r => Id(r) == 2);
        Assert.NotEqual(ChangeOperation.Delete, moved.Operation);
        Assert.Equal("Alice", moved["Name"]);
    }

    [Fact]
    public async Task AnUpdateThatKeepsTheKey_IsStillOneRow()
    {
        await ExecuteAsync($"INSERT INTO {Q} (\"Id\", \"Name\") VALUES (1, 'Alice')");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"UPDATE {Q} SET \"Name\" = 'Alicia' WHERE \"Id\" = 1");

        var row = Assert.Single(await ChangesSinceAsync(start));
        Assert.Equal(ChangeOperation.Update, row.Operation);
    }

    [Fact]
    public async Task AKeyUpdateFollowedByAnUpdateOfTheNewRow_CarriesTheFinalValues()
    {
        await ExecuteAsync($"INSERT INTO {Q} (\"Id\", \"Name\") VALUES (1, 'Alice')");
        var start = (await ReadAsync(null)).NewWatermark;

        await ExecuteAsync($"UPDATE {Q} SET \"Id\" = 2 WHERE \"Id\" = 1");
        await ExecuteAsync($"UPDATE {Q} SET \"Name\" = 'Final' WHERE \"Id\" = 2");

        var rows = await ChangesSinceAsync(start);

        Assert.Equal(2, rows.Count);
        Assert.Equal(ChangeOperation.Delete, Assert.Single(rows, r => Id(r) == 1).Operation);
        Assert.Equal("Final", Assert.Single(rows, r => Id(r) == 2)["Name"]);
    }

    /// <summary>The trigger as it was before CW02a: the update branch records the new key only.</summary>
    private Task InstallOldTriggerAsync() => ExecuteAsync($"""
        CREATE OR REPLACE TRIGGER "{db.SchemaName}"."{OracleTriggerAudit.TriggerName(_tableName)}"
        AFTER INSERT OR UPDATE OR DELETE ON "{db.SchemaName}"."{_tableName}"
        FOR EACH ROW
        BEGIN
            IF INSERTING THEN
                INSERT INTO "{db.SchemaName}"."DS_Changes_{_tableName}" ("DS_Op", "Id") VALUES ('I', :NEW."Id");
            ELSIF UPDATING THEN
                INSERT INTO "{db.SchemaName}"."DS_Changes_{_tableName}" ("DS_Op", "Id") VALUES ('U', :NEW."Id");
            ELSE
                INSERT INTO "{db.SchemaName}"."DS_Changes_{_tableName}" ("DS_Op", "Id") VALUES ('D', :OLD."Id");
            END IF;
        END;
        """);

    [Fact]
    public async Task ATriggerOfTheOldText_IsOutdated_TheCurrentOneIsCurrent_AMissingOneIsAbsent()
    {
        Assert.Equal(TriggerAuditInstall.Current,
            await OracleTriggerInstall.InspectAsync(_connection, db.SchemaName, _tableName, CancellationToken.None));

        await InstallOldTriggerAsync();
        Assert.Equal(TriggerAuditInstall.Outdated,
            await OracleTriggerInstall.InspectAsync(_connection, db.SchemaName, _tableName, CancellationToken.None));

        Assert.Equal(TriggerAuditInstall.Absent,
            await OracleTriggerInstall.InspectAsync(_connection, db.SchemaName, "NO_SUCH_TABLE", CancellationToken.None));
    }

    [Fact]
    public async Task APassOverATriggerOfTheOldText_FailsNamingTheTableAndTheWayOut()
    {
        await ExecuteAsync($"INSERT INTO {Q} (\"Id\", \"Name\") VALUES (1, 'Alice')");
        var start = (await ReadAsync(null)).NewWatermark;
        await InstallOldTriggerAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(start));
        Assert.Contains(_tableName, failure.Message);
        Assert.Contains("Enable source change capture", failure.Message);

        await ExecuteAsync(OracleTriggerAudit.CreateTrigger(db.SchemaName, _tableName, ["Id"]));
        await ExecuteAsync($"UPDATE {Q} SET \"Id\" = 2 WHERE \"Id\" = 1");
        Assert.Equal(2, (await ChangesSinceAsync(start)).Count);
    }
}
