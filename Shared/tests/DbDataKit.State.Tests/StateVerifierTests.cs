using System.Data;
using DbDataKit.State.Snapshots;
using DbDataKit.State.Verification;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29b slice X03: two live state databases compared by row count and digest of the logical values, on SQLite, PostgreSQL and SQL Server and across every pair of them.</summary>
public sealed class StateVerifierTests : IDisposable
{
    private readonly StateEngines _engines = new();

    private readonly List<string> _directories = [];

    public void Dispose()
    {
        _engines.Dispose();
        foreach (var directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
        }
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 7, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Run1 = Guid.Parse("00000000-0000-7000-8000-00000000000a");
    private static readonly Guid Run2 = Guid.Parse("00000000-0000-7000-8000-00000000000B");
    private static readonly Guid Run3 = Guid.Parse("FFFFFFFF-0000-7000-8000-000000000001");
    private static readonly Guid PayloadId = Guid.Parse("00000000-0000-7000-8000-0000000000c1");

    private static readonly StateModule[] AllModules =
    [
        IdentityModule.Module, RunsModule.Module, AuditModule.Module, NotificationsModule.Module,
        DefinitionsModule.Module, ProgressModule.Module, PayloadStore.Module,
    ];

    public static TheoryData<string, string> Pairs
    {
        get
        {
            var data = new TheoryData<string, string>();
            string[] engines = [StateEngineIds.Sqlite, StateEngineIds.Postgres, StateEngineIds.MsSql];
            foreach (var left in engines)
            {
                foreach (var right in engines) data.Add(left, right);
            }

            return data;
        }
    }

    private sealed record Json(string Text);

    private StateDatabase OpenPopulated(string engine, params StateModule[] modules)
    {
        var database = _engines.Provision(engine).Open(modules.Length == 0 ? AllModules : modules, StateScope.Standalone);
        if (modules.Length == 0) Populate(database);
        return database;
    }

    /// <summary>The same logical data on any engine: fixed ids and times, inserted in the same order (so identity keys agree), with values that engines store differently (mixed-case keys, a JSON document, a decimal, a blob).</summary>
    private static void Populate(StateDatabase db)
    {
        foreach (var (id, name, email, role, enabled) in new[] { ("Alice", "Alice A", "alice@x.test", "Admin", true), ("bob", "Bob", null, "Viewer", false), ("_zed", "Zed", null, "Viewer", true), ("Zoe", "Zoe", null, "Viewer", true) })
            Insert(db, "identity", "account", ("id", id), ("display_name", name), ("email", email), ("email_key", email), ("role", role), ("enabled", enabled), ("created_at", T0));
        Insert(db, "identity", "credential", ("id", "c1"), ("account_id", "Alice"), ("method", "passkey"), ("subject", "s1"), ("secret", "key"), ("label", "laptop"), ("created_at", T0));

        Insert(db, "runs", "run", ("id", Run1), ("service", "console"), ("job_id", "j1"), ("started_at", T0.AddTicks(1234560)), ("finished_at", T0.AddSeconds(5)), ("outcome", "Succeeded"), ("rows_moved", 10L));
        Insert(db, "runs", "run", ("id", Run2), ("service", "console"), ("job_id", "j2"), ("started_at", T0), ("outcome", "Running"));
        Insert(db, "runs", "run", ("id", Run3), ("service", "sync"), ("job_id", "j3"), ("started_at", T0), ("outcome", "Failed"), ("failure_kind", "Timeout"), ("failure_message", "waited 30 s"));
        for (var i = 1; i <= 5; i++)
            Insert(db, "runs", "run_log", ("run_id", Run1), ("logged_at", T0.AddSeconds(i)), ("level", "Info"), ("message", $"line {i} ünï¢ode"));

        Insert(db, "audit", "entry", ("service_id", "console"), ("happened_at", T0), ("actor_kind", "person"), ("actor_id", "Alice"), ("verb", "definition.publish"), ("outcome", "ok"), ("detail", new Json("""{"b": 1, "a": [1, 2.0], "c": {"y": true, "x": null}}""")));
        Insert(db, "audit", "entry", ("service_id", "console"), ("happened_at", T0.AddSeconds(1)), ("actor_kind", "system"), ("actor_id", "console"), ("verb", "retention.run"), ("outcome", "ok"));

        Insert(db, "notifications", "notification", ("service_id", "console"), ("kind", "RunFailed"), ("created_at", T0), ("message", "Run failed"), ("run_id", Run3));
        Insert(db, "notifications", "notification", ("service_id", "console"), ("kind", "Info"), ("created_at", T0), ("message", "hello"));
        Insert(db, "notifications", "reader_position", ("reader_id", "Alice"), ("last_seen_id", 1L), ("updated_at", T0));

        Insert(db, "definitions", "definition_version", ("kind", "app"), ("name", "sales"), ("version", 1L), ("hash", "abc"), ("content", "content: 1"), ("depends_on", new Json("""["model:orders@1", "model:items@2"]""")), ("published_by", "Alice"), ("published_at", T0));
        Insert(db, "definitions", "definition_serving", ("kind", "app"), ("name", "sales"), ("channel", "live"), ("version", 1L), ("switched_at", T0), ("switched_by", "Alice"));
        Insert(db, "definitions", "definition_event", ("action", "publish"), ("kind", "app"), ("name", "sales"), ("channel", "live"), ("version", 1L), ("actor_id", "Alice"), ("happened_at", T0));

        Insert(db, "runner_progress", "checkpoint_head", ("package", "p"), ("channel", "main"), ("task", "t"), ("name", "since"), ("item_key", ""), ("row_version", 1L), ("updated_at", T0));
        Insert(db, "runner_progress", "checkpoint_part", ("package", "p"), ("channel", "main"), ("task", "t"), ("name", "since"), ("item_key", ""), ("part", "value"), ("value_type", "decimal"),
            ("value_int", 5L), ("value_decimal", 1.5m), ("value_at", T0), ("value_bool", true), ("value_payload", PayloadId), ("value_json", new Json("""{"k": [3, 1, 2], "a": "b"}""")));

        if (db.Layout!.Namespaces.All(n => n.Module.Name != PayloadStore.ModuleName)) return;
        Insert(db, "payloads", "payload", ("id", PayloadId), ("size", 6L), ("sha256", "0123abcd"), ("media_type", "text/plain"), ("state", "complete"), ("chunk_size", 4L), ("created_at", T0), ("completed_at", T0));
        Insert(db, "payloads", "chunk", ("payload_id", PayloadId), ("sequence", 0L), ("data", new byte[] { 1, 2, 3, 4 }));
        Insert(db, "payloads", "chunk", ("payload_id", PayloadId), ("sequence", 1L), ("data", new byte[] { 5, 6 }));
    }

    private static void Insert(StateDatabase db, string module, string table, params (string Column, object? Value)[] values)
    {
        // a NULL is a column left out, so no engine has to guess its type
        var present = values.Where(v => v.Value is not null).ToArray();
        var names = string.Join(", ", present.Select(v => v.Column));
        var parameters = string.Join(", ", present.Select((v, i) => v.Value is Json ? $"CAST($p{i} AS {{{{json}}}})" : $"$p{i}"));
        using var connection = db.OpenConnection();
        using var command = db.Command(module, connection, $"INSERT INTO {{table:{table}}} ({names}) VALUES ({parameters});");
        for (var i = 0; i < present.Length; i++) command.Bind(db, $"p{i}", present[i].Value is Json json ? json.Text : present[i].Value);
        command.ExecuteNonQuery();
    }

    private static int Execute(StateDatabase db, string module, string sql, params (string Name, object Value)[] values)
    {
        using var connection = db.OpenConnection();
        using var command = db.Command(module, connection, sql);
        foreach (var (name, value) in values) command.Bind(db, name, value);
        return command.ExecuteNonQuery();
    }

    private static int TableCount(StateDatabase database) => database.Layout!.Namespaces.Sum(ns => ns.Tables.Count);

    [Theory]
    [MemberData(nameof(Pairs))]
    public void The_same_data_on_any_two_engines_compares_equal(string leftEngine, string rightEngine)
    {
        var left = OpenPopulated(leftEngine);
        var right = OpenPopulated(rightEngine);

        var report = new StateVerifier().Verify(left, right);

        Assert.True(report.Equal, report.Describe());
        Assert.Equal(TableCount(left), report.Tables.Count);
        Assert.Equal(report.Tables.Select(t => t.LeftRows), report.Tables.Select(t => t.RightRows));
        // the data really is there: identity, runs with logs, audit, notifications, definitions, progress and payloads
        foreach (var (module, table, rows) in new[] { ("identity", "account", 4L), ("runs", "run_log", 5L), ("audit", "entry", 2L), ("notifications", "notification", 2L), ("definitions/main", "definition_version", 1L), ("runner_progress/main", "checkpoint_part", 1L), ("payloads", "chunk", 2L) })
            Assert.Equal(rows, report.Tables.Single(t => t.Module == module && t.Table == table).LeftRows);
    }

    /// <summary>Everything but the payload tables, whose rows in a snapshot hold offsets into its payload files and so have no digest a database can reproduce.</summary>
    private static readonly StateModule[] WithoutPayloads = [.. AllModules.Where(m => m.Name != PayloadStore.ModuleName)];

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_database_verifies_against_the_manifest_of_its_own_snapshot_and_a_change_after_the_export_fails_naming_the_table(string engine)
    {
        var database = OpenPopulated(engine, WithoutPayloads);
        Populate(database);
        var directory = Path.Combine(Path.GetTempPath(), "dbdatakit-verify-" + Guid.NewGuid().ToString("N"));
        _directories.Add(directory);
        await StateSnapshotWriter.WriteAsync(database, WithoutPayloads, StateScope.Standalone, directory, SnapshotOptions.Default, null, CancellationToken.None);
        var manifest = new ManifestDigestSource(directory);

        // the exporter's digests and the verifier's are the same rule, so the live database equals what was exported
        // (the payload chunks are the one table a manifest cannot digest: the snapshot's rows hold offsets into its payload files, so it is named as not offered and checked through the payload hashes)
        Assert.Equal(["payloads.chunk"], manifest.NotOffered);
        var report = new StateVerifier().Verify(new DatabaseDigestSource(database), manifest);
        Assert.Equal(["payloads.chunk"], report.Differences.Where(d => d.Kind == VerificationDifferenceKind.MissingTable).Select(d => $"{d.Module}.{d.Table}"));
        Assert.Single(report.Differences);
        Assert.Equal(TableCount(database) - 1, report.Tables.Count);
        Assert.All(report.Tables, t => Assert.True(t.Equal, report.Describe()));
        Assert.Contains(report.Tables, t => t is { Module: "identity", Table: "account", LeftRows: 4 } && t.LeftSha256 == t.RightSha256);

        // a row changed after the export: a manifest cannot list rows, so the table is named with a digest mismatch
        Assert.Equal(1, Execute(database, "identity", "UPDATE {table:account} SET display_name = $name WHERE id = $id;", ("name", "Robert"), ("id", "bob")));
        var changed = new StateVerifier().Verify(new DatabaseDigestSource(database), manifest);
        var difference = Assert.Single(changed.Differences, d => d.Kind != VerificationDifferenceKind.MissingTable);
        Assert.Equal(VerificationDifferenceKind.DigestMismatch, difference.Kind);
        Assert.Equal(("identity", "account"), (difference.Module, difference.Table));
        Assert.Contains("identity.account", difference.Message);

        // and a row added after it as a count mismatch of that table
        Insert(database, "identity", "account", ("id", "new"), ("display_name", "New"), ("role", "Viewer"), ("enabled", true), ("created_at", T0));
        var added = new StateVerifier().Verify(new DatabaseDigestSource(database), manifest);
        Assert.Contains(added.Differences, d => d.Kind == VerificationDifferenceKind.CountMismatch && d is { Module: "identity", Table: "account" });
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_changed_row_is_found_and_named(string engine)
    {
        var left = OpenPopulated(engine);
        var right = OpenPopulated(engine);
        Assert.Equal(1, Execute(right, "identity", "UPDATE {table:account} SET display_name = $name WHERE id = $id;", ("name", "Robert"), ("id", "bob")));

        var report = new StateVerifier().Verify(left, right);

        var difference = Assert.Single(report.Differences);
        Assert.Equal(VerificationDifferenceKind.ChangedRow, difference.Kind);
        Assert.Equal("identity", difference.Module);
        Assert.Equal("account", difference.Table);
        Assert.Equal(["bob"], difference.Keys);
        Assert.Contains("identity.account", difference.Message);
        Assert.False(report.Tables.Single(t => t.Table == "account").Equal);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_missing_row_and_an_extra_row_are_found_and_named(string engine)
    {
        var left = OpenPopulated(engine);
        var right = OpenPopulated(engine);
        Assert.Equal(1, Execute(right, "runs", "DELETE FROM {table:run_log} WHERE id = $id;", ("id", 2L)));
        Insert(right, "identity", "account", ("id", "Yan"), ("display_name", "Yan"), ("role", "Viewer"), ("enabled", true), ("created_at", T0));

        var report = new StateVerifier().Verify(left, right);

        var missing = Assert.Single(report.Differences, d => d.Kind == VerificationDifferenceKind.MissingRow);
        Assert.Equal("run_log", missing.Table);
        Assert.Equal(["2"], missing.Keys);
        var count = Assert.Single(report.Differences, d => d.Kind == VerificationDifferenceKind.CountMismatch && d.Table == "run_log");
        Assert.Contains("5 rows", count.Message);
        Assert.Contains("4", count.Message);
        var extra = Assert.Single(report.Differences, d => d.Kind == VerificationDifferenceKind.ExtraRow);
        Assert.Equal("account", extra.Table);
        Assert.Equal(["Yan"], extra.Keys);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_changed_payload_byte_is_found_when_the_options_say_to_hash_the_bytes(string engine)
    {
        var left = OpenPopulated(engine);
        var right = OpenPopulated(engine);
        Assert.Equal(1, Execute(right, "payloads", "UPDATE {table:chunk} SET data = $data WHERE payload_id = $id AND sequence = $sequence;", ("data", new byte[] { 1, 2, 9, 4 }), ("id", PayloadId), ("sequence", 0L)));

        // chunks are compared by id, sequence and size unless asked: a byte flipped in place has the same size
        Assert.True(new StateVerifier().Verify(left, right).Equal);

        var report = new StateVerifier(new VerifyOptions { RehashPayloads = true }).Verify(left, right);

        var difference = Assert.Single(report.Differences);
        Assert.Equal(VerificationDifferenceKind.ChangedRow, difference.Kind);
        Assert.Equal("payloads", difference.Module);
        Assert.Equal("chunk", difference.Table);
        Assert.Equal([$"{PayloadId}/0"], difference.Keys);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_changed_stored_payload_hash_is_found_without_reading_the_bytes(string engine)
    {
        var left = OpenPopulated(engine);
        var right = OpenPopulated(engine);
        Execute(right, "payloads", "UPDATE {table:payload} SET sha256 = $hash WHERE id = $id;", ("hash", "ffff"), ("id", PayloadId));

        var difference = Assert.Single(new StateVerifier().Verify(left, right).Differences);

        Assert.Equal((VerificationDifferenceKind.ChangedRow, "payload", PayloadId.ToString()), (difference.Kind, difference.Table, difference.Keys![0]));
    }

    private static StateModule Things(int migrations) => new("things", StateScopeKind.Install,
        new[] { "CREATE TABLE {table:thing} (id {{key}} PRIMARY KEY, label {{text}} NULL);", "ALTER TABLE {table:thing} {{addcolumn}} note {{text}} NULL;", "ALTER TABLE {table:thing} {{addcolumn}} more {{text}} NULL;" }[..migrations]);

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_differing_module_version_is_named_and_its_tables_are_not_compared(string engine)
    {
        var left = _engines.Provision(engine).Open([Things(2), PayloadStore.Module], StateScope.Standalone);
        var right = _engines.Provision(engine).Open([Things(3), RunsModule.Module], StateScope.Standalone);

        var report = new StateVerifier().Verify(left, right);

        var version = Assert.Single(report.Differences, d => d.Kind == VerificationDifferenceKind.ModuleVersionMismatch);
        Assert.Equal("things", version.Module);
        Assert.Contains("version 2", version.Message);
        Assert.Contains("version 3", version.Message);
        Assert.Equal("payloads", Assert.Single(report.Differences, d => d.Kind == VerificationDifferenceKind.MissingModule).Module);
        Assert.Equal("runs", Assert.Single(report.Differences, d => d.Kind == VerificationDifferenceKind.ExtraModule).Module);
        Assert.DoesNotContain(report.Tables, t => t.Module == "things");
        Assert.False(report.Equal);
    }

    [Fact]
    public void The_logical_columns_of_every_module_are_known_with_a_key()
    {
        foreach (var module in AllModules.Concat([AppStorageModule.Module]))
        {
            foreach (var table in StateTableDefinitions.Read(module))
            {
                Assert.NotEmpty(table.PrimaryKey);
                Assert.NotEmpty(table.Columns);
            }
        }

        var chunk = StateTableDefinitions.Read(PayloadStore.Module).Single(t => t.Name == "chunk");
        Assert.Equal(new[] { SnapshotColumnTypes.Uuid, SnapshotColumnTypes.Int, SnapshotColumnTypes.Blob }, chunk.Columns.Select(c => c.Type));
        Assert.Equal(new[] { "payload_id", "sequence" }, chunk.PrimaryKey);
        var runLog = StateTableDefinitions.Read(RunsModule.Module).Single(t => t.Name == "run_log");
        Assert.Equal(SnapshotColumnTypes.Int, runLog.Columns.Single(c => c.Name == runLog.PrimaryKey.Single()).Type);
        Assert.Equal(SnapshotColumnTypes.Json, StateTableDefinitions.Read(AuditModule.Module).Single(t => t.Name == "entry").Columns.Last().Type);
    }

    [Fact]
    public void Neutral_values_agree_where_engines_store_the_same_thing_differently()
    {
        Assert.Equal("00000000-0000-7000-8000-00000000000b", SnapshotValues.Normalise(SnapshotColumnTypes.Uuid, "00000000-0000-7000-8000-00000000000B"));
        Assert.Equal(SnapshotValues.Normalise(SnapshotColumnTypes.Time, "2026-11-07T10:00:00.1234560Z"), SnapshotValues.Normalise(SnapshotColumnTypes.Time, new DateTime(2026, 11, 7, 10, 0, 0, 0, DateTimeKind.Utc).AddTicks(1234560)));
        Assert.Equal("2026-11-07T10:00:00.1234567Z", SnapshotValues.Normalise(SnapshotColumnTypes.Time, "2026-11-07T10:00:00.1234567Z"));
        Assert.Equal(SnapshotValues.Normalise(SnapshotColumnTypes.Decimal, "1.5"), SnapshotValues.Normalise(SnapshotColumnTypes.Decimal, 1.5000000000m));
        Assert.Equal("true", SnapshotValues.Normalise(SnapshotColumnTypes.Bool, 1L));
        Assert.Equal("""{"a":[1,2.0],"b":{"x":null,"y":true}}""", SnapshotValues.CanonicalJson("""{ "b": {"y": true, "x": null}, "a": [1, 2.0] }"""));
        Assert.Equal("not json", SnapshotValues.CanonicalJson("not json"));
    }

    [Fact]
    public void A_cancelled_verification_stops()
    {
        var left = OpenPopulated(StateEngineIds.Sqlite);
        var right = OpenPopulated(StateEngineIds.Sqlite);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();

        Assert.Throws<OperationCanceledException>(() => new StateVerifier().Verify(left, right, cancel.Token));
    }

    [Fact]
    public void Progress_names_each_table_and_counts_to_the_total()
    {
        var left = OpenPopulated(StateEngineIds.Sqlite);
        var right = OpenPopulated(StateEngineIds.Sqlite);
        var reports = new List<VerificationProgress>();

        new StateVerifier().Verify(left, right, progress: new SyncProgress<VerificationProgress>(reports.Add));

        var finished = reports.Where(p => p.Step == "done").ToList();
        Assert.Equal(TableCount(left), finished.Count);
        Assert.Equal(TableCount(left), finished[^1].TablesDone);
        Assert.All(finished, p => Assert.Equal(TableCount(left), p.TablesTotal));
        Assert.Contains(reports, p => p is { Module: "runs", Table: "run_log", Step: "left", Rows: 5 });
    }

    /// <summary>Reports on the calling thread, so a test reads them as they were made.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    /// <summary>Wraps a source to see how many rows each read returns.</summary>
    private sealed class Watching(DatabaseDigestSource inner, Action sample) : IRowDigestSource
    {
        public int MaxPage { get; private set; }

        public int Pages { get; private set; }

        public string Description => inner.Description;

        public IReadOnlyList<DigestModule> Modules() => inner.Modules();

        public TableDigest Digest(string moduleKey, string table, VerifyOptions options, CancellationToken cancellationToken, Action<long>? onRows = null)
        {
            long last = 0;
            return inner.Digest(moduleKey, table, options, cancellationToken, rows =>
            {
                MaxPage = Math.Max(MaxPage, (int)(rows - last));
                last = rows;
                Pages++;
                sample();
                onRows?.Invoke(rows);
            });
        }

        public IReadOnlyList<RowDigest> Rows(string moduleKey, string table, IReadOnlyList<object>? after, int take, VerifyOptions options, CancellationToken cancellationToken)
        {
            var rows = inner.Rows(moduleKey, table, after, take, options, cancellationToken);
            MaxPage = Math.Max(MaxPage, rows.Count);
            Pages++;
            sample();
            return rows;
        }
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_table_of_fifty_thousand_rows_is_compared_in_pages_with_bounded_memory(string engine)
    {
        const int rows = 50_000;
        var left = _engines.Provision(engine).Open([RunsModule.Module], StateScope.Standalone);
        var right = _engines.Provision(engine).Open([RunsModule.Module], StateScope.Standalone);
        var filler = new string('x', 1000);
        foreach (var database in new[] { left, right }) Fill(database, rows, filler);
        // the right one has one row changed and one gone, so the walk by key runs over the whole table too
        Execute(right, "runs", "UPDATE {table:run_log} SET message = $message WHERE id = $id;", ("message", "changed"), ("id", 25_000L));
        Execute(right, "runs", "DELETE FROM {table:run_log} WHERE id = $id;", ("id", 40_000L));

        GC.Collect();
        var baseline = GC.GetTotalMemory(forceFullCollection: true);
        long peak = 0;
        void Sample() { if (++_samples % 10 == 0) peak = Math.Max(peak, GC.GetTotalMemory(forceFullCollection: true)); }
        var watchLeft = new Watching(new DatabaseDigestSource(left), Sample);
        var watchRight = new Watching(new DatabaseDigestSource(right), Sample);

        var report = new StateVerifier(new VerifyOptions { BatchSize = 500 }).Verify(watchLeft, watchRight);

        Assert.Equal(["25000"], report.Differences.Single(d => d.Kind == VerificationDifferenceKind.ChangedRow).Keys);
        Assert.Equal(["40000"], report.Differences.Single(d => d.Kind == VerificationDifferenceKind.MissingRow).Keys);
        Assert.Equal(rows, report.Tables.Single(t => t.Table == "run_log").LeftRows);
        Assert.Equal(500, watchLeft.MaxPage);
        Assert.True(watchLeft.Pages >= rows / 500, $"read in {watchLeft.Pages} pages");
        // the table is 50,000 rows of a kilobyte each (100 MB as .NET text); two pages of it are about 1 MB
        Assert.True(peak - baseline < 25_000_000, $"memory grew by {(peak - baseline) / 1_000_000} MB while comparing");
    }

    private long _samples;

    private static void Fill(StateDatabase db, int rows, string message)
    {
        using var connection = db.OpenConnection();
        using var transaction = connection.BeginTransaction();
        for (var i = 0; i < rows; i++)
        {
            using var command = db.Command("runs", connection, transaction, "INSERT INTO {table:run_log} (run_id, logged_at, level, message) VALUES ($run, $at, $level, $message);");
            command.Bind(db, "run", Run1).Bind(db, "at", T0.AddSeconds(i)).Bind(db, "level", "Info").Bind(db, "message", message + i, DbType.String);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }
}
