using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.State.Snapshots;
using Microsoft.Data.Sqlite;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29b X01: the snapshot exporter, on SQLite, PostgreSQL and SQL Server.</summary>
public sealed partial class StateSnapshotWriterTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-07T09:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly List<string> _directories = [];
    private readonly StateTestClock _clock = new(Start);

    [GeneratedRegex(@"^\{\s*""format"": ""state-snapshot/1"",")]
    private static partial Regex FormatFirst { get; }

    public void Dispose()
    {
        _engines.Dispose();
        foreach (var directory in _directories)
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temp folder left behind is untidy and nothing else */ }
        }
    }

    /// <summary>A path that does not exist yet.</summary>
    private string NewDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "dbdatakit-snapshot-" + Guid.NewGuid().ToString("N"));
        _directories.Add(path);
        return path;
    }

    private StateDatabase Open(string engine, params StateModule[] modules) => _engines.Provision(engine).Open(modules, StateScope.Standalone, time: _clock);

    private static Task<SnapshotResult> Export(StateDatabase database, string directory, StateModule[]? modules = null, SnapshotOptions? options = null, IProgress<SnapshotProgress>? progress = null, CancellationToken ct = default) =>
        StateSnapshotWriter.WriteAsync(database, modules ?? [.. database.Layout!.Namespaces.Select(n => n.Module)], StateScope.Standalone, directory, options ?? SnapshotOptions.Default, progress, ct);

    private static long Count(StateDatabase database, string module, string table)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(connection, $"SELECT COUNT(*) FROM {{table:{module}.{table}}};");
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static List<string> ColumnsOf(StateDatabase database, string module, string table)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(connection, $"SELECT * FROM {{table:{module}.{table}}} WHERE 1 = 0;");
        using var reader = command.ExecuteReader();
        return [.. Enumerable.Range(0, reader.FieldCount).Select(reader.GetName)];
    }

    internal static void Execute(StateDatabase database, string module, string sql, Action<DbCommand>? bind = null)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(module, connection, sql);
        bind?.Invoke(command);
        command.ExecuteNonQuery();
    }

    private sealed class SyncProgress(Action<SnapshotProgress> report) : IProgress<SnapshotProgress>
    {
        public void Report(SnapshotProgress value) => report(value);
    }

    // ---------------------------------------------------------------- populated state

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task An_export_of_a_populated_state_has_manifest_counts_equal_to_the_tables(string engine)
    {
        var database = Open(engine, SnapshotPopulate.Modules);
        var body = SnapshotPopulate.Fill(database);
        var directory = NewDirectory();

        var result = await Export(database, directory);

        using var files = new SnapshotFiles(directory);
        Assert.Matches(FormatFirst, files.ManifestText);
        Assert.Equal("state-snapshot/1", files.Root.GetProperty("format").GetString());
        Assert.Equal(engine, files.Root.GetProperty("engine").GetString());
        Assert.Equal("DbDataKit.State", files.Root.GetProperty("tool").GetProperty("name").GetString());
        Assert.False(string.IsNullOrEmpty(files.Root.GetProperty("tool").GetProperty("version").GetString()));
        Assert.Equal(("main", "main"), (files.Root.GetProperty("scope").GetProperty("group").GetString(), files.Root.GetProperty("scope").GetProperty("service").GetString()));
        Assert.Equal(StateValues.FormatTime(_clock.GetUtcNow()), files.Root.GetProperty("created_at").GetString());

        // every module of the layout, in the layout's (dependency) order, every table of each
        var layout = database.Layout!;
        Assert.Equal(layout.Namespaces.Select(n => n.Module.Name), files.Modules.Select(m => m.GetProperty("name").GetString()));
        foreach (var module in files.Modules)
        {
            var ns = layout.Namespace(module.GetProperty("name").GetString()!);
            Assert.Equal(ns.Tables.Keys.Order(), module.GetProperty("tables").EnumerateArray().Select(t => t.GetProperty("name").GetString()!).Order());
            Assert.Equal(ns.Module.Version, module.GetProperty("version").GetInt32());
            Assert.Equal(ns.Module.Scope.ToString().ToLowerInvariant(), module.GetProperty("scope_kind").GetString());
            Assert.Equal(ns.ScopeName, module.GetProperty("scope_name").GetString());
            foreach (var dependency in ns.Module.DependsOn)
                Assert.True(files.Modules.ToList().FindIndex(m => m.GetProperty("name").GetString() == dependency.Name) < files.Modules.ToList().FindIndex(m => m.GetProperty("name").GetString() == ns.Module.Name));

            // the registry's version and checksum are the manifest's
            using var connection = database.OpenConnection();
            using var registry = database.Command(connection, $"SELECT version, checksum FROM {layout.Registry} WHERE module = $module;");
            registry.Bind(database, "module", ns.Module.Name);
            using var reader = registry.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(Convert.ToInt32(reader.GetValue(0), CultureInfo.InvariantCulture), module.GetProperty("version").GetInt32());
            Assert.Equal(reader.GetString(1), module.GetProperty("checksum").GetString());
        }

        var totalRows = 0L;
        foreach (var (moduleName, table) in files.Tables)
        {
            var name = table.GetProperty("name").GetString()!;
            var where = $"{moduleName}.{name}";
            var lines = files.Lines(moduleName, name);
            var rows = table.GetProperty("rows").GetInt64();
            totalRows += rows;
            Assert.Equal(Count(database, moduleName, name), rows);
            Assert.Equal(rows, lines.Count);
            Assert.Equal(layout.Table(moduleName, name), table.GetProperty("physical_name").GetString());
            Assert.Equal($"tables/{moduleName}.{name}.jsonl.gz", table.GetProperty("file").GetString());
            Assert.Equal(SnapshotFiles.Sha256Of(Path.Combine(directory, table.GetProperty("file").GetString()!)), table.GetProperty("sha256").GetString());
            Assert.Equal(SnapshotFiles.RowsDigestOf(lines), table.GetProperty("rows_digest").GetString());

            // the manifest's columns are the engine's columns, and every row has a value for each
            var columns = table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!).ToList();
            Assert.True(ColumnsOf(database, moduleName, name).Order().SequenceEqual(columns.Order()), $"{where}: the manifest's columns are not the table's");
            Assert.All(files.Rows(moduleName, name), row => Assert.Equal(columns.Count, row.Length));
            Assert.NotEmpty(table.GetProperty("primary_key").EnumerateArray());
        }

        Assert.Equal(totalRows, result.Rows);
        Assert.Equal(files.Tables.Count(), result.Tables);
        Assert.Equal(directory, result.Directory);
        Assert.Equal(files.Modules.Count(), result.Manifest.Modules.Count);

        // the row counts the populate made, read back from the files by their own columns
        Assert.Equal(2, files.Rows("identity", "account").Count);
        Assert.Equal(25, files.Rows("audit", "entry").Count);
        Assert.Equal(40, files.Rows("runs", "run_log").Count);
        Assert.Equal(2, files.Rows("runs", "run").Count);
        Assert.Equal(2, files.Rows("definitions", "definition_version").Count);
        Assert.Equal(2, files.Rows("notifications", "notification").Count);
        Assert.Equal(4, files.Rows("runner_progress", "checkpoint_history").Count);
        Assert.Single(files.Rows("runner_progress", "checkpoint_head"));
        Assert.Equal(3, files.Rows("payloads", "payload").Count);
        Assert.Equal(["Ada Lovelace", "Grace Hopper"], files.Rows("identity", "account").Select(r => r[ColumnIndex(files, "identity", "account", "display_name")].GetString()!).Order());
        var auditIds = files.Rows("audit", "entry").Select(r => r[0].GetInt64()).ToList();
        Assert.Equal(auditIds.Order(), auditIds);   // the identity key's order

        // payload chunk bytes are files, named by payload id, with their hashes in the manifest; the chunk rows say where each chunk is
        var chunkRows = files.Rows("payloads", "chunk");
        var chunkColumns = files.Table("payloads", "chunk").GetProperty("columns").EnumerateArray().ToList();
        Assert.Equal("payload-file", chunkColumns.Single(c => c.GetProperty("name").GetString() == "data").GetProperty("storage").GetString());
        var payloadEntries = files.Root.GetProperty("payloads").EnumerateArray().ToList();
        Assert.Equal(chunkRows.Select(r => r[0].GetString()).Distinct().Count(), payloadEntries.Count);
        foreach (var entry in payloadEntries)
        {
            var id = entry.GetProperty("id").GetString()!;
            var path = Path.Combine(directory, "payloads", id + ".bin");
            Assert.Equal($"payloads/{id}.bin", entry.GetProperty("file").GetString());
            Assert.Equal(new FileInfo(path).Length, entry.GetProperty("size").GetInt64());
            Assert.Equal(SnapshotFiles.Sha256Of(path), entry.GetProperty("sha256").GetString());
            var markers = chunkRows.Where(r => r[0].GetString() == id).Select(r => r[2]).ToList();
            Assert.Equal(entry.GetProperty("chunks").GetInt32(), markers.Count);
            Assert.Equal(entry.GetProperty("size").GetInt64(), markers.Sum(m => m.GetProperty("length").GetInt64()));
            Assert.Equal(markers.Select(m => m.GetProperty("offset").GetInt64()), markers.Select((_, i) => markers.Take(i).Sum(m => m.GetProperty("length").GetInt64())));
        }

        using (var payloadReader = new PayloadStore(database, chunkSize: 1024).OpenRead(body))
        using (var copy = new MemoryStream())
        {
            payloadReader.CopyTo(copy);
            Assert.Equal(copy.ToArray(), File.ReadAllBytes(Path.Combine(directory, "payloads", body.ToString("D") + ".bin")));
        }
    }

    private static int ColumnIndex(SnapshotFiles files, string module, string table, string column) =>
        files.Table(module, table).GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList().IndexOf(column);

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Exporting_twice_gives_the_same_digests_for_every_table_and_payload(string engine)
    {
        var database = Open(engine, SnapshotPopulate.Modules);
        SnapshotPopulate.Fill(database);

        await Export(database, NewDirectory());
        _clock.Advance(TimeSpan.FromHours(1));
        await Export(database, NewDirectory());

        using var first = new SnapshotFiles(_directories[^2]);
        using var second = new SnapshotFiles(_directories[^1]);
        Assert.NotEqual(first.Root.GetProperty("created_at").GetString(), second.Root.GetProperty("created_at").GetString());
        var a = first.Tables.Select(t => (t.Module, Name: t.Table.GetProperty("name").GetString(), Sha: t.Table.GetProperty("sha256").GetString(), Digest: t.Table.GetProperty("rows_digest").GetString())).ToList();
        var b = second.Tables.Select(t => (t.Module, Name: t.Table.GetProperty("name").GetString(), Sha: t.Table.GetProperty("sha256").GetString(), Digest: t.Table.GetProperty("rows_digest").GetString())).ToList();
        Assert.NotEmpty(a);
        Assert.Equal(a, b);
        Assert.Equal(first.Root.GetProperty("payloads").GetRawText(), second.Root.GetProperty("payloads").GetRawText());
    }

    // ---------------------------------------------------------------- logical types and neutral forms

    private static readonly Guid Id1 = Guid.Parse("00000000-0000-7000-8000-000000000001");
    private static readonly Guid Id2 = Guid.Parse("00000000-0000-7000-8000-000000000002");

    /// <summary>The sum of the line hashes of the two <c>sample</c> rows below, which every engine has to arrive at (pinned from the format's definition, not from any one engine).</summary>
    private const string SampleRowsDigest = "cddb5b37c36b15e5ec58d6be6e385da2ec8ba1f6154e12a242448c24ccf65663";

    internal static void FillTypes(StateDatabase database)
    {
        const string insert = "INSERT INTO {table:sample} (id, label, note, created_at, seen_at, enabled, amount, doc, data, size, extra) VALUES ($id, $label, $note, $created, $seen, $enabled, $amount, CAST($doc AS {{json}}), $data, $size, $extra);";
        Execute(database, "snapshot_types", insert, c => c
            .Bind(database, "id", Id1).Bind(database, "label", "alpha").Bind(database, "note", "multi\nline é ✓ \"quoted\"")
            .Bind(database, "created", DateTimeOffset.Parse("2026-01-02T03:04:05.123456Z")).Bind(database, "seen", null, System.Data.DbType.DateTimeOffset)
            .Bind(database, "enabled", true).Bind(database, "amount", 12.50m).Bind(database, "doc", "{\"b\": 1, \"a\": [1, {\"d\": 2, \"c\": 3.50}]}")
            .Bind(database, "data", new byte[] { 1, 2, 3 }).Bind(database, "size", 3L).Bind(database, "extra", null, System.Data.DbType.String));
        Execute(database, "snapshot_types", insert, c => c
            .Bind(database, "id", Id2).Bind(database, "label", "beta").Bind(database, "note", null, System.Data.DbType.String)
            .Bind(database, "created", DateTimeOffset.Parse("2026-02-03T04:05:06+01:00")).Bind(database, "seen", DateTimeOffset.Parse("2026-02-03T05:06:07.5Z"))
            .Bind(database, "enabled", false).Bind(database, "amount", -0.0050m).Bind(database, "doc", null, System.Data.DbType.String)
            .Bind(database, "data", Array.Empty<byte>()).Bind(database, "size", 0L).Bind(database, "extra", "x"));

        // a composite key, inserted out of order
        var order = Enumerable.Range(1, 10).SelectMany(a => Enumerable.Range(1, 10).Select(b => (a, b))).OrderBy(p => (p.a * 7 + p.b * 13) % 17).ThenBy(p => p.a).ToList();
        foreach (var (a, b) in order)
            Execute(database, "snapshot_types", "INSERT INTO {table:pair} (a, b, note) VALUES ($a, $b, $note);", c => c.Bind(database, "a", (long)a).Bind(database, "b", (long)b).Bind(database, "note", $"{a}/{b}"));
        foreach (var name in new[] { "one", "two", "three" })
            Execute(database, "snapshot_types", "INSERT INTO {table:counter} (name) VALUES ($name);", c => c.Bind(database, "name", name));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Every_logical_type_is_written_in_its_neutral_form_and_the_columns_are_typed_from_the_templates(string engine)
    {
        var database = Open(engine, SnapshotTestModules.Types);
        FillTypes(database);
        var directory = NewDirectory();

        await Export(database, directory);

        using var files = new SnapshotFiles(directory);
        var columns = files.Table("snapshot_types", "sample").GetProperty("columns").EnumerateArray().Select(c => (Name: c.GetProperty("name").GetString(), Type: c.GetProperty("type").GetString(), Nullable: c.GetProperty("nullable").GetBoolean())).ToList();
        Assert.Equal(
            [("id", "uuid", false), ("label", "text", false), ("note", "text", true), ("created_at", "time", false), ("seen_at", "time", true), ("enabled", "bool", false),
             ("amount", "decimal", true), ("doc", "json", true), ("data", "blob", true), ("size", "int", false), ("extra", "text", true)],
            columns);
        Assert.Equal(["id"], files.Table("snapshot_types", "sample").GetProperty("primary_key").EnumerateArray().Select(k => k.GetString()));
        Assert.Equal(["a", "b"], files.Table("snapshot_types", "pair").GetProperty("primary_key").EnumerateArray().Select(k => k.GetString()));
        var identity = files.Table("snapshot_types", "counter").GetProperty("columns")[0];
        Assert.Equal(("id", "int", true), (identity.GetProperty("name").GetString(), identity.GetProperty("type").GetString(), identity.GetProperty("identity").GetBoolean()));

        // the uuid key's order is the engine's, so the rows are found by label
        var rows = files.Rows("snapshot_types", "sample").ToDictionary(r => r[1].GetString()!);
        var alpha = rows["alpha"];
        Assert.Equal("00000000-0000-7000-8000-000000000001", alpha[0].GetString());
        Assert.Equal("multi\nline é ✓ \"quoted\"", alpha[2].GetString());
        Assert.Equal("2026-01-02T03:04:05.1234560Z", alpha[3].GetString());
        Assert.Equal(JsonValueKind.Null, alpha[4].ValueKind);
        Assert.Equal(JsonValueKind.True, alpha[5].ValueKind);
        Assert.Equal("12.5", alpha[6].GetString());
        Assert.Equal("{\"a\":[1,{\"c\":3.50,\"d\":2}],\"b\":1}", alpha[7].GetString());
        Assert.Equal("AQID", alpha[8].GetString());
        Assert.Equal(JsonValueKind.Number, alpha[9].ValueKind);
        Assert.Equal(3, alpha[9].GetInt64());
        Assert.Equal(JsonValueKind.Null, alpha[10].ValueKind);
        var beta = rows["beta"];
        Assert.Equal(JsonValueKind.Null, beta[2].ValueKind);
        Assert.Equal("2026-02-03T03:05:06.0000000Z", beta[3].GetString());   // UTC, fixed width
        Assert.Equal("2026-02-03T05:06:07.5000000Z", beta[4].GetString());
        Assert.Equal(JsonValueKind.False, beta[5].ValueKind);
        Assert.Equal("-0.005", beta[6].GetString());
        Assert.Equal(JsonValueKind.Null, beta[7].ValueKind);
        Assert.Equal("", beta[8].GetString());
        Assert.Equal("x", beta[10].GetString());

        // the key order of an integer key is the same everywhere
        Assert.Equal(Enumerable.Range(1, 10).SelectMany(a => Enumerable.Range(1, 10).Select(b => $"{a}/{b}")), files.Rows("snapshot_types", "pair").Select(r => r[2].GetString()));
        Assert.Equal([1L, 2L, 3L], files.Rows("snapshot_types", "counter").Select(r => r[0].GetInt64()));

        // and the digest of the rows is the one the format defines, whatever engine wrote them
        var table = files.Table("snapshot_types", "sample");
        Assert.Equal(SampleRowsDigest, table.GetProperty("rows_digest").GetString());
        Assert.Equal(SnapshotFiles.RowsDigestOf(files.Lines("snapshot_types", "sample")), table.GetProperty("rows_digest").GetString());
    }

    [Fact]
    public async Task The_same_logical_rows_have_the_same_table_digests_on_every_engine_that_is_available()
    {
        var digests = new Dictionary<string, Dictionary<string, (string Digest, string Sha)>>();
        foreach (var engine in new[] { StateEngineIds.Sqlite, StateEngineIds.MsSql, StateEngineIds.Postgres })
        {
            if (engine != StateEngineIds.Sqlite && StateEngines.Server(engine) is null) continue;
            var database = Open(engine, SnapshotTestModules.Types);
            FillTypes(database);
            var directory = NewDirectory();
            await Export(database, directory);
            using var files = new SnapshotFiles(directory);
            digests[engine] = files.Tables.ToDictionary(t => t.Table.GetProperty("name").GetString()!, t => (t.Table.GetProperty("rows_digest").GetString()!, t.Table.GetProperty("sha256").GetString()!));
        }

        var reference = digests[StateEngineIds.Sqlite];
        foreach (var (engine, tables) in digests)
        {
            Assert.Equal(reference.ToDictionary(t => t.Key, t => t.Value.Digest), tables.ToDictionary(t => t.Key, t => t.Value.Digest));
            // where the engines order a key alike (integers), even the files are the same bytes
            Assert.Equal(reference["pair"].Sha, tables["pair"].Sha);
            Assert.Equal(reference["counter"].Sha, tables["counter"].Sha);
            _ = engine;
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(1000)]
    public async Task Keyset_paging_reads_every_row_once_in_key_order_whatever_the_batch_size(int batch)
    {
        foreach (var engine in new[] { StateEngineIds.Sqlite, StateEngineIds.MsSql, StateEngineIds.Postgres })
        {
            if (engine != StateEngineIds.Sqlite && StateEngines.Server(engine) is null) continue;
            var database = Open(engine, SnapshotTestModules.Types);
            FillTypes(database);
            var directory = NewDirectory();

            var seen = new List<string>();
            await Export(database, directory, options: SnapshotOptions.Default with { BatchSize = batch }, progress: new SyncProgress(p => { if (p.Phase == "rows" && p.Table == "pair") seen.Add($"{p.Rows}"); }));

            using var files = new SnapshotFiles(directory);
            Assert.Equal(Enumerable.Range(1, 10).SelectMany(a => Enumerable.Range(1, 10).Select(b => $"{a}/{b}")), files.Rows("snapshot_types", "pair").Select(r => r[2].GetString()));
            Assert.Equal(100, files.Table("snapshot_types", "pair").GetProperty("rows").GetInt64());
            Assert.Equal((100 + batch - 1) / batch + (100 % batch == 0 ? 1 : 0), seen.Count);   // a batch each, and the empty one that ends a table that fills its last batch
        }
    }

    // ---------------------------------------------------------------- refusing

    private async Task<SnapshotRefusedException> Refused(StateDatabase database, string directory, SnapshotOptions? options = null) =>
        await Assert.ThrowsAsync<SnapshotRefusedException>(() => Export(database, directory, options: options));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_live_serving_instance_makes_the_export_refuse_naming_it_until_its_lease_runs_out(string engine)
    {
        var database = Open(engine, SnapshotTestModules.Serving, SnapshotTestModules.Types);
        Execute(database, "serving", "INSERT INTO {table:serving_instance} (instance_key, host, process_id, started_at, heartbeat_at) VALUES ($key, $host, $pid, $at, $at);",
            c => c.Bind(database, "key", "instance-abc").Bind(database, "host", "proxy-host-1").Bind(database, "pid", 4242L).Bind(database, "at", _clock.GetUtcNow()));
        var directory = NewDirectory();

        var refusal = await Refused(database, directory);

        Assert.Equal(SnapshotRefusal.InstanceInUse, refusal.Reason);
        Assert.Contains("instance-abc", refusal.Message);
        Assert.Contains("proxy-host-1", refusal.Message);
        Assert.Contains("4242", refusal.Message);
        Assert.Contains("serving_instance", refusal.Message);
        Assert.False(Directory.Exists(directory));   // nothing was written

        // forced, it is exported (and the row with it)
        var forced = await Export(database, directory, options: SnapshotOptions.Default with { ExportEvenIfInUse = true });
        Assert.Equal(1, forced.Manifest.Modules.Single(m => m.Name == "serving").Tables.Single().Rows);

        // a row whose heartbeat is older than the lease is a crashed instance's, and does not stop an export
        _clock.Advance(TimeSpan.FromSeconds(31));
        var stale = NewDirectory();
        await Export(database, stale);
        Assert.True(File.Exists(Path.Combine(stale, "manifest.json")));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_runner_instance_inside_its_lease_makes_the_export_refuse_naming_it(string engine)
    {
        var database = Open(engine, SnapshotTestModules.Runner);
        Execute(database, "runner", "INSERT INTO {table:instance} (id, host, lease_expires_at) VALUES ($id, $host, $until);",
            c => c.Bind(database, "id", "runner-7").Bind(database, "host", "build-box").Bind(database, "until", _clock.GetUtcNow().AddMinutes(2)));

        var refusal = await Refused(database, NewDirectory());

        Assert.Equal(SnapshotRefusal.InstanceInUse, refusal.Reason);
        Assert.Contains("runner-7", refusal.Message);
        Assert.Contains("build-box", refusal.Message);

        _clock.Advance(TimeSpan.FromMinutes(3));
        await Export(database, NewDirectory());
    }

    [Fact]
    public async Task A_sqlite_file_another_process_holds_makes_the_export_refuse_naming_the_file()
    {
        var location = _engines.Provision(StateEngineIds.Sqlite);
        var database = location.Open([SnapshotTestModules.Types], StateScope.Standalone, time: _clock);
        using var holder = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = location.Target, Pooling = false }.ToString());
        holder.Open();
        using (var begin = holder.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE;";
            begin.ExecuteNonQuery();
        }

        var refusal = await Refused(database, NewDirectory());

        Assert.Equal(SnapshotRefusal.FileHeld, refusal.Reason);
        Assert.Contains(location.Target, refusal.Message);
        Assert.Contains("another process", refusal.Message);

        // forced, a file that is only being written to by somebody else is still read
        await Export(database, NewDirectory(), options: SnapshotOptions.Default with { ExportEvenIfInUse = true });

        using (var rollback = holder.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
        }

        await Export(database, NewDirectory());
    }

    [Fact]
    public async Task The_directory_must_be_empty_or_absent_and_a_refusal_names_it()
    {
        var database = Open(StateEngineIds.Sqlite, SnapshotTestModules.Types);
        FillTypes(database);
        var directory = NewDirectory();
        Directory.CreateDirectory(directory);
        var existing = Path.Combine(directory, "notes.txt");
        File.WriteAllText(existing, "keep me");

        var refusal = await Refused(database, directory);

        Assert.Equal(SnapshotRefusal.DirectoryNotEmpty, refusal.Reason);
        Assert.Contains(directory, refusal.Message);
        Assert.Equal("keep me", File.ReadAllText(existing));
        Assert.Equal(["notes.txt"], Directory.EnumerateFileSystemEntries(directory).Select(Path.GetFileName));

        // an empty directory is fine, and so is a path several levels below one that does not exist
        File.Delete(existing);
        await Export(database, directory);
        Assert.True(File.Exists(Path.Combine(directory, "manifest.json")));
        var nested = Path.Combine(NewDirectory(), "a", "b");
        await Export(database, nested);
        Assert.True(File.Exists(Path.Combine(nested, "manifest.json")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_cancelled_export_removes_what_it_wrote(bool directoryExisted)
    {
        var database = Open(StateEngineIds.Sqlite, SnapshotPopulate.Modules);
        SnapshotPopulate.Fill(database);
        var directory = NewDirectory();
        if (directoryExisted) Directory.CreateDirectory(directory);
        using var cts = new CancellationTokenSource();
        var filesWhenCancelled = 0;

        // cancelled once the audit table is done, with the identity and notification files already on disk
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Export(database, directory, progress: new SyncProgress(p =>
        {
            if (p is { Phase: "table-done", Module: "audit" })
            {
                filesWhenCancelled = Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Count();
                cts.Cancel();
            }
        }), ct: cts.Token));

        Assert.True(filesWhenCancelled >= 3, "the test cancelled before anything was written");
        if (directoryExisted)
        {
            Assert.True(Directory.Exists(directory));
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory));
        }
        else
            Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public async Task A_module_the_database_does_not_have_is_refused_by_name_and_an_older_structure_is_not_exported()
    {
        var database = Open(StateEngineIds.Sqlite, SnapshotTestModules.Types);
        var refusal = await Assert.ThrowsAsync<SnapshotRefusedException>(() => Export(database, NewDirectory(), modules: [RunsModule.Module]));
        Assert.Equal(SnapshotRefusal.UnknownModule, refusal.Reason);
        Assert.Contains("runs", refusal.Message);

        var old = _engines.Open(StateEngineIds.Sqlite);
        var unsupported = await Assert.ThrowsAsync<SnapshotRefusedException>(() => StateSnapshotWriter.WriteAsync(old, [], StateScope.Standalone, NewDirectory(), SnapshotOptions.Default, null, CancellationToken.None));
        Assert.Equal(SnapshotRefusal.Unsupported, unsupported.Reason);
    }

    [Fact]
    public async Task A_module_comes_with_the_modules_it_depends_on_and_only_those()
    {
        var database = Open(StateEngineIds.Sqlite, SnapshotPopulate.Modules);
        var directory = NewDirectory();

        var result = await Export(database, directory, modules: [ProgressModule.Module]);

        Assert.Equal(["payloads", "runner_progress"], result.Manifest.Modules.Select(m => m.Name));
    }

    // ---------------------------------------------------------------- secrets

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task A_secret_is_in_the_snapshot_only_as_the_ciphertext_that_was_stored(string engine)
    {
        var database = Open(engine, SnapshotTestModules.Trust);
        const string plain = "correct-horse-battery-staple-9f3a";
        var key = RandomNumberGenerator.GetBytes(32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[Encoding.UTF8.GetByteCount(plain)];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, Encoding.UTF8.GetBytes(plain), cipher, tag);
        var stored = Convert.ToBase64String([.. nonce, .. cipher, .. tag]);
        Execute(database, "trust", "INSERT INTO {table:group_secret} (name, value) VALUES ($name, $value);", c => c.Bind(database, "name", "dbdata:proxy:managed-build-1:warehouse").Bind(database, "value", stored));
        var directory = NewDirectory();

        await Export(database, directory);

        using var files = new SnapshotFiles(directory);
        var row = Assert.Single(files.Rows("trust", "group_secret"));
        Assert.Equal(stored, row[1].GetString());   // the stored bytes, untouched
        var everything = files.EverythingAsText();
        Assert.Contains(stored, everything);
        Assert.DoesNotContain(plain, everything);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.UTF8.GetBytes(plain)), everything);
        Assert.DoesNotContain(Convert.ToHexStringLower(Encoding.UTF8.GetBytes(plain)), everything);
    }
}
