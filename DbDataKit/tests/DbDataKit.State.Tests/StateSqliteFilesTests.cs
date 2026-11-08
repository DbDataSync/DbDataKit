using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q24: a namespace in a SQLite file of its own, attached when the database is opened, so a module that writes heavily does not make another's writes wait.</summary>
public sealed class StateSqliteFilesTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("dbdatakit-files-").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    private string Main => Path.Combine(_directory, "state.db");

    private static StateModule Heavy() => new("heavy", StateScopeKind.Install,
        ["CREATE TABLE {table:event} (id {{key}} PRIMARY KEY, body {{text}} NULL); CREATE INDEX {index:event_by_body} ON {table:event}(body);"]);

    private static StateModule HeavyChild(StateModule heavy) => new("heavychild", StateScopeKind.Install,
        ["CREATE TABLE {table:detail} (id {{key}} PRIMARY KEY, event_id {{key}} NOT NULL REFERENCES {table:heavy.event}(id));"], dependsOn: [heavy]);

    private static StateModule Light() => new("light", StateScopeKind.Install, ["CREATE TABLE {table:note} (id {{key}} PRIMARY KEY);"]);

    private static StateNamespaceSettings InFile(string module, string file) =>
        new() { Modules = new Dictionary<string, NamespaceSetting> { [module] = new(File: file) } };

    private StateDatabase Open(StateNamespaceSettings settings, params StateModule[] modules) =>
        new(Main, StateLayout.Create(StateEngineIds.Sqlite, modules, settings: settings));

    private static void Run(StateDatabase db, string sql)
    {
        using var connection = db.OpenConnection();
        using var command = db.Command(connection, sql);
        command.ExecuteNonQuery();
    }

    private static long Count(string file, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    [Fact]
    public void A_namespace_in_a_file_of_its_own_has_its_tables_there_and_the_rest_stays_in_the_main_file()
    {
        var db = Open(InFile("heavy", "heavy.db"), Heavy(), Light());

        Run(db, "INSERT INTO {table:heavy.event} (id, body) VALUES ('e1', 'x');".Replace("{table:heavy.event}", db.Layout!.Table("heavy", "event")));
        Run(db, "INSERT INTO " + db.Layout.Table("light", "note") + " (id) VALUES ('n1');");

        var heavyFile = Path.Combine(_directory, "heavy.db");
        Assert.True(File.Exists(heavyFile));
        Assert.Equal(1, Count(heavyFile, "SELECT COUNT(*) FROM dbdatakit_heavy_event;"));
        Assert.Equal(0, Count(Main, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'dbdatakit_heavy_event';"));     // not in the main file
        Assert.Equal(1, Count(Main, "SELECT COUNT(*) FROM dbdatakit_light_note;"));
        Assert.Equal(1, Count(heavyFile, "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = 'dbdatakit_heavy_event_by_body';"));   // its index went with its table
        Assert.StartsWith("f_heavy.", db.Layout.Table("heavy", "event"));
    }

    [Fact]
    public void Modules_that_refer_to_each_other_in_one_file_keep_their_foreign_key_and_reopening_applies_nothing()
    {
        var heavy = Heavy();
        var settings = new StateNamespaceSettings { Modules = new Dictionary<string, NamespaceSetting> { ["heavy"] = new(File: "both.db"), ["heavychild"] = new(File: "both.db") } };
        var db = Open(settings, HeavyChild(heavy));
        Run(db, "INSERT INTO " + db.Layout!.Table("heavy", "event") + " (id) VALUES ('e1');");
        Run(db, "INSERT INTO " + db.Layout.Table("heavychild", "detail") + " (id, event_id) VALUES ('d1', 'e1');");

        var again = Open(settings, HeavyChild(heavy));   // the registry knows where they are: nothing to apply

        using var connection = again.OpenConnection();
        using var command = again.Command(connection, "SELECT COUNT(*) FROM " + again.Layout!.Table("heavychild", "detail") + ";");
        Assert.Equal(1L, Convert.ToInt64(command.ExecuteScalar()));
    }

    [Fact]
    public void A_split_that_would_put_a_foreign_key_across_two_files_is_refused_naming_both_modules()
    {
        var heavy = Heavy();

        var ex = Assert.Throws<InvalidOperationException>(() => StateLayout.Create(StateEngineIds.Sqlite, [HeavyChild(heavy)], settings: InFile("heavy", "heavy.db")));

        Assert.Contains("heavychild", ex.Message);
        Assert.Contains("heavy", ex.Message);
        Assert.Contains("different SQLite files", ex.Message);
    }

    [Fact]
    public void Moving_a_module_to_another_file_is_refused_rather_than_starting_it_again_empty()
    {
        Open(InFile("heavy", "heavy.db"), Heavy(), Light());

        var ex = Assert.Throws<InvalidOperationException>(() => Open(InFile("heavy", "elsewhere.db"), Heavy(), Light()));
        Assert.Contains("'heavy'", ex.Message);
        Assert.Contains("heavy.db", ex.Message);
        Assert.Throws<InvalidOperationException>(() => Open(new StateNamespaceSettings(), Heavy(), Light()));   // back into the main file: also refused
    }

    [Fact]
    public void A_file_is_a_SQLite_setting_and_two_files_with_one_name_are_refused()
    {
        var onServer = Assert.Throws<InvalidOperationException>(() => StateLayout.Create(StateEngineIds.Postgres, [Heavy()], settings: InFile("heavy", "heavy.db")));
        Assert.Contains("SQLite setting", onServer.Message);

        var settings = new StateNamespaceSettings { Modules = new Dictionary<string, NamespaceSetting> { ["heavy"] = new(File: "a/shared.db"), ["light"] = new(File: "b/shared.db") } };
        var clash = Assert.Throws<InvalidOperationException>(() => StateLayout.Create(StateEngineIds.Sqlite, [Heavy(), Light()], settings: settings));
        Assert.Contains("different names", clash.Message);
    }

    [Fact]
    public void A_long_write_to_one_file_does_not_make_a_write_to_another_wait()
    {
        var db = Open(InFile("heavy", "heavy.db"), Heavy(), Light());
        using var _ = SqliteLockGuard.Allow();   // the point of the test is a connection opened while another holds a lock

        // another connection is in the middle of a big write to the heavy module's file: it holds that file's write lock
        using var busy = db.OpenConnection();
        using var transaction = db.BeginTransaction(busy, "heavy");   // deferred: it locks only the heavy file
        using (var insert = db.Command(busy, transaction, "INSERT INTO " + db.Layout!.Table("heavy", "event") + " (id) VALUES ('long');")) insert.ExecuteNonQuery();

        var watch = Stopwatch.StartNew();
        Run(db, "INSERT INTO " + db.Layout.Table("light", "note") + " (id) VALUES ('n1');");
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"the write to the light module waited {watch.Elapsed.TotalSeconds:F1} s for a write to the heavy module's file");
        transaction.Rollback();
    }

    [Fact]
    public void An_ordinary_transaction_is_immediate_and_does_lock_every_file_which_is_why_the_stores_of_such_a_module_begin_theirs_through_the_database()
    {
        var db = Open(InFile("heavy", "heavy.db"), Heavy(), Light());
        using var _ = SqliteLockGuard.Allow();   // the point of the test is a connection opened while another holds a lock
        using var busy = db.OpenConnection();
        using var transaction = busy.BeginTransaction();   // Microsoft.Data.Sqlite: BEGIN IMMEDIATE, on the main file too
        using (var insert = db.Command(busy, transaction, "INSERT INTO " + db.Layout!.Table("heavy", "event") + " (id) VALUES ('long');")) insert.ExecuteNonQuery();

        using var other = db.OpenConnection();
        using var write = db.Command(other, "PRAGMA busy_timeout = 100;");
        write.ExecuteNonQuery();
        using var blocked = db.Command(other, "INSERT INTO " + db.Layout.Table("light", "note") + " (id) VALUES ('n1');");

        Assert.Throws<SqliteException>(() => blocked.ExecuteNonQuery());
        transaction.Rollback();
    }
}
