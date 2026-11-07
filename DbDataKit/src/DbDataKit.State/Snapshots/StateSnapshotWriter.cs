using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace DbDataKit.State.Snapshots;

/// <summary>
/// Writes a state database as a snapshot (plan 29b, decision M2; the format is <see cref="SnapshotFormat"/>). The modules are exported in dependency order, each table in primary-key order by keyset
/// paging (<c>WHERE key &gt; last ORDER BY key</c>, never <c>OFFSET</c>) in batches of <see cref="SnapshotOptions.BatchSize"/>, streamed through gzip to its file: a table, a row set or a payload is never
/// in memory, only the row being written (and the chunk being copied to its payload file). The registry is not exported; the manifest's module list is its content.
/// <para>
/// Where tables are is the database's own layout (schemas, prefixes, SQLite files), so a snapshot is the same for any placement. What each column is comes from the module's migration templates
/// (<see cref="StateTableDefinitions"/>). Rows of the payloads module's <c>chunk</c> table have their bytes written to <c>payloads/&lt;id&gt;.bin</c> (chunks in sequence, one file per payload) and a marker
/// object in the row; the file's size and hash are in the manifest. Secrets are copied as stored, ciphertext as ciphertext (M6): the writer never decrypts anything.
/// </para>
/// <para>
/// Refuses (<see cref="SnapshotRefusedException"/>) a directory that is not empty, a live instance of the database and, on SQLite, a file another process holds, unless
/// <see cref="SnapshotOptions.ExportEvenIfInUse"/> is set. A failed or cancelled export removes what it wrote.
/// </para>
/// </summary>
public static class StateSnapshotWriter
{
    internal static readonly JsonWriterOptions RowOptions = SnapshotValues.RowOptions;

    private static readonly JsonWriterOptions ManifestOptions = new() { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <param name="source">A database built from modules, opened for the process that is exporting.</param>
    /// <param name="modules">What to export; the modules they depend on come with them. Each has to be in the database's layout.</param>
    /// <param name="scope">The group and service the exporting process is, recorded in the manifest.</param>
    /// <param name="directory">Empty or absent.</param>
    public static async Task<SnapshotResult> WriteAsync(
        StateDatabase source, IReadOnlyList<StateModule> modules, StateScope scope, string directory, SnapshotOptions options, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        options ??= SnapshotOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchSize, 1);
        var layout = source.Layout ?? throw new SnapshotRefusedException(SnapshotRefusal.Unsupported, "A snapshot is made of a database built from modules; this one was built from a StateSchema (the structure being replaced).");

        var started = source.Time.GetTimestamp();
        var namespaces = Select(layout, modules);
        var definitions = namespaces.ToDictionary(ns => ns.Module.Name, StateTableDefinitions.ReadFor, StringComparer.Ordinal);

        if (!options.ExportEvenIfInUse)
        {
            var instances = await SnapshotUseCheck.FindInstancesAsync(source, options, ct).ConfigureAwait(false);
            if (instances.Count > 0)
                throw new SnapshotRefusedException(SnapshotRefusal.InstanceInUse,
                    $"A live instance is using this state database: {string.Join("; ", instances)}. Stop it and export again, or wait for its lease to run out. " +
                    "(SnapshotOptions.ExportEvenIfInUse exports anyway, and can miss what that instance writes next.)");
            if (SnapshotUseCheck.FindHeldFile(source, options) is { } held)
                throw new SnapshotRefusedException(SnapshotRefusal.FileHeld, held + " Stop the process that uses it and export again. (SnapshotOptions.ExportEvenIfInUse exports anyway.)");
        }

        var root = Path.GetFullPath(directory);
        var createdRoot = false;
        if (File.Exists(root)) throw new SnapshotRefusedException(SnapshotRefusal.DirectoryNotEmpty, $"The snapshot directory '{root}' is a file; give a directory that is empty or does not exist.");
        if (Directory.Exists(root))
        {
            if (Directory.EnumerateFileSystemEntries(root).Any())
                throw new SnapshotRefusedException(SnapshotRefusal.DirectoryNotEmpty, $"The snapshot directory '{root}' is not empty; give a directory that is empty or does not exist.");
        }
        else
        {
            Directory.CreateDirectory(root);
            createdRoot = true;
        }

        try
        {
            var manifest = await ExportAsync(source, namespaces, definitions, scope, root, options, progress, ct).ConfigureAwait(false);
            var tables = manifest.Modules.SelectMany(m => m.Tables).ToList();
            var result = new SnapshotResult(manifest, root, tables.Sum(t => t.Rows), tables.Count, manifest.Payloads.Count, manifest.Payloads.Sum(p => p.Size), source.Time.GetElapsedTime(started));
            progress?.Report(new SnapshotProgress("done", null, null, result.Rows, result.PayloadBytes));
            return result;
        }
        catch
        {
            Remove(root, createdRoot);
            throw;
        }
    }

    /// <summary>The namespaces to export, in the layout's order (a module after its dependencies): the given modules and what they depend on.</summary>
    private static List<StateNamespace> Select(StateLayout layout, IReadOnlyList<StateModule> modules)
    {
        var wanted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules) Add(module);
        return [.. layout.Namespaces.Where(ns => wanted.Contains(ns.Module.Name))];

        void Add(StateModule module)
        {
            if (!layout.Has(module.Name))
                throw new SnapshotRefusedException(SnapshotRefusal.UnknownModule, $"The module '{module.Name}' is not part of this database's layout (it has: {string.Join(", ", layout.Namespaces.Select(n => n.Module.Name))}).");
            if (!wanted.Add(module.Name)) return;
            foreach (var dependency in module.DependsOn) Add(dependency);
        }
    }

    private static async Task<SnapshotManifest> ExportAsync(
        StateDatabase source, List<StateNamespace> namespaces, Dictionary<string, IReadOnlyList<StateTableDefinition>> definitions, StateScope scope,
        string root, SnapshotOptions options, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.Combine(root, SnapshotFormat.TablesDirectory));
        Directory.CreateDirectory(Path.Combine(root, SnapshotFormat.PayloadsDirectory));

        await using var connection = source.OpenConnection();
        // one consistent view of the whole database where the engine gives one cheaply (PostgreSQL's repeatable read does not block anyone); the others read it as it is, which is what an offline export wants
        await using DbTransaction transaction = connection switch
        {
            SqliteConnection sqlite => sqlite.BeginTransaction(deferred: true),
            _ when source.Dialect.Engine == StateEngineIds.Postgres => await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct).ConfigureAwait(false),
            _ => await connection.BeginTransactionAsync(ct).ConfigureAwait(false),
        };

        var payloads = new List<SnapshotPayloadFile>();
        var moduleEntries = new List<SnapshotModule>();
        var totalRows = 0L;
        foreach (var ns in namespaces)
        {
            var tableEntries = new List<SnapshotTable>();
            foreach (var table in definitions[ns.Module.Name])
            {
                ct.ThrowIfCancellationRequested();
                var entry = await ExportTableAsync(source, connection, transaction, ns, table, root, options, progress, payloads, totalRows, ct).ConfigureAwait(false);
                totalRows += entry.Rows;
                tableEntries.Add(entry);
            }

            moduleEntries.Add(new SnapshotModule(ns.Module.Name, ns.Module.Scope.ToString().ToLowerInvariant(), ns.ScopeName, ns.Module.Version, StateMigrator.Checksum(ns.Module, ns.Module.Version), tableEntries));
        }

        var manifest = new SnapshotManifest(
            SnapshotFormat.Id,
            source.Time.GetUtcNow(),
            source.Dialect.Engine,
            new SnapshotTool("DbDataKit.State", ToolVersion()),
            new SnapshotScope(scope.Group, scope.Service),
            moduleEntries,
            payloads);

        await using (var file = new FileStream(Path.Combine(root, SnapshotFormat.ManifestFile), FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
            await SnapshotManifestJson.WriteAsync(manifest, file, ManifestOptions, ct).ConfigureAwait(false);
        return manifest;
    }

    private static async Task<SnapshotTable> ExportTableAsync(
        StateDatabase source, DbConnection connection, DbTransaction transaction, StateNamespace ns, StateTableDefinition table, string root, SnapshotOptions options,
        IProgress<SnapshotProgress>? progress, List<SnapshotPayloadFile> payloads, long rowsBefore, CancellationToken ct)
    {
        var module = ns.Module.Name;
        var isChunks = module == PayloadStore.ModuleName && table.Name == "chunk";
        var columns = table.Columns
            .Select(c => isChunks && c.Name == "data" && c.Type == SnapshotColumnTypes.Blob ? c with { Storage = SnapshotColumnStorage.PayloadFile } : c)
            .ToList();
        var keyIndexes = table.PrimaryKey.Select(k => columns.FindIndex(c => c.Name == k)).ToArray();
        var payloadIdIndex = isChunks ? columns.FindIndex(c => c.Name == "payload_id") : -1;
        var physical = ns.Tables[table.Name];
        var relative = SnapshotFormat.TableFile(module, table.Name);

        var select = $"SELECT {string.Join(", ", columns.Select(c => c.Name))} FROM {{table:{module}.{table.Name}}}";
        var order = $" ORDER BY {string.Join(", ", table.PrimaryKey)} {source.Limit("batch_size")};";
        var firstBatch = select + order;
        var nextBatch = select + " WHERE " + KeysetPredicate(table.PrimaryKey) + order;

        progress?.Report(new SnapshotProgress("table-start", module, table.Name, rowsBefore, 0));

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var digest = new RowsDigest();
        var rows = 0L;
        string fileHash;
        var payloadSink = isChunks ? new PayloadSink(root, payloads, options.ProgressBytes, (bytes) => progress?.Report(new SnapshotProgress("payload", module, table.Name, rowsBefore + rows, bytes))) : null;
        await using (var file = new FileStream(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var hashing = new HashingStream(file, sha);
            await using (var gzip = new GZipStream(hashing, CompressionLevel.Fastest, leaveOpen: true))
            {
                var buffer = new ArrayBufferWriter<byte>(4096);
                var json = new Utf8JsonWriter(buffer, RowOptions);
                var rowHash = new byte[32];
                object?[]? last = null;
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    var inBatch = 0;
                    await using var command = source.Command(connection, transaction, last is null ? firstBatch : nextBatch);
                    if (last is not null)
                    {
                        for (var k = 0; k < last.Length; k++) command.Bind(source, "k" + k, last[k]);
                    }

                    command.Bind(source, "batch_size", (long)options.BatchSize);
                    await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
                    {
                        while (await reader.ReadAsync(ct).ConfigureAwait(false))
                        {
                            buffer.ResetWrittenCount();
                            json.Reset(buffer);
                            json.WriteStartArray();
                            for (var i = 0; i < columns.Count; i++)
                            {
                                var raw = reader.GetValue(i);
                                if (columns[i].Storage == SnapshotColumnStorage.PayloadFile && raw is byte[] bytes)
                                {
                                    var id = StateValues.ToGuid(reader.GetValue(payloadIdIndex));
                                    var offset = await payloadSink!.AppendAsync(id, bytes, ct).ConfigureAwait(false);
                                    json.WriteStartObject();
                                    json.WriteString("payload", id.ToString("D", CultureInfo.InvariantCulture));
                                    json.WriteNumber("offset", offset);
                                    json.WriteNumber("length", bytes.Length);
                                    json.WriteEndObject();
                                }
                                else
                                    SnapshotValues.Write(json, columns[i].Type, raw);
                            }

                            json.WriteEndArray();
                            json.Flush();
                            SHA256.HashData(buffer.WrittenSpan, rowHash);
                            digest.Add(rowHash);
                            buffer.GetSpan(1)[0] = (byte)'\n';
                            buffer.Advance(1);
                            await gzip.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);

                            rows++;
                            inBatch++;
                            if (inBatch == options.BatchSize)
                            {
                                last = new object?[keyIndexes.Length];
                                for (var k = 0; k < keyIndexes.Length; k++) last[k] = reader.GetValue(keyIndexes[k]);
                            }
                        }
                    }

                    progress?.Report(new SnapshotProgress("rows", module, table.Name, rowsBefore + rows, payloadSink?.Bytes ?? 0));
                    if (inBatch < options.BatchSize) break;
                }
            }

            await file.FlushAsync(ct).ConfigureAwait(false);
            fileHash = Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        if (payloadSink is not null) await payloadSink.FinishAsync(ct).ConfigureAwait(false);
        progress?.Report(new SnapshotProgress("table-done", module, table.Name, rowsBefore + rows, payloadSink?.Bytes ?? 0));
        return new SnapshotTable(table.Name, physical, relative, columns, table.PrimaryKey, rows, fileHash, digest.Hex());
    }

    /// <summary>The condition that selects the rows after the key <c>$k0, $k1, ...</c>: <c>(a &gt; $k0) OR (a = $k0 AND b &gt; $k1)</c>, which every engine can use an index for (row values are not on SQL Server).</summary>
    private static string KeysetPredicate(IReadOnlyList<string> key)
    {
        var alternatives = new List<string>();
        for (var i = 0; i < key.Count; i++)
        {
            var parts = key.Take(i).Select((c, j) => $"{c} = $k{j}").Append($"{key[i]} > $k{i}");
            alternatives.Add("(" + string.Join(" AND ", parts) + ")");
        }

        return string.Join(" OR ", alternatives);
    }

    private static string ToolVersion()
    {
        var version = typeof(StateSnapshotWriter).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? typeof(StateSnapshotWriter).Assembly.GetName().Version?.ToString() ?? "unknown";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? version : version[..plus];
    }

    /// <summary>Deletes what a failed export wrote: the whole directory if the export made it, else its contents (it was empty when the export began).</summary>
    private static void Remove(string root, bool createdRoot)
    {
        try
        {
            if (createdRoot)
            {
                Directory.Delete(root, recursive: true);
                return;
            }

            foreach (var entry in Directory.EnumerateFileSystemEntries(root))
            {
                if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
                else File.Delete(entry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the export's own failure is what the caller needs to see; a file that cannot be removed is left, and the next export refuses the directory, naming it
        }
    }

    /// <summary>Passes bytes on and hashes them: the hash of the file as it was written, without reading it back.</summary>
    private sealed class HashingStream(Stream inner, IncrementalHash hash) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            hash.AppendData(buffer);
            inner.Write(buffer);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            hash.AppendData(buffer.Span);
            await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the chunks of one payload after another to its file, one file open at a time (the chunk table is read in payload order).</summary>
    private sealed class PayloadSink(string root, List<SnapshotPayloadFile> files, long progressBytes, Action<long> report)
    {
        private Guid _id;
        private FileStream? _file;
        private IncrementalHash? _hash;
        private long _size;
        private int _chunks;
        private long _sinceReport;

        /// <summary>Payload bytes written so far, over every payload.</summary>
        public long Bytes { get; private set; }

        /// <summary>Appends a chunk to its payload's file; returns where in the file it starts.</summary>
        public async ValueTask<long> AppendAsync(Guid id, byte[] data, CancellationToken ct)
        {
            if (_file is null || id != _id)
            {
                await FinishAsync(ct).ConfigureAwait(false);
                _id = id;
                _file = new FileStream(Path.Combine(root, SnapshotFormat.PayloadFile(id).Replace('/', Path.DirectorySeparatorChar)), FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                _size = 0;
                _chunks = 0;
            }

            var offset = _size;
            _hash!.AppendData(data);
            await _file.WriteAsync(data, ct).ConfigureAwait(false);
            _size += data.Length;
            _chunks++;
            Bytes += data.Length;
            _sinceReport += data.Length;
            if (_sinceReport >= progressBytes)
            {
                _sinceReport = 0;
                report(Bytes);
            }

            return offset;
        }

        /// <summary>Closes the file in progress and lists it.</summary>
        public async ValueTask FinishAsync(CancellationToken ct)
        {
            if (_file is null) return;
            await _file.FlushAsync(ct).ConfigureAwait(false);
            await _file.DisposeAsync().ConfigureAwait(false);
            files.Add(new SnapshotPayloadFile(_id, SnapshotFormat.PayloadFile(_id), _size, Convert.ToHexStringLower(_hash!.GetHashAndReset()), _chunks));
            _hash.Dispose();
            _file = null;
            _hash = null;
        }
    }
}

/// <summary>Writes the manifest as JSON, the format id first.</summary>
internal static class SnapshotManifestJson
{
    public static async Task WriteAsync(SnapshotManifest manifest, Stream output, JsonWriterOptions options, CancellationToken ct)
    {
        var buffer = new ArrayBufferWriter<byte>();
        await using (var json = new Utf8JsonWriter(buffer, options))
        {
            json.WriteStartObject();
            json.WriteString("format", manifest.Format);
            json.WriteString("created_at", StateValues.FormatTime(manifest.CreatedAt));
            json.WriteString("engine", manifest.Engine);
            json.WriteStartObject("tool");
            json.WriteString("name", manifest.Tool.Name);
            json.WriteString("version", manifest.Tool.Version);
            json.WriteEndObject();
            json.WriteStartObject("scope");
            json.WriteString("group", manifest.Scope.Group);
            json.WriteString("service", manifest.Scope.Service);
            json.WriteEndObject();

            json.WriteStartArray("modules");
            foreach (var module in manifest.Modules)
            {
                json.WriteStartObject();
                json.WriteString("name", module.Name);
                json.WriteString("scope_kind", module.ScopeKind);
                json.WriteString("scope_name", module.ScopeName);
                json.WriteNumber("version", module.Version);
                json.WriteString("checksum", module.Checksum);
                json.WriteStartArray("tables");
                foreach (var table in module.Tables)
                {
                    json.WriteStartObject();
                    json.WriteString("name", table.Name);
                    json.WriteString("physical_name", table.PhysicalName);
                    json.WriteString("file", table.File);
                    json.WriteStartArray("columns");
                    foreach (var column in table.Columns)
                    {
                        json.WriteStartObject();
                        json.WriteString("name", column.Name);
                        json.WriteString("type", column.Type);
                        json.WriteBoolean("nullable", column.Nullable);
                        if (column.Identity) json.WriteBoolean("identity", true);
                        if (column.Storage is not null) json.WriteString("storage", column.Storage);
                        json.WriteEndObject();
                    }

                    json.WriteEndArray();
                    json.WriteStartArray("primary_key");
                    foreach (var key in table.PrimaryKey) json.WriteStringValue(key);
                    json.WriteEndArray();
                    json.WriteNumber("rows", table.Rows);
                    json.WriteString("sha256", table.Sha256);
                    json.WriteString("rows_digest", table.RowsDigest);
                    json.WriteEndObject();
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();

            json.WriteStartArray("payloads");
            foreach (var payload in manifest.Payloads)
            {
                json.WriteStartObject();
                json.WriteString("id", payload.Id.ToString("D", CultureInfo.InvariantCulture));
                json.WriteString("file", payload.File);
                json.WriteNumber("size", payload.Size);
                json.WriteString("sha256", payload.Sha256);
                json.WriteNumber("chunks", payload.Chunks);
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        await output.WriteAsync(buffer.WrittenMemory, ct).ConfigureAwait(false);
        await output.WriteAsync(Encoding.UTF8.GetBytes("\n"), ct).ConfigureAwait(false);
    }
}
