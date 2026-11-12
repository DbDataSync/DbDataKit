using DbDataKit.State.Engines.Snapshots;
using DbDataKit.State.Snapshots;
using DbDataKit.State.Verification;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29b X04a: the snapshot service (export, import, the three forms of verify) and the shipped bulk loaders, on SQLite, PostgreSQL and SQL Server.</summary>
public sealed class StateSnapshotServiceTests : IDisposable
{
    private readonly ServiceScratch _scratch = new();
    private readonly StateSnapshotService _service;
    private static readonly StateModule[] Modules = ServiceData.Modules;
    private static readonly StateScope Scope = StateScope.Standalone;

    public StateSnapshotServiceTests() => _service = new StateSnapshotService(_scratch.Resolver);

    public void Dispose() => _scratch.Dispose();

    private string NewDirectory() => Path.Combine(_scratch.Directory, "snap-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>A populated database on the engine, opened the way a product opens it (migrating).</summary>
    private (StateEndpoint Endpoint, Guid Payload, byte[] Bytes) Seed(string engine, params StateModule[] extra)
    {
        var endpoint = _scratch.Provision(engine);
        var database = StateOpener.Open(endpoint.Settings, endpoint.SqliteFile!, [.. Modules, .. extra], Scope, _scratch.Resolver);
        var (payload, bytes) = ServiceData.Fill(database);
        return (endpoint, payload, bytes);
    }

    private StateDatabase OpenRaw(StateEndpoint endpoint) => StateOpener.OpenWithoutMigrating(endpoint.Settings, endpoint.SqliteFile, Modules, Scope, _scratch.Resolver);

    private static void Run(StateDatabase database, string module, string sql, Action<System.Data.Common.DbCommand>? bind = null)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(module, connection, sql);
        bind?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private Task<StateExportResult> Export(StateEndpoint source, string directory) =>
        _service.ExportAsync(source, Modules, Scope, directory, null, null, CancellationToken.None);

    private Task<StateImportResult> Import(string directory, StateEndpoint target, ImportOptions? options = null) =>
        _service.ImportAsync(directory, target, Modules, Scope, options, null, CancellationToken.None);

    private static void Flip(string path)
    {
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0x01;
        File.WriteAllBytes(path, bytes);
    }

    // ---- bulk loaders

    [Theory]
    [MemberData(nameof(ServiceScratch.Engines), MemberType = typeof(ServiceScratch))]
    public async Task The_factory_gives_each_engine_its_loader_and_sqlite_none(string engine)
    {
        var loader = SnapshotBulkLoaders.For(ServiceScratch.Id(engine));
        await Task.CompletedTask;
        Assert.Equal(engine == "sqlite", loader is null);
        if (engine == "postgres") Assert.IsType<PostgresCopyBulkLoader>(loader);
        if (engine == "mssql") Assert.IsType<SqlServerBulkCopyLoader>(loader);
    }

    [Theory]
    [MemberData(nameof(ServiceScratch.Engines), MemberType = typeof(ServiceScratch))]
    public async Task The_importer_with_the_bulk_loader_gives_the_same_digests_as_with_plain_inserts(string engine)
    {
        var (source, _, _) = Seed("sqlite");
        var directory = NewDirectory();
        var exported = await Export(source, directory);

        // plain: the importer called directly takes the batched INSERT
        var plainEndpoint = _scratch.Provision(engine);
        var plainTarget = StateOpener.OpenWithoutMigrating(plainEndpoint.Settings, plainEndpoint.SqliteFile, Modules, Scope, _scratch.Resolver);
        var plain = await StateSnapshotImporter.ImportAsync(directory, plainTarget, Modules, Scope, ImportOptions.Default, null, CancellationToken.None);
        Assert.Null(ImportOptions.Default.BulkLoader);

        // bulk: through the service, which selects the engine's loader; the loader's own counters say it was used
        var bulkEndpoint = _scratch.Provision(engine);
        var bulk = await Import(directory, bulkEndpoint);

        Assert.Equal(plain.Rows, bulk.Import.Rows);
        if (engine == "sqlite")
        {
            Assert.Equal(0, bulk.BulkBatches);
        }
        else
        {
            Assert.True(bulk.BulkBatches >= 5, $"the {engine} loader took {bulk.BulkBatches} batches");
            Assert.True(bulk.BulkRows >= 50, $"the {engine} loader took {bulk.BulkRows} rows");
            Assert.Contains("bulk path", bulk.Summary);
        }

        // the same per-table counts and digests: both against each other and, each, against the manifest
        var same = await _service.VerifyAsync(plainEndpoint, bulkEndpoint, Modules, Scope, null, null, CancellationToken.None);
        Assert.True(same.Equal, same.Summary);
        foreach (var (endpoint, result) in new[] { (plainEndpoint, plain), (bulkEndpoint, bulk.Import) })
        {
            var against = await _service.VerifyAsync(endpoint, directory, Modules, Scope, new StateVerifyOptions { RehashPayloads = true, RoundedTables = result.RoundedTables }, null, CancellationToken.None);
            Assert.True(against.Equal, against.Summary);
        }

        Assert.Equal(exported.Snapshot.Rows, bulk.Import.Rows);
    }

    // ---- export, import, verify

    [Theory]
    [MemberData(nameof(ServiceScratch.Pairs), MemberType = typeof(ServiceScratch))]
    public async Task A_snapshot_goes_from_any_engine_into_any_engine_and_all_three_verifications_agree(string from, string to)
    {
        var (source, payload, bytes) = Seed(from);
        var directory = NewDirectory();

        var exported = await Export(source, directory);
        Assert.Contains("Exported", exported.Summary);
        Assert.True(exported.Snapshot.Rows > 50, exported.Snapshot.Rows.ToString());

        var target = _scratch.Provision(to);
        var imported = await Import(directory, target);
        Assert.Contains("Imported", imported.Summary);
        Assert.Equal(exported.Snapshot.Rows, imported.Import.Rows);

        var verify = new StateVerifyOptions { RehashPayloads = true, RoundedTables = imported.Import.RoundedTables };
        var selfCheck = await _service.VerifyAsync(directory, null, CancellationToken.None);
        Assert.True(selfCheck.Equal, selfCheck.Summary);
        Assert.Equal("snapshot", selfCheck.Form);

        var againstSnapshot = await _service.VerifyAsync(target, directory, Modules, Scope, verify, null, CancellationToken.None);
        Assert.True(againstSnapshot.Equal, againstSnapshot.Summary);
        Assert.Contains(againstSnapshot.Notes, n => n.StartsWith("payloads.chunk", StringComparison.Ordinal));
        Assert.Contains(againstSnapshot.Notes, n => n.Contains("hashed from the database", StringComparison.Ordinal));

        var databases = await _service.VerifyAsync(source, target, Modules, Scope, verify, null, CancellationToken.None);
        Assert.True(databases.Equal, databases.Summary);

        // the product opens the target and the stores read what was there
        var database = StateOpener.Open(target.Settings, target.SqliteFile!, Modules, Scope, _scratch.Resolver);
        Assert.Equal(2, new IdentityUserStore(database).List().Count);
        using var stream = new PayloadStore(database).OpenRead(payload);
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        Assert.Equal(bytes, copy.ToArray());
    }

    [Theory]
    [MemberData(nameof(ServiceScratch.Engines), MemberType = typeof(ServiceScratch))]
    public async Task A_changed_row_is_found_in_both_database_forms_and_names_the_table(string engine)
    {
        var (source, _, _) = Seed(engine);
        var directory = NewDirectory();
        await Export(source, directory);
        var target = _scratch.Provision(engine);
        var imported = await Import(directory, target);
        Run(OpenRaw(target), "svc_types", "UPDATE {table:sample} SET label = 'tampered' WHERE size = 1;");

        var verify = new StateVerifyOptions { RoundedTables = imported.Import.RoundedTables };
        var databases = await _service.VerifyAsync(source, target, Modules, Scope, verify, null, CancellationToken.None);
        Assert.False(databases.Equal);
        Assert.Contains(databases.Differences, d => d.Contains("svc_types.sample", StringComparison.Ordinal));

        var againstSnapshot = await _service.VerifyAsync(target, directory, Modules, Scope, verify, null, CancellationToken.None);
        Assert.False(againstSnapshot.Equal);
        Assert.Contains(againstSnapshot.Differences, d => d.Contains("svc_types.sample", StringComparison.Ordinal));
        Assert.StartsWith("NOT EQUAL", againstSnapshot.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ServiceScratch.Engines), MemberType = typeof(ServiceScratch))]
    public async Task A_changed_payload_is_found_by_its_stored_hash_and_by_hashing_its_bytes(string engine)
    {
        var (source, payload, _) = Seed(engine);
        var directory = NewDirectory();
        await Export(source, directory);
        var target = _scratch.Provision(engine);
        var imported = await Import(directory, target);
        var verify = new StateVerifyOptions { RehashPayloads = true, RoundedTables = imported.Import.RoundedTables };

        // a chunk's bytes replaced by others of the same size: only hashing the bytes sees it
        Run(OpenRaw(target), "payloads", "UPDATE {table:chunk} SET data = $data WHERE payload_id = $id AND sequence = 0;", c => c.Bind(OpenRaw(target), "data", new byte[1024], System.Data.DbType.Binary).Bind(OpenRaw(target), "id", payload));
        var byBytes = await _service.VerifyAsync(target, directory, Modules, Scope, verify, null, CancellationToken.None);
        Assert.False(byBytes.Equal);
        Assert.Contains(byBytes.Differences, d => d.Contains(payload.ToString("D"), StringComparison.Ordinal));
        var withoutBytes = await _service.VerifyAsync(target, directory, Modules, Scope, new StateVerifyOptions { RoundedTables = imported.Import.RoundedTables }, null, CancellationToken.None);
        Assert.True(withoutBytes.Equal, "without hashing the bytes the stored hash still matches");

        // the stored hash of the payload row changed
        Run(OpenRaw(target), "payloads", "UPDATE {table:payload} SET sha256 = 'ab' WHERE id = $id;", c => c.Bind(OpenRaw(target), "id", payload));
        var byHash = await _service.VerifyAsync(target, directory, Modules, Scope, new StateVerifyOptions { RoundedTables = imported.Import.RoundedTables }, null, CancellationToken.None);
        Assert.False(byHash.Equal);
        Assert.Contains(byHash.Differences, d => d.Contains("stored SHA-256", StringComparison.Ordinal) || d.Contains("payloads.payload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_damaged_snapshot_names_each_damaged_file_without_a_database()
    {
        var (source, payload, _) = Seed("sqlite");
        var directory = NewDirectory();
        await Export(source, directory);
        Assert.True((await _service.VerifyAsync(directory, null, CancellationToken.None)).Equal);

        var payloadFile = Path.Combine(directory, "payloads", $"{payload:D}.bin");
        var tableFile = Directory.EnumerateFiles(Path.Combine(directory, "tables"), "svc_types.sample.*").Single();
        Flip(payloadFile);
        Flip(tableFile);

        var damaged = await _service.VerifyAsync(directory, null, CancellationToken.None);
        Assert.False(damaged.Equal);
        Assert.Contains(damaged.Differences, d => d.Contains($"{payload:D}.bin", StringComparison.Ordinal));
        Assert.Contains(damaged.Differences, d => d.Contains("svc_types.sample", StringComparison.Ordinal));

        // the database form names the payload file too
        var against = await _service.VerifyAsync(source, directory, Modules, Scope, null, null, CancellationToken.None);
        Assert.Contains(against.Differences, d => d.Contains($"{payload:D}.bin", StringComparison.Ordinal));

        File.Delete(Path.Combine(directory, "manifest.json"));
        await Assert.ThrowsAsync<SnapshotImportException>(() => _service.VerifyAsync(directory, null, CancellationToken.None));
    }

    // ---- refusals, with the fix named

    [Fact]
    public async Task Refusals_are_typed_and_name_the_fix()
    {
        // no SQLite path, and a SQLite file that is not there
        var noPath = await Assert.ThrowsAsync<StateServiceException>(() => Export(new StateEndpoint(new StateSettings(StateEngineIds.Sqlite)), NewDirectory()));
        Assert.Equal(StateServiceRefusal.SqlitePathMissing, noPath.Reason);
        var missing = await Assert.ThrowsAsync<StateServiceException>(() => Export(new StateEndpoint(new StateSettings(StateEngineIds.Sqlite), Path.Combine(_scratch.Directory, "nothing.db")), NewDirectory()));
        Assert.Equal(StateServiceRefusal.SourceMissing, missing.Reason);
        Assert.Contains("nothing.db", missing.Message);

        // an empty database is not a state database
        var empty = _scratch.Provision("sqlite");
        StateOpener.OpenWithoutMigrating(empty.Settings, empty.SqliteFile, Modules, Scope);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        await using (var create = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={empty.SqliteFile};Pooling=False"))
        {
            await create.OpenAsync();
            await using var command = create.CreateCommand();
            command.CommandText = "CREATE TABLE unrelated (x INTEGER);";
            await command.ExecuteNonQueryAsync();
        }

        var notState = await Assert.ThrowsAsync<StateServiceException>(() => Export(empty, NewDirectory()));
        Assert.Equal(StateServiceRefusal.NotAStateDatabase, notState.Reason);

        // a module the software has and the database lacks, and one the database has newer
        var (source, _, _) = Seed("sqlite");
        var needs = await Assert.ThrowsAsync<StateServiceException>(() => _service.ExportAsync(source, [.. Modules, ServiceData.Runner], Scope, NewDirectory(), null, null, CancellationToken.None));
        Assert.Equal(StateServiceRefusal.NeedsMigration, needs.Reason);
        Assert.Contains("runner", needs.Message);
        var database = OpenRaw(source);
        Run(database, "payloads", "SELECT 1;");
        using (var connection = database.OpenConnection())
        {
            using var bump = connection.CreateCommand();
            bump.CommandText = $"UPDATE {database.Layout!.Registry} SET version = version + 1 WHERE module = 'payloads';";
            bump.ExecuteNonQuery();
        }

        var newer = await Assert.ThrowsAsync<StateServiceException>(() => Export(source, NewDirectory()));
        Assert.Equal(StateServiceRefusal.DatabaseNewer, newer.Reason);
        Assert.Contains("payloads", newer.Message);
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mssql")]
    public async Task A_wrong_password_or_an_unknown_secret_is_named_and_the_value_never_appears(string engine)
    {
        var wrong = _scratch.Provision(engine, passwordOverride: "Wrong_Passw0rd_Not_The_Real_One");
        var failure = await Assert.ThrowsAsync<StateServiceException>(() => Export(wrong, NewDirectory()));
        Assert.Equal(StateServiceRefusal.ConnectionFailed, failure.Reason);
        Assert.DoesNotContain("Wrong_Passw0rd_Not_The_Real_One", failure.ToString(), StringComparison.Ordinal);
        foreach (var password in _scratch.Passwords) Assert.DoesNotContain(password, failure.ToString(), StringComparison.Ordinal);

        var unknown = new StateEndpoint(new StateSettings(engine, (engine == "mssql" ? "Server=127.0.0.1,1;User Id=sa;" : "Host=127.0.0.1;Username=u;") + "Password=secret:not-in-any-provider"));
        var secret = await Assert.ThrowsAsync<StateServiceException>(() => Export(unknown, NewDirectory()));
        Assert.Equal(StateServiceRefusal.SecretMissing, secret.Reason);
        Assert.Contains("not-in-any-provider", secret.Message);
    }

    [Theory]
    [MemberData(nameof(ServiceScratch.Engines), MemberType = typeof(ServiceScratch))]
    public async Task A_target_that_holds_modules_is_refused_naming_one_and_a_live_instance_stops_the_export(string engine)
    {
        var (source, _, _) = Seed(engine);
        var directory = NewDirectory();
        await Export(source, directory);

        var occupied = await Assert.ThrowsAsync<StateServiceException>(() => Import(directory, source));
        Assert.Equal(StateServiceRefusal.TargetNotEmpty, occupied.Reason);
        Assert.Contains("identity", occupied.Message);

        // a runner instance row inside its lease
        var (busy, _, _) = Seed(engine, ServiceData.Runner);
        var live = StateOpener.Open(busy.Settings, busy.SqliteFile!, [.. Modules, ServiceData.Runner], Scope, _scratch.Resolver);
        Run(live, "runner", "INSERT INTO {table:instance} (id, host, lease_expires_at) VALUES ($id, $host, $lease);", c => c.Bind(live, "id", "worker-1").Bind(live, "host", "host-a").Bind(live, "lease", DateTimeOffset.UtcNow.AddMinutes(10)));
        var refused = await Assert.ThrowsAsync<SnapshotRefusedException>(() => _service.ExportAsync(busy, [.. Modules, ServiceData.Runner], Scope, NewDirectory(), null, null, CancellationToken.None));
        Assert.Equal(SnapshotRefusal.InstanceInUse, refused.Reason);
        Assert.Contains("worker-1", refused.Message);
    }
}
