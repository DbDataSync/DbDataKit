using System.Security.Cryptography;

namespace DbDataSync.State.Tests;

/// <summary>
/// Plan 28c, E14 and decision ED15: a state database that Sync made before its state was kept in modules (one schema version for the whole database) is a clean break. It is not converted: opening it fails with a
/// message that names the database and the ways forward, and it is left exactly as it was, so the previous release still reads it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OldStateTests : IClassFixture<LibraryInstallFixture>, IDisposable
{
    private readonly string _tempDir = Directory.CreateTempSubdirectory("dbdatasync-old-state-").FullName;
    private readonly DbDataSync.Libraries.LibraryRegistry _libraries;
    private readonly List<StateEngineFixture> _fixtures = [];

    public OldStateTests(LibraryInstallFixture libraries) => _libraries = libraries.Registry;

    public void Dispose()
    {
        foreach (var fixture in _fixtures)
            fixture.Dispose();
        Directory.Delete(_tempDir, recursive: true);
    }

    /// <summary>
    /// The one table of the old structure these tests read (its positions), made the way the old structure made every table: a numbered schema script (plan 29a Q22 deleted the old list of
    /// scripts, <c>Migrations.Templates</c>; a database the old structure made is recognised by its schema version, which this writes too).
    /// </summary>
    private static readonly StateSchema OldSchema = new([
        """
        CREATE TABLE ChangeWatermarks (
            TaskName {{key}} NOT NULL,
            MappingName {{key}} NOT NULL,
            SourceTable {{key}} NOT NULL,
            Watermark {{text}} NULL,
            UpdatedAtUtc {{text}} NOT NULL,
            PRIMARY KEY (TaskName, MappingName, SourceTable)
        );
        """,
    ]);

    /// <summary>A database as the last release before the modules left it: the old structure, and a position in it.</summary>
    private static void MakeOld(string engine, string connectionString, string? marker = null)
    {
        var old = engine == StateEngineIds.Sqlite
            ? new DbDataKit.State.StateDatabase(connectionString, OldSchema)
            : new DbDataKit.State.StateDatabase(engine, connectionString, OldSchema);
        using var connection = old.OpenConnection();
        using var cmd = old.Command(connection, "INSERT INTO ChangeWatermarks (TaskName, MappingName, SourceTable, Watermark, UpdatedAtUtc) VALUES ('crm-sync', 'orders', 'dbo.Orders', '0000002A:00000140:0003', '2026-11-01T10:00:00Z');");
        cmd.ExecuteNonQuery();
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public void AnOldStateFile_IsRefusedNamingTheFileAndTheWaysToCarryOn_AndIsLeftUntouched()
    {
        var path = Path.Combine(_tempDir, "state.db");
        MakeOld(StateEngineIds.Sqlite, path);
        var before = Hash(path);

        var refused = Assert.Throws<OldStateException>(() => new StateDatabase(path));

        Assert.Contains(path, refused.Message);
        Assert.Equal(path, refused.Database);
        Assert.Contains("is not converted", refused.Message);
        Assert.Contains("ChangesFromEarliest", refused.Message);   // replay from the earliest position the source still has, a table whose position is gone reloaded alone
        Assert.Contains("by hand", refused.Message);   // set each mapping's position
        Assert.Contains("previous release", refused.Message);   // which still reads the file
        Assert.Contains("new state database", refused.Message);
        Assert.DoesNotContain("convert ", refused.Message.Replace("is not converted", ""), StringComparison.Ordinal);   // there is no 'state convert' command
        Assert.Equal(before, Hash(path));
    }

    [Fact]
    public void TheOldFile_StillReadsWithTheOldSchema_AfterTheRefusal()
    {
        var path = Path.Combine(_tempDir, "state.db");
        MakeOld(StateEngineIds.Sqlite, path);
        Assert.Throws<OldStateException>(() => new StateDatabase(path));

        var old = new DbDataKit.State.StateDatabase(path, OldSchema);
        using var connection = old.OpenConnection();
        using var cmd = old.Command(connection, "SELECT Watermark FROM ChangeWatermarks WHERE TaskName = 'crm-sync';");
        Assert.Equal("0000002A:00000140:0003", cmd.ExecuteScalar());
    }

    [Fact]
    public void ANewFileIsNotRefused_AndAnEmptyOneNextToTheOldOneIsFine()
    {
        MakeOld(StateEngineIds.Sqlite, Path.Combine(_tempDir, "state.db"));

        var fresh = new StateDatabase(Path.Combine(_tempDir, "new-state.db"));

        Assert.Contains(fresh.RegisteredModules(), m => m.Module == SyncStateModule.Name);
    }

    [Theory]
    [InlineData(StateEngineIds.MsSql)]
    [InlineData(StateEngineIds.Postgres)]
    public void AnOldDatabaseOnAServer_IsRefusedNamingTheDatabase(string engine)
    {
        var fixture = new StateEngineFixture(engine, _libraries, openState: false);
        _fixtures.Add(fixture);
        MakeOld(engine, fixture.ConnectionString);

        var refused = Assert.Throws<OldStateException>(() => new StateDatabase(engine, fixture.ConnectionString));

        Assert.Contains("the database '", refused.Message);
        Assert.Contains("dbdatasync_state_", refused.Message);   // the scratch database's name
        Assert.Contains("ChangesFromEarliest", refused.Message);
    }
}
