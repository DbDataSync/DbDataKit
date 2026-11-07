using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Monitoring;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29b X02: the snapshot importer, between every pair of SQLite, PostgreSQL and SQL Server.</summary>
public sealed class StateSnapshotImporterTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-07T09:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly List<string> _directories = [];
    private readonly StateTestClock _clock = new(Start);

    private static readonly StateModule[] AllModules = [.. SnapshotPopulate.Modules, SnapshotTestModules.Types];

    public void Dispose()
    {
        _engines.Dispose();
        foreach (var directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temp folder left behind is untidy and nothing else */ }
        }
    }

    public static TheoryData<string, string> Pairs
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var source in new[] { StateEngineIds.Sqlite, StateEngineIds.Postgres, StateEngineIds.MsSql })
            {
                foreach (var target in new[] { StateEngineIds.Sqlite, StateEngineIds.Postgres, StateEngineIds.MsSql })
                    data.Add(source, target);
            }

            return data;
        }
    }

    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "dbdatakit-import-" + Guid.NewGuid().ToString("N"));
        _directories.Add(path);
        return path;
    }

    private StateDatabase OpenSource(string engine, params StateModule[] modules) => _engines.Provision(engine).Open(modules, StateScope.Standalone, time: _clock);

    /// <summary>An empty database that has not been migrated, with a layout that differs from the default one (schema and prefix), so a table is found by its logical name.</summary>
    private StateDatabase OpenTarget(string engine, StateModule[] modules, bool differentNames = true)
    {
        var location = _engines.Provision(engine);
        return OpenTarget(location, modules, differentNames);
    }

    private static StateDatabase OpenTarget(StateEngines.Location location, StateModule[] modules, bool differentNames = true)
    {
        var sqlite = location.Engine == StateEngineIds.Sqlite;
        var settings = differentNames
            ? new StateNamespaceSettings { Default = new NamespaceSetting(Schema: sqlite ? null : "imp_{module}", Prefix: sqlite ? "imp_{module}_" : "z_") }
            : null;
        var layout = StateLayout.Create(location.Engine, modules, StateScope.Standalone, settings);
        return sqlite ? StateDatabase.OpenSqliteForImport(location.Target, layout, time: new StateTestClock(Start)) : StateDatabase.OpenForImport(location.Engine, location.Target, layout, time: new StateTestClock(Start));
    }

    private static Task<SnapshotResult> Export(StateDatabase database, string directory, StateModule[] modules) =>
        StateSnapshotWriter.WriteAsync(database, modules, StateScope.Standalone, directory, SnapshotOptions.Default, null, CancellationToken.None);

    private static Task<SnapshotImportResult> Import(string directory, StateDatabase target, StateModule[] modules, ImportOptions? options = null, IProgress<SnapshotProgress>? progress = null, CancellationToken ct = default) =>
        StateSnapshotImporter.ImportAsync(directory, target, modules, StateScope.Standalone, options ?? ImportOptions.Default, progress, ct);

    private sealed class SyncProgress(Action<SnapshotProgress> report) : IProgress<SnapshotProgress>
    {
        public void Report(SnapshotProgress value) => report(value);
    }

    /// <summary>The snapshot copied so a test can damage it.</summary>
    private string Copy(string directory)
    {
        var copy = NewDirectory();
        foreach (var path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(copy, Path.GetRelativePath(directory, path));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(path, destination);
        }

        return copy;
    }

    private static void Flip(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, bytes);
    }

    private static void EditManifest(string directory, Action<JsonNode> edit)
    {
        var path = Path.Combine(directory, "manifest.json");
        var node = JsonNode.Parse(File.ReadAllText(path))!;
        edit(node);
        File.WriteAllText(path, node.ToJsonString());
    }

    private static JsonNode ModuleNode(JsonNode manifest, string module) => manifest["modules"]!.AsArray().Single(m => m!["name"]!.GetValue<string>() == module)!;

    private static JsonNode TableNode(JsonNode manifest, string module, string table) => ModuleNode(manifest, module)["tables"]!.AsArray().Single(t => t!["name"]!.GetValue<string>() == table)!;

    private static long Count(StateDatabase database, string module, string table)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(connection, $"SELECT COUNT(*) FROM {{table:{module}.{table}}};");
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<string> RegisteredModules(StateDatabase database)
    {
        using var connection = database.OpenConnection();
        if (!database.Dialect.TableExists(connection, database.Layout!.Registry)) return [];
        using var command = database.Command(connection, $"SELECT module FROM {database.Layout.Registry} ORDER BY module;");
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private static bool TableExists(StateDatabase database, string module, string table)
    {
        using var connection = database.OpenConnection();
        return database.Dialect.TableExists(connection, database.Layout!.Table(module, table));
    }

    /// <summary>Exports the target again with the exporter and compares every table with the manifest it was imported from: counts and row digests.</summary>
    private async Task AssertSameAsManifest(StateDatabase target, SnapshotManifest manifest, params string[] except)
    {
        var again = await Export(target, NewDirectory(), [.. manifest.Modules.Select(m => target.Layout!.Namespace(m.Name).Module)]);
        foreach (var module in manifest.Modules)
        {
            var exported = again.Manifest.Modules.Single(m => m.Name == module.Name);
            Assert.Equal(module.Version, exported.Version);
            Assert.Equal(module.Checksum, exported.Checksum);
            foreach (var table in module.Tables)
            {
                var other = exported.Tables.Single(t => t.Name == table.Name);
                Assert.Equal(table.Rows, other.Rows);
                if (!except.Contains($"{module.Name}.{table.Name}"))
                    Assert.True(table.RowsDigest == other.RowsDigest, $"{module.Name}.{table.Name}: the target's rows_digest {other.RowsDigest} is not the manifest's {table.RowsDigest}");
            }
        }

        Assert.Equal(manifest.Payloads.Select(p => (p.Id, p.Size, p.Sha256, p.Chunks)), again.Manifest.Payloads.Select(p => (p.Id, p.Size, p.Sha256, p.Chunks)).OrderBy(p => manifest.Payloads.ToList().FindIndex(q => q.Id == p.Id)));
    }

    // ---------------------------------------------------------------- the nine pairs

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task A_populated_state_round_trips_between_every_pair_of_engines(string sourceEngine, string targetEngine)
    {
        var source = OpenSource(sourceEngine, AllModules);
        var body = SnapshotPopulate.Fill(source);
        StateSnapshotWriterTests.FillTypes(source);
        var directory = NewDirectory();
        var exported = await Export(source, directory, AllModules);
        var users = new IdentityUserStore(source).List().Select(u => (u.Id, u.DisplayName, u.Role)).Order().ToList();
        var runs = new RunStore(source, "dbdatasync").Read().Select(r => r.Id).Order().ToList();
        byte[] bodyBytes;
        using (var reader = new PayloadStore(source, chunkSize: 1024).OpenRead(body))
        using (var copy = new MemoryStream())
        {
            reader.CopyTo(copy);
            bodyBytes = copy.ToArray();
        }

        var target = OpenTarget(targetEngine, AllModules);
        var phases = new List<string>();
        var result = await Import(directory, target, AllModules, progress: new SyncProgress(p => { lock (phases) phases.Add($"{p.Phase}:{p.Module}.{p.Table}"); }));

        // the target's rows are the manifest's (checked by the exporter, independently of the importer)
        Assert.Equal(exported.Rows, result.Rows);
        Assert.Equal(exported.Tables, result.Tables);
        Assert.Equal(target.Layout!.Namespaces.Select(n => n.Module.Name), result.Modules);
        Assert.True(result.Migrated);
        Assert.Empty(result.Warnings);
        await AssertSameAsManifest(target, exported.Manifest);

        // progress per table, and the end
        Assert.Contains("table-start:identity.account", phases);
        Assert.Contains("table-done:payloads.chunk", phases);
        Assert.Contains("module-done:runs.", phases);
        Assert.EndsWith("done:.", phases[^1]);

        // the products' stores read it
        Assert.Equal(users, new IdentityUserStore(target).List().Select(u => (u.Id, u.DisplayName, u.Role)).Order().ToList());
        var runStore = new RunStore(target, "dbdatasync");
        Assert.Equal(runs, runStore.Read().Select(r => r.Id).Order().ToList());
        var finished = runStore.Read(new RunQuery(JobId: "orders-sync")).Single();
        Assert.Equal(RunOutcome.Failed, finished.Run.Outcome);
        Assert.Equal(40, runStore.Logs(finished.Id, limit: 1000).Count);
        using (var reader = new PayloadStore(target, chunkSize: 1024).OpenRead(body))
        using (var copy = new MemoryStream())
        {
            reader.CopyTo(copy);   // checks the size and hash of the payload at its end
            Assert.Equal(bodyBytes, copy.ToArray());
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bodyBytes)), reader.Info.Sha256);
        }

        Assert.Equal(25, Count(target, "audit", "entry"));
        Assert.Equal(2, new NotificationFeedStore(target, "sync").List().Count);

        // and writes: the identity-style keys go on from where the snapshot's stopped
        var maxLog = runStore.Logs(finished.Id, limit: 1000).Max(l => l.Id);
        Assert.True(runStore.Log(finished.Id, "Info", "after the import") > maxLog);
    }

    // ---------------------------------------------------------------- refusals

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_snapshot_with_a_newer_module_version_is_refused_naming_it(string engine)
    {
        var source = OpenSource(StateEngineIds.Sqlite, SnapshotTestModules.Types);
        StateSnapshotWriterTests.FillTypes(source);
        var directory = NewDirectory();
        await Export(source, directory, [SnapshotTestModules.Types]);
        var edited = Copy(directory);
        EditManifest(edited, m => ModuleNode(m, "snapshot_types")["version"] = 3);

        var target = OpenTarget(engine, [SnapshotTestModules.Types]);
        var failure = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(edited, target, [SnapshotTestModules.Types]));

        Assert.Equal(SnapshotImportRefusal.ModuleNewer, failure.Reason);
        Assert.Contains("snapshot_types", failure.Message);
        Assert.Contains("version 3", failure.Message);
        Assert.Contains("version 2", failure.Message);
        Assert.Empty(RegisteredModules(target));   // nothing was written
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_non_empty_target_is_refused_naming_the_module(string engine)
    {
        var source = OpenSource(StateEngineIds.Sqlite, SnapshotTestModules.Types);
        StateSnapshotWriterTests.FillTypes(source);
        var directory = NewDirectory();
        await Export(source, directory, [SnapshotTestModules.Types]);

        var location = _engines.Provision(engine);
        var used = location.Open([SnapshotTestModules.Types], StateScope.Standalone);   // a database that already has the module
        var failure = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(directory, used, [SnapshotTestModules.Types]));

        Assert.Equal(SnapshotImportRefusal.TargetNotEmpty, failure.Reason);
        Assert.Contains("snapshot_types", failure.Message);
        Assert.Equal(0, Count(used, "snapshot_types", "sample"));   // untouched
    }

    [Fact]
    public async Task A_migration_that_was_edited_is_refused_naming_the_module_and_the_number_of_migrations()
    {
        var source = OpenSource(StateEngineIds.Sqlite, SnapshotTestModules.Types);
        var directory = NewDirectory();
        await Export(source, directory, [SnapshotTestModules.Types]);
        var edited = new StateModule("snapshot_types", StateScopeKind.Install, [SnapshotTestModules.Types.Migrations[0], SnapshotTestModules.Types.Migrations[1] + "\n-- edited"]);

        var target = OpenTarget(StateEngineIds.Sqlite, [edited]);
        var failure = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(directory, target, [edited]));

        Assert.Equal(SnapshotImportRefusal.ChecksumMismatch, failure.Reason);
        Assert.Contains("snapshot_types", failure.Message);
        Assert.Contains("first 2 migrations", failure.Message);
        Assert.Empty(RegisteredModules(target));
    }

    [Fact]
    public async Task A_missing_unreadable_or_foreign_manifest_is_refused_naming_the_file()
    {
        var source = OpenSource(StateEngineIds.Sqlite, SnapshotTestModules.Types);
        var directory = NewDirectory();
        await Export(source, directory, [SnapshotTestModules.Types]);
        var target = OpenTarget(StateEngineIds.Sqlite, [SnapshotTestModules.Types]);

        var empty = NewDirectory();
        Directory.CreateDirectory(empty);
        var missing = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(empty, target, [SnapshotTestModules.Types]));
        Assert.Equal(SnapshotImportRefusal.ManifestMissing, missing.Reason);
        Assert.Contains("manifest.json", missing.Message);

        var garbage = Copy(directory);
        File.WriteAllText(Path.Combine(garbage, "manifest.json"), "{ not json");
        var corrupt = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(garbage, target, [SnapshotTestModules.Types]));
        Assert.Equal(SnapshotImportRefusal.ManifestCorrupt, corrupt.Reason);
        Assert.Contains("manifest.json", corrupt.Message);

        var foreign = Copy(directory);
        EditManifest(foreign, m => m["format"] = "state-snapshot/2");
        var format = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(foreign, target, [SnapshotTestModules.Types]));
        Assert.Equal(SnapshotImportRefusal.FormatUnsupported, format.Reason);
        Assert.Contains("state-snapshot/2", format.Message);
        Assert.Contains("manifest.json", format.Message);

        var damaged = Copy(directory);
        EditManifest(damaged, m => ModuleNode(m, "snapshot_types").AsObject().Remove("tables"));
        var member = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(damaged, target, [SnapshotTestModules.Types]));
        Assert.Equal(SnapshotImportRefusal.ManifestCorrupt, member.Reason);
        Assert.Contains("tables", member.Message);

        Assert.Empty(RegisteredModules(target));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_tampered_table_file_and_a_tampered_payload_file_are_refused_naming_the_file(string engine)
    {
        var source = OpenSource(StateEngineIds.Sqlite, AllModules);
        SnapshotPopulate.Fill(source);
        StateSnapshotWriterTests.FillTypes(source);
        var directory = NewDirectory();
        var exported = await Export(source, directory, AllModules);

        var tampered = Copy(directory);
        var file = exported.Manifest.Modules.Single(m => m.Name == "audit").Tables.Single().File;
        Flip(Path.Combine(tampered, file));
        var target = OpenTarget(engine, AllModules);
        var table = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(tampered, target, AllModules));
        Assert.Equal(SnapshotImportRefusal.FileCorrupt, table.Reason);
        Assert.Contains(file, table.Message);
        Assert.Empty(RegisteredModules(target));   // refused before anything was written

        // a payload file: caught while it is streamed into the chunk rows, so the payloads module is rolled back
        var payloadTampered = Copy(directory);
        var payload = exported.Manifest.Payloads.OrderByDescending(p => p.Size).First();
        Flip(Path.Combine(payloadTampered, payload.File));
        var target2 = OpenTarget(engine, AllModules);
        var failure = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(payloadTampered, target2, AllModules, new ImportOptions { ResetOnFailure = true }));
        Assert.Equal(SnapshotImportRefusal.FileCorrupt, failure.Reason);
        Assert.Contains(payload.File, failure.Message);
        Assert.DoesNotContain("payloads", failure.CompletedModules);
        Assert.True(failure.WasReset || failure.CompletedModules.Count == 0);
        Assert.Empty(RegisteredModules(target2));

        // a payload file that is shorter than the manifest says
        var truncated = Copy(directory);
        using (var stream = File.Open(Path.Combine(truncated, payload.File), FileMode.Open)) stream.SetLength(payload.Size - 1);
        var target3 = OpenTarget(engine, AllModules);
        var shorter = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(truncated, target3, AllModules));
        Assert.Contains(payload.File, shorter.Message);
    }

    // ---------------------------------------------------------------- an older snapshot

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task An_older_snapshot_is_imported_at_its_version_and_migrated_up(string sourceEngine, string targetEngine)
    {
        // the module as it was shipped when it had its first migration only
        var first = new StateModule("snapshot_types", StateScopeKind.Install, [SnapshotTestModules.Types.Migrations[0]]);
        var source = OpenSource(sourceEngine, first);
        const string insert = "INSERT INTO {table:sample} (id, label, note, created_at, seen_at, enabled, amount, doc, data, size) VALUES ($id, $label, NULL, $created, NULL, $enabled, NULL, NULL, NULL, $size);";
        StateSnapshotWriterTests.Execute(source, "snapshot_types", insert, c => c.Bind(source, "id", Guid.NewGuid()).Bind(source, "label", "old").Bind(source, "created", Start).Bind(source, "enabled", true).Bind(source, "size", 1L));
        StateSnapshotWriterTests.Execute(source, "snapshot_types", "INSERT INTO {table:counter} (name) VALUES ('c');");
        var directory = NewDirectory();
        var exported = await Export(source, directory, [first]);
        Assert.Equal(1, Assert.Single(exported.Manifest.Modules).Version);

        // not migrated afterwards: the target is at the snapshot's version, with the first migration's columns only
        var plain = OpenTarget(targetEngine, [SnapshotTestModules.Types]);
        var kept = await Import(directory, plain, [SnapshotTestModules.Types], new ImportOptions { MigrateAfterImport = false });
        Assert.False(kept.Migrated);
        Assert.Equal(1, RegisteredVersion(plain, "snapshot_types"));
        Assert.Equal(1, Count(plain, "snapshot_types", "sample"));
        Assert.False(HasColumn(plain, "snapshot_types", "sample", "extra"));

        // and the normal migrator brings it up when asked (the default), or later by opening the database as usual
        var migrated = OpenTarget(targetEngine, [SnapshotTestModules.Types]);
        var result = await Import(directory, migrated, [SnapshotTestModules.Types]);
        Assert.True(result.Migrated);
        Assert.Equal(2, RegisteredVersion(migrated, "snapshot_types"));
        Assert.True(HasColumn(migrated, "snapshot_types", "sample", "extra"));
        Assert.Equal(1, Count(migrated, "snapshot_types", "sample"));
        StateSnapshotWriterTests.Execute(migrated, "snapshot_types", "UPDATE {table:sample} SET extra = 'migrated';");
    }

    private static int RegisteredVersion(StateDatabase database, string module)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(connection, $"SELECT version FROM {database.Layout!.Registry} WHERE module = $module;");
        command.Bind(database, "module", module);
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool HasColumn(StateDatabase database, string module, string table, string column)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(connection, $"SELECT * FROM {{table:{module}.{table}}} WHERE 1 = 0;");
        using var reader = command.ExecuteReader();
        return Enumerable.Range(0, reader.FieldCount).Any(i => reader.GetName(i) == column);
    }

    // ---------------------------------------------------------------- identity keys

    [Theory]
    [MemberData(nameof(Pairs))]
    public async Task An_identity_column_is_reseeded_so_the_next_insert_gets_a_fresh_id(string sourceEngine, string targetEngine)
    {
        var source = OpenSource(sourceEngine, SnapshotTestModules.Types);
        for (var i = 1; i <= 6; i++)
            StateSnapshotWriterTests.Execute(source, "snapshot_types", "INSERT INTO {table:counter} (name) VALUES ($name);", c => c.Bind(source, "name", $"n{i}"));
        // leave gaps: ids 3 and 5 stay, and the sequence of the source is at 6
        StateSnapshotWriterTests.Execute(source, "snapshot_types", "DELETE FROM {table:counter} WHERE id NOT IN (3, 5);");
        var directory = NewDirectory();
        var exported = await Export(source, directory, [SnapshotTestModules.Types]);
        Assert.Equal([3L, 5L], await IdsOf(source));

        var target = OpenTarget(targetEngine, [SnapshotTestModules.Types]);
        await Import(directory, target, [SnapshotTestModules.Types]);

        Assert.Equal([3L, 5L], await IdsOf(target));   // the explicit ids were kept
        await AssertSameAsManifest(target, exported.Manifest);
        StateSnapshotWriterTests.Execute(target, "snapshot_types", "INSERT INTO {table:counter} (name) VALUES ('next');");
        Assert.Equal([3L, 5L, 6L], await IdsOf(target));
    }

    private static Task<List<long>> IdsOf(StateDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(connection, "SELECT id FROM {table:snapshot_types.counter} ORDER BY id;");
        using var reader = command.ExecuteReader();
        var ids = new List<long>();
        while (reader.Read()) ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
        return Task.FromResult(ids);
    }

    // ---------------------------------------------------------------- failure and cancellation

    /// <summary>A snapshot of the payloads module and the test types, and the same with a duplicate key row put into <c>pair</c> (the manifest's hash and row count fixed up so the file passes the checks).</summary>
    private async Task<(string Good, string Bad)> SnapshotWithBadRow()
    {
        var modules = new[] { PayloadStore.Module, SnapshotTestModules.Types };
        var source = OpenSource(StateEngineIds.Sqlite, modules);
        using (var writer = new PayloadStore(source, chunkSize: 100).OpenWrite())
        {
            writer.Write(new byte[250]);
            writer.Complete();
        }

        StateSnapshotWriterTests.FillTypes(source);
        var good = NewDirectory();
        var exported = await Export(source, good, modules);
        var bad = Copy(good);
        var relative = exported.Manifest.Modules.Single(m => m.Name == "snapshot_types").Tables.Single(t => t.Name == "pair").File;
        var path = Path.Combine(bad, relative);
        List<string> lines;
        using (var gzip = new GZipStream(File.OpenRead(path), CompressionMode.Decompress))
        using (var reader = new StreamReader(gzip, Encoding.UTF8))
            lines = [.. reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)];
        lines.Insert(50, lines[3]);   // the same key twice
        using (var output = File.Create(path))
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
            gzip.Write(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));
        EditManifest(bad, m =>
        {
            var table = TableNode(m, "snapshot_types", "pair");
            table["sha256"] = SnapshotFiles.Sha256Of(path);
            table["rows"] = lines.Count;
        });
        return (good, bad);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_failure_in_the_middle_of_a_module_rolls_that_module_back(string engine)
    {
        var modules = new[] { PayloadStore.Module, SnapshotTestModules.Types };
        var (good, bad) = await SnapshotWithBadRow();
        var target = OpenTarget(engine, modules);

        var failure = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(bad, target, modules));

        Assert.Equal(SnapshotImportRefusal.LoadFailed, failure.Reason);
        Assert.Contains("snapshot_types.pair", failure.Message);
        Assert.Equal(["payloads"], failure.CompletedModules);
        Assert.False(failure.WasReset);
        // the module that failed is gone as if it had never been started: no tables, no registry row; the one before it is complete
        Assert.Equal(["payloads"], RegisteredModules(target));
        Assert.False(TableExists(target, "snapshot_types", "sample"));
        Assert.False(TableExists(target, "snapshot_types", "pair"));
        Assert.Equal(3 + 0, Count(target, "payloads", "chunk"));

        // not empty any more, so a retry is refused naming the module that is there ...
        var refused = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(good, target, modules));
        Assert.Equal(SnapshotImportRefusal.TargetNotEmpty, refused.Reason);
        Assert.Contains("payloads", refused.Message);

        // ... until Reset drops what the import created; then the good snapshot goes in
        await StateSnapshotImporter.ResetAsync(target, failure.CompletedModules, CancellationToken.None);
        Assert.Empty(RegisteredModules(target));
        Assert.False(TableExists(target, "payloads", "chunk"));
        var result = await Import(good, target, modules);
        Assert.Equal(["payloads", "snapshot_types"], RegisteredModules(target).Order());
        Assert.Equal(100, Count(target, "snapshot_types", "pair"));
        Assert.Equal(2, result.Modules.Count);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task ResetOnFailure_leaves_an_empty_target_for_a_clean_retry(string engine)
    {
        var modules = new[] { PayloadStore.Module, SnapshotTestModules.Types };
        var (good, bad) = await SnapshotWithBadRow();
        var target = OpenTarget(engine, modules);

        var failure = await Assert.ThrowsAsync<SnapshotImportException>(() => Import(bad, target, modules, new ImportOptions { ResetOnFailure = true }));

        Assert.Equal(["payloads"], failure.CompletedModules);
        Assert.True(failure.WasReset);
        Assert.Empty(RegisteredModules(target));
        Assert.False(TableExists(target, "payloads", "payload"));
        await Import(good, target, modules);
        Assert.Equal(3, Count(target, "payloads", "chunk"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Cancelling_leaves_the_target_as_it_was_after_the_last_completed_module(string engine)
    {
        var modules = new[] { PayloadStore.Module, SnapshotTestModules.Types };
        var (good, _) = await SnapshotWithBadRow();
        var target = OpenTarget(engine, modules);
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress(p =>
        {
            if (p.Phase == "module-done" && p.Module == "payloads") cancel.Cancel();
        });

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Import(good, target, modules, progress: progress, ct: cancel.Token));

        Assert.Contains("payloads", failure.Message);
        Assert.Equal(["payloads"], RegisteredModules(target));
        Assert.False(TableExists(target, "snapshot_types", "sample"));
    }

    // ---------------------------------------------------------------- batching and layout

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Rows_are_loaded_in_small_batches_whatever_the_limits(string engine)
    {
        var source = OpenSource(StateEngineIds.Sqlite, SnapshotTestModules.Types);
        StateSnapshotWriterTests.FillTypes(source);
        var directory = NewDirectory();
        var exported = await Export(source, directory, [SnapshotTestModules.Types]);

        foreach (var options in new[] { new ImportOptions { BatchRows = 1 }, new ImportOptions { BatchRows = 7, MaxParameters = 10 }, new ImportOptions { MaxParameters = 3 }, new ImportOptions { BatchRows = 1000, BatchBytes = 1 } })
        {
            var target = OpenTarget(engine, [SnapshotTestModules.Types]);
            await Import(directory, target, [SnapshotTestModules.Types], options);
            await AssertSameAsManifest(target, exported.Manifest);
        }
    }

    [Theory]
    [InlineData(StateEngineIds.Postgres)]
    [InlineData(StateEngineIds.MsSql)]
    public async Task The_optimised_loaders_load_the_same_rows_as_the_batched_insert(string engine)
    {
        var source = OpenSource(StateEngineIds.Sqlite, AllModules);
        var body = SnapshotPopulate.Fill(source);
        StateSnapshotWriterTests.FillTypes(source);
        var directory = NewDirectory();
        var exported = await Export(source, directory, AllModules);

        ISnapshotBulkLoader loader = engine == StateEngineIds.Postgres ? new PostgresCopyLoader() : new SqlServerBulkCopyLoader();
        var target = OpenTarget(engine, AllModules);
        var result = await Import(directory, target, AllModules, new ImportOptions { BulkLoader = loader, BatchRows = 30 });

        Assert.Equal(exported.Rows, result.Rows);
        Assert.True((loader as PostgresCopyLoader)?.Batches > 10 || (loader as SqlServerBulkCopyLoader)?.Batches > 10, "the loader was used for the tables");
        await AssertSameAsManifest(target, exported.Manifest);
        using var reader = new PayloadStore(target, chunkSize: 1024).OpenRead(body);
        reader.CopyTo(Stream.Null);
        // identity keys were kept and counting on
        var runs = new RunStore(target, "dbdatasync");
        var run = runs.Read(new RunQuery(JobId: "orders-sync")).Single().Id;
        var max = runs.Logs(run, limit: 1000).Max(l => l.Id);
        Assert.True(runs.Log(run, "Info", "after the import") > max);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task The_target_keeps_the_names_its_own_layout_gives_not_the_sources(string engine)
    {
        var source = OpenSource(engine, SnapshotTestModules.Types);
        StateSnapshotWriterTests.FillTypes(source);
        var directory = NewDirectory();
        var exported = await Export(source, directory, [SnapshotTestModules.Types]);

        var target = OpenTarget(engine, [SnapshotTestModules.Types]);   // other schema and prefix
        await Import(directory, target, [SnapshotTestModules.Types]);

        var sourceName = source.Layout!.Table("snapshot_types", "sample");
        var targetName = target.Layout!.Table("snapshot_types", "sample");
        Assert.NotEqual(sourceName, targetName);
        Assert.Equal(sourceName, exported.Manifest.Modules[0].Tables.Single(t => t.Name == "sample").PhysicalName);
        Assert.Equal(2, Count(target, "snapshot_types", "sample"));
        // a database opened the usual way with that layout finds what was imported and has nothing to migrate
        var layout = target.Layout;
        Assert.Equal(["snapshot_types"], RegisteredModules(target));
        Assert.Equal(layout.Namespace("snapshot_types").Placement, RegisteredPlacement(target));
    }

    private static string RegisteredPlacement(StateDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(connection, $"SELECT schema_name FROM {database.Layout!.Registry} WHERE module = 'snapshot_types';");
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public async Task Times_with_more_precision_than_postgres_keeps_are_rounded_down_and_reported()
    {
        var source = OpenSource(StateEngineIds.Sqlite, SnapshotTestModules.Types);
        StateSnapshotWriterTests.Execute(source, "snapshot_types",
            "INSERT INTO {table:sample} (id, label, created_at, enabled, size) VALUES ($id, 'seven', $created, 1, 1);",
            c => c.Bind(source, "id", Guid.NewGuid()).Bind(source, "created", DateTimeOffset.Parse("2026-01-02T03:04:05.1234567Z")));
        var directory = NewDirectory();
        var exported = await Export(source, directory, [SnapshotTestModules.Types]);

        var target = OpenTarget(StateEngineIds.Postgres, [SnapshotTestModules.Types]);
        var result = await Import(directory, target, [SnapshotTestModules.Types]);

        var warning = Assert.Single(result.Warnings);
        Assert.Contains("snapshot_types.sample", warning);
        await AssertSameAsManifest(target, exported.Manifest, except: "snapshot_types.sample");
        using var connection = target.OpenConnection();
        using var command = target.Command(connection, "SELECT created_at FROM {table:snapshot_types.sample};");
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05.123456Z"), StateValues.ToTime(command.ExecuteScalar()!));
    }
}

/// <summary>Runs alone: it measures the live memory of the process.</summary>
[Collection("snapshot-memory")]
public sealed class StateSnapshotImportMemoryTests : IDisposable
{
    private const int Mebi = 1024 * 1024;
    private const int PayloadMebi = 200;

    private readonly StateEngines _engines = new();
    private readonly List<string> _directories = [];

    public void Dispose()
    {
        _engines.Dispose();
        foreach (var directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temp folder left behind is untidy and nothing else */ }
        }
    }

    private sealed class SyncProgress(Action<SnapshotProgress> report) : IProgress<SnapshotProgress>
    {
        public void Report(SnapshotProgress value) => report(value);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_two_hundred_mebibyte_payload_is_imported_holding_a_few_chunks_in_memory(string engine)
    {
        var target = _engines.Provision(engine);   // provisioned first: a skipped engine does no work
        var source = _engines.Provision(StateEngineIds.Sqlite).Open([PayloadStore.Module], StateScope.Standalone);
        var block = new byte[Mebi];
        new Random(11).NextBytes(block);
        using var expected = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Guid id;
        using (var writer = new PayloadStore(source).OpenWrite())
        {
            id = writer.Id;
            for (var i = 0; i < PayloadMebi; i++)
            {
                BitConverter.TryWriteBytes(block, i);
                expected.AppendData(block);
                writer.Write(block);
            }

            writer.Complete();
        }

        block = [];
        var directory = Path.Combine(Path.GetTempPath(), "dbdatakit-import-" + Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        var exported = await StateSnapshotWriter.WriteAsync(source, [PayloadStore.Module], StateScope.Standalone, directory, SnapshotOptions.Default, null, CancellationToken.None);
        var hash = Convert.ToHexStringLower(expected.GetHashAndReset());
        Assert.Equal(hash, Assert.Single(exported.Manifest.Payloads).Sha256);

        var database = StateTestOpen.ForImport(target, [PayloadStore.Module]);
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;
        var reports = 0;
        var progress = new SyncProgress(p =>
        {
            if (p.Phase != "payload") return;
            reports++;
            peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true) - baseline);
        });

        var result = await StateSnapshotImporter.ImportAsync(directory, database, [PayloadStore.Module], StateScope.Standalone, ImportOptions.Default, progress, CancellationToken.None);

        Assert.Equal((long)PayloadMebi * Mebi, result.PayloadBytes);
        Assert.True(reports >= 10, $"the import reported payload progress {reports} times for {PayloadMebi} MiB");
        Assert.True(peak < 32L * Mebi, $"live memory grew by {peak / (double)Mebi:F1} MiB while importing a {PayloadMebi} MiB payload");

        var store = new PayloadStore(database);
        using var reader = store.OpenRead(id);
        using var actual = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[Mebi];
        int read;
        while ((read = reader.Read(buffer)) > 0) actual.AppendData(buffer.AsSpan(0, read));
        Assert.Equal(hash, Convert.ToHexStringLower(actual.GetHashAndReset()));
        Assert.Equal(hash, store.Get(id)!.Sha256);
    }
}

/// <summary>Opens a scratch location as the target of an import.</summary>
internal static class StateTestOpen
{
    public static StateDatabase ForImport(StateEngines.Location location, StateModule[] modules)
    {
        var layout = StateLayout.Create(location.Engine, modules, StateScope.Standalone);
        return location.Engine == StateEngineIds.Sqlite ? StateDatabase.OpenSqliteForImport(location.Target, layout) : StateDatabase.OpenForImport(location.Engine, location.Target, layout);
    }
}
