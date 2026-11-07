using System.Buffers;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DbDataKit.State.Snapshots;

/// <summary>
/// Loads a snapshot (plan 29b, decisions M3, M4 and M6; the format is <see cref="SnapshotFormat"/>) into an <b>empty</b> state database, whatever engine the snapshot came from.
/// <para>
/// <b>Before anything is written</b> the importer reads and checks the manifest (format <c>state-snapshot/1</c>), refuses a snapshot whose module is newer than this software's (or unknown), a module whose
/// first migrations are not the ones the snapshot was made with, a manifest whose tables and columns are not what the migrations declare at that version, and a target that has any module in its registry
/// (<see cref="SnapshotImportRefusal"/>; each message names the file or module), and hashes every table file against the manifest. Payload files are hashed while they are streamed into the chunk rows.
/// </para>
/// <para>
/// <b>Then, module by module in the manifest's dependency order</b>, in one transaction per module (PostgreSQL, SQL Server and SQLite): the module is created <i>at the snapshot's version</i> (only that many
/// migrations, through the migrator, in the target's own layout: tables are found by logical name, so schemas, prefixes and SQLite files may differ from the source), its tables are loaded in the order the
/// migrations created them, identity-style keys are inserted explicitly and reseeded, the target's rows are read back and the table's <c>rows_digest</c> is compared with the manifest's, and the module
/// is committed. Any failure rolls that module back; modules that completed before stay (they are named in <see cref="SnapshotImportException.CompletedModules"/>), and
/// <see cref="ImportOptions.ResetOnFailure"/> or <see cref="ResetAsync"/> removes them. Cancellation does the same: the target is as it was after the last completed module. When all modules are in, and
/// <see cref="ImportOptions.MigrateAfterImport"/> is set, the normal migrator brings an older snapshot up to this software's versions.
/// </para>
/// <para>
/// Values are converted from the neutral forms (see <see cref="SnapshotColumnTypes"/>) to what the target engine stores with the conventions of <see cref="StateValues"/>. Rows are loaded by batched
/// multi-row <c>INSERT</c> with parameters (rows per statement bounded by the engine's parameter limit and by <see cref="ImportOptions.BatchBytes"/>); the neutral line of every row is hashed as it is read, so
/// the snapshot's own <c>rows_digest</c> is checked as well. Chunk rows get their bytes from the payload files, one chunk in memory at a time, the file's size and SHA-256 checked as it is read.
/// </para>
/// </summary>
public static class StateSnapshotImporter
{
    private const int SqlServerParameters = 2000;
    private const int SqliteParameters = 999;
    private const int PostgresParameters = 30000;

    /// <param name="snapshotDirectory">A snapshot written by <see cref="StateSnapshotWriter"/>; nothing in it is changed.</param>
    /// <param name="target">An empty database built from modules and opened without migrating (<see cref="StateDatabase.OpenForImport"/>); a database that already has modules is refused.</param>
    /// <param name="modules">The modules this software knows; a module of the snapshot that is not among them (or their dependencies) is refused.</param>
    /// <param name="scope">The group and service this process is; the target's layout was made for it.</param>
    public static async Task<SnapshotImportResult> ImportAsync(
        string snapshotDirectory, StateDatabase target, IReadOnlyList<StateModule> modules, StateScope scope, ImportOptions options, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(scope);
        options ??= ImportOptions.Default;
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchRows, 1);
        var layout = target.Layout ?? throw new SnapshotImportException(SnapshotImportRefusal.Unsupported, "A snapshot is imported into a database built from modules; this one was built from a StateSchema (the structure being replaced).");

        var started = target.Time.GetTimestamp();
        var root = Path.GetFullPath(snapshotDirectory);
        var manifest = StateSnapshotReader.ReadManifest(root);
        var plan = Plan(manifest, layout, modules);
        await CheckTargetIsEmptyAsync(target, layout, ct).ConfigureAwait(false);

        foreach (var payload in manifest.Payloads)
        {
            var path = StateSnapshotReader.ResolveFile(root, payload.File, "the payload file");
            if (new FileInfo(path).Length != payload.Size)
                throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"The payload file '{payload.File}' is {new FileInfo(path).Length} bytes and the manifest says {payload.Size}.");
        }

        foreach (var table in manifest.Modules.SelectMany(m => m.Tables))
            await StateSnapshotReader.VerifyFileAsync(StateSnapshotReader.ResolveFile(root, table.File, "the table file"), table.File, "the table file", table.Sha256, null, ct).ConfigureAwait(false);

        var appliedBy = options.AppliedBy ?? target.AppliedBy;
        var completed = new List<PlannedModule>();
        var warnings = new List<string>();
        var context = new ImportContext(root, target, layout, manifest, options, progress, warnings);
        Exception? failure = null;

        await using (var connection = target.OpenConnection())
        {
            var migrationLock = target.Dialect.AcquireMigrationLock(connection);
            try
            {
                StateMigrator.PrepareForImport(connection, target, layout);
                foreach (var planned in plan)
                {
                    await ImportModuleAsync(context, connection, planned, appliedBy, ct).ConfigureAwait(false);
                    completed.Add(planned);
                    progress?.Report(new SnapshotProgress("module-done", planned.Module.Name, null, context.Rows, context.PayloadBytes));
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }

            if (failure is not null)
            {
                var reset = false;
                string? resetFailure = null;
                if (options.ResetOnFailure && completed.Count > 0)
                {
                    try
                    {
                        await ResetCoreAsync(connection, target, layout, [.. completed.Select(c => c.Namespace)]).ConfigureAwait(false);
                        reset = true;
                    }
                    catch (Exception ex)
                    {
                        resetFailure = ex.Message;
                    }
                }

                migrationLock?.Dispose();
                throw Translate(failure, completed.Select(c => c.Module.Name).ToList(), reset, resetFailure, ct);
            }

            migrationLock?.Dispose();
        }

        var migrated = false;
        if (options.MigrateAfterImport)
        {
            try
            {
                target.ApplyMigrations();
                migrated = true;
            }
            catch (Exception ex)
            {
                throw new SnapshotImportException(SnapshotImportRefusal.MigrationFailed,
                    $"Every module of the snapshot was loaded, and bringing them up to this version failed: {ex.Message}", [.. plan.Select(p => p.Module.Name)], ex);
            }
        }

        var tables = manifest.Modules.SelectMany(m => m.Tables).ToList();
        progress?.Report(new SnapshotProgress("done", null, null, context.Rows, context.PayloadBytes));
        return new SnapshotImportResult(manifest, [.. plan.Select(p => p.Module.Name)], context.Rows, tables.Count, manifest.Payloads.Count, context.PayloadBytes, migrated, warnings, target.Time.GetElapsedTime(started));
    }

    /// <summary>
    /// Drops the tables of the named modules and their registry rows (the modules in reverse dependency order): what an import created, for a clean retry. Only for modules the importer created; it deletes
    /// whatever tables those modules have in this layout.
    /// </summary>
    public static async Task ResetAsync(StateDatabase target, IReadOnlyList<string> moduleNames, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(moduleNames);
        var layout = target.Layout ?? throw new SnapshotImportException(SnapshotImportRefusal.Unsupported, "Only a database built from modules can be reset.");
        var namespaces = layout.Namespaces.Where(n => moduleNames.Contains(n.Module.Name)).ToList();
        await using var connection = target.OpenConnection();
        using var migrationLock = target.Dialect.AcquireMigrationLock(connection);
        await ResetCoreAsync(connection, target, layout, namespaces).ConfigureAwait(false);
    }

    private static async Task ResetCoreAsync(DbConnection connection, StateDatabase target, StateLayout layout, List<StateNamespace> namespaces)
    {
        await using var transaction = target.Dialect.BeginMigrationTransaction(connection);
        foreach (var ns in Enumerable.Reverse(namespaces))
        {
            var ordered = StateTableDefinitions.Read(ns.Module).Select(t => t.Name).Reverse().ToList();
            foreach (var name in ordered.Concat(ns.Tables.Keys.Except(ordered)))
            {
                await using var drop = connection.CreateCommand();
                drop.Transaction = transaction;
                drop.CommandText = $"DROP TABLE IF EXISTS {ns.Tables[name]};";
                await drop.ExecuteNonQueryAsync().ConfigureAwait(false);
            }

            await using var registry = target.Command(connection, transaction, $"DELETE FROM {layout.Registry} WHERE module = $module AND scope = $scope;");
            registry.Bind(target, "module", ns.Module.Name).Bind(target, "scope", ns.ScopeName ?? "install");
            await registry.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await transaction.CommitAsync().ConfigureAwait(false);
    }

    private static Exception Translate(Exception failure, List<string> completed, bool reset, string? resetFailure, CancellationToken ct)
    {
        var suffix = completed.Count == 0
            ? " No module had been completed, so the target holds nothing of the import."
            : $" Modules completed before the failure: {string.Join(", ", completed)}." + (reset
                ? " They were removed again (ImportOptions.ResetOnFailure)."
                : resetFailure is not null
                    ? $" Removing them failed too ({resetFailure})."
                    : " They are still in the target; set ImportOptions.ResetOnFailure or call StateSnapshotImporter.ResetAsync to remove them before a retry.");

        if (failure is OperationCanceledException || ct.IsCancellationRequested)
            return new OperationCanceledException("The import was cancelled; the module in progress was rolled back." + suffix, failure, ct);
        if (failure is SnapshotImportException known)
            return new SnapshotImportException(known.Reason, known.Message + suffix, completed, known) { WasReset = reset };
        return new SnapshotImportException(SnapshotImportRefusal.LoadFailed, $"The import failed: {failure.Message}" + suffix, completed, failure) { WasReset = reset };
    }

    // ------------------------------------------------------------------------------------------------------------- checks before anything is written

    private sealed record PlannedModule(SnapshotModule Module, StateNamespace Namespace, IReadOnlyList<StateTableDefinition> Definitions);

    private static List<PlannedModule> Plan(SnapshotManifest manifest, StateLayout layout, IReadOnlyList<StateModule> modules)
    {
        var known = new Dictionary<string, StateModule>(StringComparer.Ordinal);
        foreach (var module in modules) Add(module);
        void Add(StateModule module)
        {
            if (!known.TryAdd(module.Name, module)) return;
            foreach (var dependency in module.DependsOn) Add(dependency);
        }

        var plan = new List<PlannedModule>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in manifest.Modules)
        {
            if (!known.ContainsKey(entry.Name))
                throw new SnapshotImportException(SnapshotImportRefusal.ModuleUnknown,
                    $"The snapshot has the module '{entry.Name}', which this software does not have (it has: {string.Join(", ", known.Keys)}). The snapshot was made by a different product or a newer version; import it with the software that made it.");
            if (!layout.Has(entry.Name))
                throw new SnapshotImportException(SnapshotImportRefusal.ModuleUnknown,
                    $"The snapshot has the module '{entry.Name}', and the target database is not set up for it (its layout has: {string.Join(", ", layout.Namespaces.Select(n => n.Module.Name))}).");
            var ns = layout.Namespace(entry.Name);
            var module = ns.Module;

            if (entry.Version > module.Version)
                throw new SnapshotImportException(SnapshotImportRefusal.ModuleNewer,
                    $"The snapshot has the module '{entry.Name}' at version {entry.Version}, and this software only knows version {module.Version}. Upgrade this software to the version that made the snapshot, or newer; a module is never taken back to an older version.");
            if (entry.Version < 0)
                throw new SnapshotImportException(SnapshotImportRefusal.ManifestCorrupt, $"The manifest gives the module '{entry.Name}' the version {entry.Version}.");
            if (!string.Equals(module.Scope.ToString(), entry.ScopeKind, StringComparison.OrdinalIgnoreCase))
                throw new SnapshotImportException(SnapshotImportRefusal.SchemaMismatch, $"The snapshot keeps the module '{entry.Name}' per {entry.ScopeKind}, and this software keeps it per {module.Scope.ToString().ToLowerInvariant()}.");
            if (StateMigrator.Checksum(module, entry.Version) != entry.Checksum)
                throw new SnapshotImportException(SnapshotImportRefusal.ChecksumMismatch,
                    $"The first {entry.Version} migrations of the module '{entry.Name}' (migration numbers 1 to {entry.Version}) are not the ones the snapshot was made with: a migration that shipped was edited. Never change a migration that has shipped; append a new one.");
            foreach (var dependency in module.DependsOn)
            {
                if (!seen.Contains(dependency.Name))
                    throw new SnapshotImportException(SnapshotImportRefusal.SchemaMismatch, $"The module '{entry.Name}' depends on '{dependency.Name}', and the snapshot does not have it before '{entry.Name}'.");
            }

            // the tables as the migrations declare them at the snapshot's version, which the manifest has to agree with
            var atVersion = new StateModule(module.Name, module.Scope, [.. module.Migrations.Take(entry.Version)], module.DependsOn);
            var definitions = StateTableDefinitions.Read(atVersion);
            if (!definitions.Select(d => d.Name).SequenceEqual(entry.Tables.Select(t => t.Name)))
                throw new SnapshotImportException(SnapshotImportRefusal.SchemaMismatch,
                    $"The snapshot's tables of the module '{entry.Name}' ({string.Join(", ", entry.Tables.Select(t => t.Name))}) are not the tables its migrations create at version {entry.Version} ({string.Join(", ", definitions.Select(d => d.Name))}).");
            foreach (var (definition, table) in definitions.Zip(entry.Tables))
            {
                if (!ns.Tables.ContainsKey(table.Name))
                    throw new SnapshotImportException(SnapshotImportRefusal.SchemaMismatch, $"The table '{entry.Name}.{table.Name}' is not in the target's layout.");
                var expected = definition.Columns.Select(c => (c.Name, c.Type, c.Identity));
                if (!expected.SequenceEqual(table.Columns.Select(c => (c.Name, c.Type, c.Identity))) || !definition.PrimaryKey.SequenceEqual(table.PrimaryKey))
                    throw new SnapshotImportException(SnapshotImportRefusal.SchemaMismatch,
                        $"The columns of the table '{entry.Name}.{table.Name}' in the snapshot ({string.Join(", ", table.Columns.Select(c => c.Name + ":" + c.Type))}) are not the ones its migrations declare at version {entry.Version} ({string.Join(", ", definition.Columns.Select(c => c.Name + ":" + c.Type))}).");
            }

            seen.Add(entry.Name);
            plan.Add(new PlannedModule(entry, ns, definitions));
        }

        return plan;
    }

    private static async Task CheckTargetIsEmptyAsync(StateDatabase target, StateLayout layout, CancellationToken ct)
    {
        await using var connection = target.OpenConnection();
        ct.ThrowIfCancellationRequested();
        var registered = StateMigrator.RegisteredModules(connection, target, layout);
        if (registered.Count > 0)
        {
            var first = registered[0];
            throw new SnapshotImportException(SnapshotImportRefusal.TargetNotEmpty,
                $"The target database already has the module '{first.Module}' (version {first.Version}{(registered.Count > 1 ? $", and {registered.Count - 1} more: {string.Join(", ", registered.Skip(1).Select(r => r.Module))}" : "")}) in its registry '{layout.Registry}'. " +
                "An import goes into an empty database only; use a new database, or remove those modules' tables and registry rows first.");
        }

        if (!target.Dialect.TableExists(connection, layout.Registry) && target.Dialect.FindLegacySchemaVersion(connection) is { } marker)
            throw new SnapshotImportException(SnapshotImportRefusal.TargetNotEmpty, $"The target database was made by the structure that keeps one schema version for the whole database ({marker}); an import goes into an empty database only.");
    }

    // ------------------------------------------------------------------------------------------------------------- loading

    private sealed class ImportContext(string root, StateDatabase target, StateLayout layout, SnapshotManifest manifest, ImportOptions options, IProgress<SnapshotProgress>? progress, List<string> warnings)
    {
        public string Root { get; } = root;
        public StateDatabase Target { get; } = target;
        public StateLayout Layout { get; } = layout;
        public SnapshotManifest Manifest { get; } = manifest;
        public ImportOptions Options { get; } = options;
        public IProgress<SnapshotProgress>? Progress { get; } = progress;
        public List<string> Warnings { get; } = warnings;
        public long Rows { get; set; }
        public long PayloadBytes { get; set; }
        public bool Sqlite => Target.Dialect.Engine == StateEngineIds.Sqlite;
        public bool Postgres => Target.Dialect.Engine == StateEngineIds.Postgres;
        public bool MsSql => Target.Dialect.Engine == StateEngineIds.MsSql;
    }

    private static async Task ImportModuleAsync(ImportContext context, DbConnection connection, PlannedModule planned, string appliedBy, CancellationToken ct)
    {
        var entry = planned.Module;
        var table = "";
        await using var transaction = context.Target.Dialect.BeginMigrationTransaction(connection);
        try
        {
            ct.ThrowIfCancellationRequested();
            StateMigrator.ApplyModuleTo(connection, context.Target, context.Layout, planned.Namespace, entry.Version, transaction, appliedBy);
            foreach (var snapshotTable in entry.Tables)
            {
                table = snapshotTable.Name;
                await ImportTableAsync(context, connection, transaction, entry, snapshotTable, ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            try
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception rollback) when (rollback is DbException or InvalidOperationException)
            {
                // the engine already ended the transaction (a failed statement can do that); the module is not committed either way
            }

            if (ex is SnapshotImportException or OperationCanceledException) throw;
            throw new SnapshotImportException(SnapshotImportRefusal.LoadFailed,
                $"Loading the table '{entry.Name}.{table}' failed: {ex.Message} The module '{entry.Name}' was rolled back.", inner: ex);
        }
    }

    private static async Task ImportTableAsync(ImportContext context, DbConnection connection, DbTransaction transaction, SnapshotModule module, SnapshotTable table, CancellationToken ct)
    {
        var target = context.Target;
        var dialect = target.Dialect;
        var columns = table.Columns;
        var isChunks = module.Name == PayloadStore.ModuleName && table.Name == "chunk";
        var identity = columns.Where(c => c.Identity).Select(c => c.Name).ToList();
        var maxParameters = context.Options.MaxParameters ?? (context.Sqlite ? SqliteParameters : context.MsSql ? SqlServerParameters : PostgresParameters);
        var batchRows = Math.Max(1, Math.Min(Math.Min(context.Options.BatchRows, 1000), maxParameters / columns.Count));
        var token = $"{{table:{module.Name}.{table.Name}}}";
        var types = columns.Select(c => DbTypeOf(c.Type, context.Sqlite)).ToArray();

        context.Progress?.Report(new SnapshotProgress("table-start", module.Name, table.Name, context.Rows, context.PayloadBytes));

        var pending = new List<object?[]>(batchRows);
        var pendingBytes = 0L;
        var rows = 0L;
        var reducedRows = 0L;
        var fileDigest = new RowsDigest();
        var expectedDigest = new RowsDigest();
        var lineHash = new byte[32];
        var payloads = isChunks ? new PayloadRestorer(context.Root, context.Manifest.Payloads, context.Options.ProgressBytes, bytes => context.Progress?.Report(new SnapshotProgress("payload", module.Name, table.Name, context.Rows + rows, context.PayloadBytes + bytes))) : null;
        var payloadIdColumn = isChunks ? columns.ToList().FindIndex(c => c.Name == "payload_id") : -1;
        var identityOn = false;

        try
        {
            if (identity.Count > 0 && context.MsSql)
            {
                await ExecuteAsync(target, connection, transaction, $"SET IDENTITY_INSERT {token} ON;", ct).ConfigureAwait(false);
                identityOn = true;
            }

            using (var lines = StateSnapshotReader.OpenTable(context.Root, table))
            {
                while (await lines.ReadAsync(ct).ConfigureAwait(false) is { } line)
                {
                    ct.ThrowIfCancellationRequested();
                    using var document = JsonDocument.Parse(line);
                    var row = document.RootElement;
                    if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != columns.Count)
                        throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"Line {lines.LineNumber} of the table file '{table.File}' is not a row of {columns.Count} values.");

                    SHA256.HashData(Encoding.UTF8.GetBytes(line), lineHash);
                    fileDigest.Add(lineHash);

                    var values = new object?[columns.Count];
                    Dictionary<int, string>? rounded = null;
                    var index = 0;
                    foreach (var element in row.EnumerateArray())
                    {
                        var column = columns[index];
                        if (column.Storage == SnapshotColumnStorage.PayloadFile)
                        {
                            var chunk = await ReadChunkAsync(payloads!, element, row[payloadIdColumn], table.File, lines.LineNumber, ct).ConfigureAwait(false);
                            values[index] = chunk;
                            pendingBytes += chunk.Length;
                        }
                        else
                        {
                            values[index] = ToParameter(context, column, element, table.File, lines.LineNumber, out var roundedText);
                            if (roundedText is not null) (rounded ??= [])[index] = roundedText;
                            if (values[index] is byte[] bytes) pendingBytes += bytes.Length;
                        }

                        index++;
                    }

                    if (rounded is null) expectedDigest.Add(lineHash);
                    else
                    {
                        reducedRows++;
                        expectedDigest.Add(SHA256.HashData(Reserialise(row, rounded)));
                    }

                    pending.Add(values);
                    rows++;
                    if (pending.Count >= batchRows || pendingBytes >= context.Options.BatchBytes)
                    {
                        await FlushAsync(context, connection, transaction, token, table, identity.Count > 0, types, pending, ct).ConfigureAwait(false);
                        pending.Clear();
                        pendingBytes = 0;
                        context.Progress?.Report(new SnapshotProgress("rows", module.Name, table.Name, context.Rows + rows, context.PayloadBytes + (payloads?.Bytes ?? 0)));
                    }
                }
            }

            if (pending.Count > 0)
            {
                await FlushAsync(context, connection, transaction, token, table, identity.Count > 0, types, pending, ct).ConfigureAwait(false);
                pending.Clear();
            }

            if (payloads is not null) await payloads.FinishAllAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (identityOn)
            {
                try { await ExecuteAsync(target, connection, transaction, $"SET IDENTITY_INSERT {token} OFF;", CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is DbException or InvalidOperationException) { /* the connection is going away with the failed transaction */ }
            }
        }

        if (rows != table.Rows)
            throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"The table file '{table.File}' has {rows} rows and the manifest says {table.Rows}.");
        if (!string.Equals(fileDigest.Hex(), table.RowsDigest, StringComparison.OrdinalIgnoreCase))
            throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"The rows of the table file '{table.File}' do not have the digest the manifest says ({fileDigest.Hex()} against {table.RowsDigest}).");

        // an identity-style key counts on from the highest id (SQLite does it by itself for the ids it was given)
        if (identity.Count > 0 && rows > 0 && !context.Sqlite) await ReseedAsync(context, connection, transaction, module, table, identity[0], ct).ConfigureAwait(false);

        // the target's own rows, read back, have to have the digest the manifest has (what the engine stores is what was exported)
        var (targetRows, targetDigest) = await ComputeTargetDigestAsync(context, connection, transaction, module, table, isChunks, ct).ConfigureAwait(false);
        var tableName = $"{module.Name}.{table.Name}";
        if (targetRows != table.Rows)
            throw new SnapshotImportException(SnapshotImportRefusal.TableDigestMismatch, $"After the load the target's table '{tableName}' has {targetRows} rows and the snapshot has {table.Rows}.");
        if (!string.Equals(targetDigest, table.RowsDigest, StringComparison.OrdinalIgnoreCase))
        {
            if (reducedRows > 0 && string.Equals(targetDigest, expectedDigest.Hex(), StringComparison.OrdinalIgnoreCase))
                context.Warnings.Add($"The table '{tableName}': {reducedRows} rows have a time value with more precision than {dialect.Engine} stores (microseconds); it was rounded down, and the table's digest differs from the manifest's for that reason only.");
            else
                throw new SnapshotImportException(SnapshotImportRefusal.TableDigestMismatch,
                    $"After the load the target's table '{tableName}' has the rows_digest {targetDigest} and the snapshot's manifest says {table.RowsDigest}: what the target stores is not what the snapshot holds.");
        }

        context.Rows += rows;
        context.PayloadBytes += payloads?.Bytes ?? 0;
        context.Progress?.Report(new SnapshotProgress("table-done", module.Name, table.Name, context.Rows, context.PayloadBytes));
    }

    private static DbType DbTypeOf(string type, bool sqlite) => type switch
    {
        SnapshotColumnTypes.Uuid => sqlite ? DbType.String : DbType.Guid,
        SnapshotColumnTypes.Time => sqlite ? DbType.String : DbType.DateTimeOffset,
        SnapshotColumnTypes.Bool => DbType.Boolean,
        SnapshotColumnTypes.Int => DbType.Int64,
        SnapshotColumnTypes.Decimal => sqlite ? DbType.String : DbType.Decimal,
        SnapshotColumnTypes.Blob => DbType.Binary,
        _ => DbType.String,
    };

    /// <summary>The value of one neutral element as the target engine takes it (see <see cref="StateValues.ForParameter"/>); <paramref name="roundedText"/> is set when the engine will store a coarser time than the snapshot has.</summary>
    private static object? ToParameter(ImportContext context, SnapshotColumn column, JsonElement element, string file, long line, out string? roundedText)
    {
        roundedText = null;
        if (element.ValueKind == JsonValueKind.Null) return null;
        var dialect = context.Target.Dialect;
        try
        {
            switch (column.Type)
            {
                case SnapshotColumnTypes.Uuid:
                    return StateValues.ForParameter(dialect, Guid.Parse(element.GetString()!, CultureInfo.InvariantCulture));
                case SnapshotColumnTypes.Time:
                    {
                        var time = DateTimeOffset.Parse(element.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                        // PostgreSQL's timestamptz keeps microseconds; the provider drops the seventh digit
                        if (context.Postgres && time.Ticks % 10 != 0)
                        {
                            time = new DateTimeOffset(time.Ticks - (time.Ticks % 10), TimeSpan.Zero);
                            roundedText = StateValues.FormatTime(time);
                        }

                        return StateValues.ForParameter(dialect, time);
                    }
                case SnapshotColumnTypes.Bool:
                    return element.GetBoolean();
                case SnapshotColumnTypes.Int:
                    return element.GetInt64();
                case SnapshotColumnTypes.Decimal:
                    return StateValues.ForParameter(dialect, decimal.Parse(element.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture));
                case SnapshotColumnTypes.Blob:
                    return element.GetBytesFromBase64();
                default:
                    return element.GetString();
            }
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or OverflowException)
        {
            throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"Line {line} of the table file '{file}': the value of the column '{column.Name}' is not a valid {column.Type} ({ex.Message}).", inner: ex);
        }
    }

    /// <summary>The row as written again with the given columns replaced (what the digest of a row is when a time was rounded).</summary>
    private static byte[] Reserialise(JsonElement row, Dictionary<int, string> replaced)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var json = new Utf8JsonWriter(buffer, StateSnapshotWriter.RowOptions))
        {
            json.WriteStartArray();
            var index = 0;
            foreach (var element in row.EnumerateArray())
            {
                if (replaced.TryGetValue(index, out var text)) json.WriteStringValue(text);
                else element.WriteTo(json);
                index++;
            }

            json.WriteEndArray();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static async Task<byte[]> ReadChunkAsync(PayloadRestorer payloads, JsonElement marker, JsonElement payloadIdValue, string file, long line, CancellationToken ct)
    {
        try
        {
            var id = Guid.Parse(marker.GetProperty("payload").GetString()!, CultureInfo.InvariantCulture);
            if (id != Guid.Parse(payloadIdValue.GetString()!, CultureInfo.InvariantCulture))
                throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"Line {line} of the table file '{file}': the chunk's data marker names a different payload than its row.");
            return await payloads.ReadAsync(id, marker.GetProperty("offset").GetInt64(), marker.GetProperty("length").GetInt32(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException or FormatException)
        {
            if (ex is SnapshotImportException) throw;
            throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"Line {line} of the table file '{file}': the chunk's data is not a payload marker ({ex.Message}).", inner: ex);
        }
    }

    private static async Task ExecuteAsync(StateDatabase target, DbConnection connection, DbTransaction? transaction, string sql, CancellationToken ct)
    {
        await using var command = target.Command(connection, transaction, sql);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>One multi-row INSERT of the pending rows.</summary>
    private static async Task FlushAsync(ImportContext context, DbConnection connection, DbTransaction transaction, string tableToken, SnapshotTable table, bool hasIdentity, DbType[] types, List<object?[]> rows, CancellationToken ct)
    {
        var target = context.Target;
        var columns = table.Columns;
        var sql = new StringBuilder(64 + (rows.Count * columns.Count * 12));
        sql.Append("INSERT INTO ").Append(tableToken).Append(" (").AppendJoin(", ", columns.Select(c => c.Name)).Append(')');
        if (hasIdentity && context.Postgres) sql.Append(" OVERRIDING SYSTEM VALUE");
        sql.Append(" VALUES ");
        for (var r = 0; r < rows.Count; r++)
        {
            if (r > 0) sql.Append(", ");
            sql.Append('(');
            for (var c = 0; c < columns.Count; c++)
            {
                if (c > 0) sql.Append(", ");
                var name = $"$r{r}c{c}";
                if (columns[c].Type == SnapshotColumnTypes.Json) sql.Append("CAST(").Append(name).Append(" AS {{json}})");
                else sql.Append(name);
            }

            sql.Append(')');
        }

        sql.Append(';');
        await using var command = target.Command(connection, transaction, sql.ToString());
        command.CommandTimeout = 0;
        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < columns.Count; c++)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = target.Dialect.ParameterName($"r{r}c{c}");
                parameter.DbType = types[c];
                parameter.Value = rows[r][c] ?? DBNull.Value;
                command.Parameters.Add(parameter);
            }
        }

        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Makes the next automatic id one more than the highest (PostgreSQL's sequence behind the identity column, SQL Server's identity seed).</summary>
    private static async Task ReseedAsync(ImportContext context, DbConnection connection, DbTransaction transaction, SnapshotModule module, SnapshotTable table, string column, CancellationToken ct)
    {
        var target = context.Target;
        var qualified = context.Layout.Table(module.Name, table.Name);
        long max;
        await using (var read = target.Command(connection, transaction, $"SELECT MAX({column}) FROM {{table:{module.Name}.{table.Name}}};"))
            max = System.Convert.ToInt64(await read.ExecuteScalarAsync(ct).ConfigureAwait(false), CultureInfo.InvariantCulture);

        var sql = context.Postgres
            ? $"SELECT setval(pg_get_serial_sequence('{qualified}', '{column}'), {max.ToString(CultureInfo.InvariantCulture)}, true);"
            : $"DBCC CHECKIDENT ('{qualified}', RESEED, {max.ToString(CultureInfo.InvariantCulture)}) WITH NO_INFOMSGS;";
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the table back from the target and serialises each row exactly as the exporter does (<see cref="SnapshotValues"/>, the payload marker for chunk data), giving the row count and the
    /// <c>rows_digest</c>. Order does not matter for the digest, except that the chunks of a payload are read in sequence to compute their offsets.
    /// </summary>
    private static async Task<(long Rows, string Digest)> ComputeTargetDigestAsync(ImportContext context, DbConnection connection, DbTransaction transaction, SnapshotModule module, SnapshotTable table, bool isChunks, CancellationToken ct)
    {
        var target = context.Target;
        var columns = table.Columns;
        var sql = $"SELECT {string.Join(", ", columns.Select(c => c.Name))} FROM {{table:{module.Name}.{table.Name}}}" + (isChunks ? " ORDER BY payload_id, sequence;" : ";");
        await using var command = target.Command(connection, transaction, sql);
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);

        var digest = new RowsDigest();
        var buffer = new ArrayBufferWriter<byte>(4096);
        var json = new Utf8JsonWriter(buffer, StateSnapshotWriter.RowOptions);
        var rowHash = new byte[32];
        var payloadIdColumn = isChunks ? columns.ToList().FindIndex(c => c.Name == "payload_id") : -1;
        Guid? currentPayload = null;
        long offset = 0;
        var rows = 0L;
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
                    var id = StateValues.ToGuid(reader.GetValue(payloadIdColumn));
                    if (currentPayload != id)
                    {
                        currentPayload = id;
                        offset = 0;
                    }

                    json.WriteStartObject();
                    json.WriteString("payload", id.ToString("D", CultureInfo.InvariantCulture));
                    json.WriteNumber("offset", offset);
                    json.WriteNumber("length", bytes.Length);
                    json.WriteEndObject();
                    offset += bytes.Length;
                }
                else
                    SnapshotValues.Write(json, columns[i].Type, raw);
            }

            json.WriteEndArray();
            json.Flush();
            SHA256.HashData(buffer.WrittenSpan, rowHash);
            digest.Add(rowHash);
            rows++;
        }

        return (rows, digest.Hex());
    }

    /// <summary>
    /// Gives the chunk rows their bytes from the payload files: one file open at a time (the chunk table is in payload order), each chunk read from where the previous one ended and hashed as it goes. A
    /// payload's size and SHA-256 are compared with the manifest when the next payload starts (or the table ends); a payload no chunk row refers to is hashed at the end.
    /// </summary>
    private sealed class PayloadRestorer(string root, IReadOnlyList<SnapshotPayloadFile> files, long progressBytes, Action<long> report)
    {
        private readonly Dictionary<Guid, SnapshotPayloadFile> _byId = files.ToDictionary(f => f.Id);
        private readonly HashSet<Guid> _verified = [];
        private SnapshotPayloadFile? _current;
        private FileStream? _stream;
        private IncrementalHash? _hash;
        private long _position;
        private long _sinceReport;

        /// <summary>Payload bytes read so far.</summary>
        public long Bytes { get; private set; }

        public async ValueTask<byte[]> ReadAsync(Guid id, long offset, int length, CancellationToken ct)
        {
            if (_current is null || _current.Id != id)
            {
                Finish();
                if (!_byId.TryGetValue(id, out var file))
                    throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"A chunk row refers to the payload {id}, which the manifest does not list.");
                if (_verified.Contains(id))
                    throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"The chunks of the payload {id} are not together in the table file ('{file.File}' was already read).");
                _current = file;
                _stream = new FileStream(StateSnapshotReader.ResolveFile(root, file.File, "the payload file"), FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
                _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                _position = 0;
            }

            if (offset != _position || length < 0 || offset + length > _current.Size)
                throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"A chunk of the payload file '{_current.File}' is at offset {offset}, length {length}, where the file is read up to {_position} and is {_current.Size} bytes.");

            var data = new byte[length];
            await _stream!.ReadExactlyAsync(data, ct).ConfigureAwait(false);
            _hash!.AppendData(data);
            _position += length;
            Bytes += length;
            _sinceReport += length;
            if (_sinceReport >= progressBytes)
            {
                _sinceReport = 0;
                report(Bytes);
            }

            return data;
        }

        /// <summary>Checks the payload in progress against the manifest and closes it.</summary>
        private void Finish()
        {
            if (_current is null) return;
            var file = _current;
            var actual = Convert.ToHexStringLower(_hash!.GetHashAndReset());
            var position = _position;
            _hash.Dispose();
            _stream!.Dispose();
            _current = null;
            _stream = null;
            _hash = null;
            if (position != file.Size)
                throw new SnapshotImportException(SnapshotImportRefusal.FileCorrupt, $"The chunk rows of the payload file '{file.File}' cover {position} bytes of its {file.Size}.");
            StateSnapshotReader.CheckHash(file.File, "the payload file", file.Sha256, actual);
            _verified.Add(file.Id);
        }

        public async Task FinishAllAsync(CancellationToken ct)
        {
            Finish();
            foreach (var file in files)
            {
                if (_verified.Contains(file.Id)) continue;
                await StateSnapshotReader.VerifyFileAsync(StateSnapshotReader.ResolveFile(root, file.File, "the payload file"), file.File, "the payload file", file.Sha256, file.Size, ct).ConfigureAwait(false);
                _verified.Add(file.Id);
            }
        }
    }
}
