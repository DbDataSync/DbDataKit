using System.Security.Cryptography;
using System.Text;
using DbDataKit.Secrets;
using DbDataKit.State.Snapshots;
using DbDataKit.State.Verification;

namespace DbDataKit.State.Engines.Snapshots;

/// <summary>An export: the snapshot's result and a summary a person can read.</summary>
public sealed record StateExportResult(SnapshotResult Snapshot, string Summary);

/// <summary>An import: the importer's result and a summary a person can read (the warnings are in it).</summary>
/// <param name="BulkBatches">Batches the engine's bulk loader took (0 when the batched INSERT did the work: SQLite, or a loader that declined).</param>
/// <param name="BulkRows">Rows in those batches.</param>
public sealed record StateImportResult(SnapshotImportResult Import, string Summary, int BulkBatches = 0, long BulkRows = 0);

/// <summary>How a verification reads and what it accepts.</summary>
public sealed record StateVerifyOptions
{
    /// <summary>Paging and key naming of the table comparison (see <see cref="VerifyOptions"/>).</summary>
    public VerifyOptions Compare { get; init; } = new();

    /// <summary>
    /// Read and hash the bytes of every payload in the database (against the snapshot's payload files, or against the other database's chunks). Off by default, as in <see cref="VerifyOptions.RehashPayloads"/>:
    /// the payload files and the stored hashes are checked either way.
    /// </summary>
    public bool RehashPayloads { get; init; }

    /// <summary>
    /// Tables (<c>module.table</c>) the importer reported as rounded to the engine's time precision (<see cref="SnapshotImportResult.RoundedTables"/>): a changed row or a digest that differs there is accepted
    /// and noted, not a failure, because PostgreSQL keeps microseconds and the other engines a tenth of one; counts, missing and extra rows are still failures. Against a <b>snapshot</b> that is all that can be done (its
    /// digests were made from the exporting engine's full-precision values); between two <b>databases</b> the times of both sides are compared at the microsecond and nothing is accepted.
    /// </summary>
    public IReadOnlyCollection<string> RoundedTables { get; init; } = [];
}

/// <summary>The outcome of a verification of any of the three forms.</summary>
/// <param name="Form"><c>databases</c>, <c>snapshot-and-database</c> or <c>snapshot</c>.</param>
/// <param name="Equal">True when nothing differs.</param>
/// <param name="Differences">Each difference as a sentence naming the table, file or payload.</param>
/// <param name="Notes">What was checked besides, and what was accepted and why.</param>
public sealed record StateVerifyResult(string Form, bool Equal, IReadOnlyList<string> Differences, IReadOnlyList<string> Notes, int Tables, long Rows, TimeSpan Elapsed)
{
    /// <summary>The result as text: the verdict first, then every difference and note.</summary>
    public string Summary
    {
        get
        {
            var text = new StringBuilder();
            text.AppendLine(Equal ? $"Verified: equal ({Tables} tables, {Rows} rows compared, {Elapsed.TotalSeconds:0.#} s)." : $"NOT EQUAL: {Differences.Count} difference{(Differences.Count == 1 ? "" : "s")} ({Tables} tables compared).");
            foreach (var difference in Differences) text.AppendLine($"  difference: {difference}");
            foreach (var note in Notes) text.AppendLine($"  {note}");
            return text.ToString().TrimEnd();
        }
    }

    public override string ToString() => Summary;
}

/// <summary>
/// The service layer under <c>state export</c>, <c>state import</c> and <c>state verify</c> (plan 29b slice X04a): it opens the databases through <see cref="StateOpener"/> without migrating them, resolves
/// the secret references of a connection string through the secret resolver it was given (the value is never written to a result, a message or a file), and runs the snapshot writer, importer and verifier.
/// A refusal is an exception naming what to do (<see cref="StateServiceException"/> for the service's own checks, and the writer's, reader's and importer's own typed exceptions for theirs); the verbs
/// translate those into exit codes.
/// </summary>
/// <param name="secrets">Resolves <c>secret:</c> and <c>env:</c> references in connection strings; null leaves a reference as it is (a connection string without references needs none).</param>
public sealed class StateSnapshotService(ISecretResolver? secrets = null, TimeProvider? time = null, string? appliedBy = null)
{
    private readonly ISecretResolver? _secrets = secrets;
    private readonly TimeProvider? _time = time;
    private readonly string? _appliedBy = appliedBy;

    /// <summary>
    /// Writes <paramref name="source"/> as a snapshot in <paramref name="directory"/> (empty or absent). The source is opened without migrating and has to be at this software's versions; a live instance, a
    /// held SQLite file or a directory that is not empty is a <see cref="SnapshotRefusedException"/>.
    /// </summary>
    public async Task<StateExportResult> ExportAsync(StateEndpoint source, IReadOnlyList<StateModule> modules, StateScope scope, string directory, SnapshotOptions? options, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(modules);
        var redactor = new Redactor(_secrets, source);
        var database = StateEndpoints.Open(source, modules, scope, StateEndpoints.Role.Read, _secrets, redactor, _appliedBy, _time);
        var result = await StateSnapshotWriter.WriteAsync(database, modules, scope, directory, options ?? SnapshotOptions.Default, progress, ct).ConfigureAwait(false);
        return new StateExportResult(result, ExportSummary(source, result));
    }

    /// <summary>
    /// Loads the snapshot in <paramref name="directory"/> into <paramref name="target"/>, which has to be an empty database (it is created by the import, never migrated first), using the engine's bulk loader
    /// (<see cref="SnapshotBulkLoaders"/>) unless <paramref name="options"/> names one. A target that holds modules is a <see cref="StateServiceException"/>; the rest are the importer's
    /// <see cref="SnapshotImportException"/>s.
    /// </summary>
    public async Task<StateImportResult> ImportAsync(string directory, StateEndpoint target, IReadOnlyList<StateModule> modules, StateScope scope, ImportOptions? options, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(modules);
        var redactor = new Redactor(_secrets, target);
        var database = StateEndpoints.Open(target, modules, scope, StateEndpoints.Role.Target, _secrets, redactor, _appliedBy, _time);
        return await ImportCoreAsync(directory, database, target, modules, scope, options, progress, redactor, ct).ConfigureAwait(false);
    }

    internal async Task<StateImportResult> ImportCoreAsync(string directory, StateDatabase database, StateEndpoint target, IReadOnlyList<StateModule> modules, StateScope scope, ImportOptions? options, IProgress<SnapshotProgress>? progress, Redactor redactor, CancellationToken ct)
    {
        try
        {
            var effective = SnapshotBulkLoaders.Apply(options ?? ImportOptions.Default, database);
            var result = await StateSnapshotImporter.ImportAsync(directory, database, modules, scope, effective, progress, ct).ConfigureAwait(false);
            var statistics = effective.BulkLoader as IBulkLoadStatistics;
            return new StateImportResult(result, ImportSummary(target, result, statistics), statistics?.Batches ?? 0, statistics?.Rows ?? 0);
        }
        catch (SnapshotImportException ex) when (ex.Message != redactor.Apply(ex.Message))
        {
            throw new SnapshotImportException(ex.Reason, redactor.Apply(ex.Message), ex.CompletedModules);
        }
    }

    // ---- verify: database against database

    /// <summary>Compares two databases (<paramref name="left"/> is what is expected): modules and versions, then every table's row count and digest in the logical values, so two engines holding the same data are equal.</summary>
    public async Task<StateVerifyResult> VerifyAsync(StateEndpoint left, StateEndpoint right, IReadOnlyList<StateModule> modules, StateScope scope, StateVerifyOptions? options, IProgress<VerificationProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var redactor = new Redactor(_secrets, left, right);
        var a = StateEndpoints.Open(left, modules, scope, StateEndpoints.Role.Read, _secrets, redactor, _appliedBy, _time);
        var b = StateEndpoints.Open(right, modules, scope, StateEndpoints.Role.Read, _secrets, redactor, _appliedBy, _time);
        return await VerifyDatabasesAsync(a, b, options, progress, ct).ConfigureAwait(false);
    }

    internal static Task<StateVerifyResult> VerifyDatabasesAsync(StateDatabase left, StateDatabase right, StateVerifyOptions? options, IProgress<VerificationProgress>? progress, CancellationToken ct)
    {
        options ??= new StateVerifyOptions();
        return Task.Run(() =>
        {
            var started = TimeProvider.System.GetTimestamp();
            // after a move between engines a time with a seventh digit was stored without it: both sides are compared at the microsecond, and nothing else is accepted
            var rounded = options.RoundedTables.Count > 0;
            var verifier = new StateVerifier(options.Compare with { RehashPayloads = options.RehashPayloads || options.Compare.RehashPayloads, TimeTicks = rounded ? 10 : options.Compare.TimeTicks });
            var report = verifier.Verify(left, right, ct, progress);
            var (differences, notes) = Accept(report.Differences, []);
            if (rounded) notes.Add($"Times were compared to the microsecond (the import rounded time values in {string.Join(", ", options.RoundedTables)} to the engine's precision); every other value is compared exactly.");
            if (options.RehashPayloads) notes.Add("Payload chunk bytes were read and hashed on both sides.");
            return new StateVerifyResult("databases", differences.Count == 0, differences, notes, report.Tables.Count, report.Tables.Sum(t => t.LeftRows), TimeProvider.System.GetElapsedTime(started));
        }, ct);
    }

    // ---- verify: database against snapshot

    /// <summary>
    /// Compares a database with a snapshot's manifest (<see cref="ManifestDigestSource"/>): modules, versions, row counts and digests of every table. The one table a manifest cannot offer, <c>payloads.chunk</c>
    /// (its rows hold offsets into the snapshot's payload files), is replaced by three checks: every payload file's size and SHA-256 against the manifest, each payload row's stored size and hash in the database
    /// against the manifest, and, with <see cref="StateVerifyOptions.RehashPayloads"/>, the bytes of each payload in the database hashed against the manifest.
    /// </summary>
    public async Task<StateVerifyResult> VerifyAsync(StateEndpoint database, string snapshotDirectory, IReadOnlyList<StateModule> modules, StateScope scope, StateVerifyOptions? options, IProgress<VerificationProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        var redactor = new Redactor(_secrets, database);
        var opened = StateEndpoints.Open(database, modules, scope, StateEndpoints.Role.Read, _secrets, redactor, _appliedBy, _time);
        return await VerifyAgainstSnapshotAsync(opened, snapshotDirectory, options, progress, ct).ConfigureAwait(false);
    }

    internal static async Task<StateVerifyResult> VerifyAgainstSnapshotAsync(StateDatabase database, string snapshotDirectory, StateVerifyOptions? options, IProgress<VerificationProgress>? progress, CancellationToken ct)
    {
        options ??= new StateVerifyOptions();
        var started = TimeProvider.System.GetTimestamp();
        var root = Path.GetFullPath(snapshotDirectory);
        var manifest = StateSnapshotReader.ReadManifest(root);
        var source = new ManifestDigestSource(root);

        var report = await Task.Run(() => new StateVerifier(options.Compare).Verify(
            new DatabaseDigestSource(database, $"the {database.Dialect.Engine} state database"), source, ct, progress), ct).ConfigureAwait(false);

        var notOffered = source.NotOffered.ToHashSet(StringComparer.Ordinal);
        var kept = report.Differences.Where(d => !(d.Kind == VerificationDifferenceKind.MissingTable && d.Table is not null && notOffered.Contains($"{ModuleName(d.Module)}.{d.Table}"))).ToList();
        var (differences, notes) = Accept(kept, options.RoundedTables);

        if (manifest.Payloads.Count > 0 || notOffered.Count > 0)
            await CheckPayloadsAsync(database, manifest, root, options, differences, notes, ct).ConfigureAwait(false);

        return new StateVerifyResult("snapshot-and-database", differences.Count == 0, differences, notes, report.Tables.Count, report.Tables.Sum(t => t.LeftRows), TimeProvider.System.GetElapsedTime(started));
    }

    private static async Task CheckPayloadsAsync(StateDatabase database, SnapshotManifest manifest, string root, StateVerifyOptions options, List<string> differences, List<string> notes, CancellationToken ct)
    {
        var byId = manifest.Payloads.ToDictionary(p => p.Id);
        var filesOk = 0;
        foreach (var payload in manifest.Payloads)
        {
            ct.ThrowIfCancellationRequested();
            var path = Path.GetFullPath(Path.Combine(root, payload.File.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            {
                differences.Add($"the payload file '{payload.File}' is missing from the snapshot");
                continue;
            }

            var (size, hash) = await HashFileAsync(path, ct).ConfigureAwait(false);
            if (size != payload.Size) differences.Add($"the payload file '{payload.File}' is {size} bytes and the manifest says {payload.Size}");
            else if (!string.Equals(hash, payload.Sha256, StringComparison.OrdinalIgnoreCase)) differences.Add($"the payload file '{payload.File}' has the SHA-256 {hash} and the manifest says {payload.Sha256}");
            else filesOk++;
        }

        if (database.Layout?.Has(PayloadStore.ModuleName) != true)
        {
            if (manifest.Payloads.Count > 0) differences.Add("the snapshot has payloads and the database has no 'payloads' module");
            return;
        }

        var seen = new HashSet<Guid>();
        var complete = new HashSet<Guid>();
        var rowsChecked = 0;
        await using (var connection = database.OpenConnection())
        {
            await using var command = database.Command(PayloadStore.ModuleName, connection, "SELECT id, size, sha256, state FROM {table:payload};");
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = StateValues.ToGuid(reader.GetValue(0));
                var size = Convert.ToInt64(reader.GetValue(1), System.Globalization.CultureInfo.InvariantCulture);
                var stored = reader.IsDBNull(2) ? null : reader.GetString(2);
                if (!byId.TryGetValue(id, out var payload))
                {
                    if (size > 0 && !reader.IsDBNull(2)) differences.Add($"the database has the payload {id:D} ({size} bytes) and the snapshot does not");
                    continue;
                }

                seen.Add(id);
                rowsChecked++;
                if (stored is null) continue;   // a payload whose writer never completed it has neither size nor hash to compare
                complete.Add(id);
                if (size != payload.Size) differences.Add($"the payload {id:D} is {size} bytes in the database and {payload.Size} in the snapshot");
                if (!string.Equals(stored, payload.Sha256, StringComparison.OrdinalIgnoreCase))
                    differences.Add($"the payload {id:D} has the stored SHA-256 {stored} in the database and {payload.Sha256} in the snapshot");
            }
        }

        foreach (var missing in byId.Keys.Where(id => !seen.Contains(id)))
            differences.Add($"the snapshot has the payload {missing:D} and the database has no row for it");

        var rehashed = 0;
        if (options.RehashPayloads)
        {
            var store = new PayloadStore(database);
            foreach (var payload in manifest.Payloads.Where(p => complete.Contains(p.Id)))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    using var stream = store.OpenRead(payload.Id);
                    using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[81920];
                    int read;
                    long total = 0;
                    while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                    {
                        sha.AppendData(buffer.AsSpan(0, read));
                        total += read;
                    }

                    var actual = Convert.ToHexStringLower(sha.GetHashAndReset());
                    if (total != payload.Size || !string.Equals(actual, payload.Sha256, StringComparison.OrdinalIgnoreCase))
                        differences.Add($"the bytes of the payload {payload.Id:D} in the database are {total} bytes with the SHA-256 {actual}; the snapshot has {payload.Size} bytes with {payload.Sha256}");
                    else rehashed++;
                }
                catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or IOException)
                {
                    differences.Add($"the payload {payload.Id:D} cannot be read from the database: {ex.Message}");
                }
            }
        }

        notes.Add($"payloads.chunk: not compared row by row (its rows hold offsets into the snapshot's files); {filesOk} of {manifest.Payloads.Count} payload files match the manifest's size and SHA-256, {rowsChecked} payload rows match its size and stored hash"
            + (options.RehashPayloads ? $", {rehashed} payloads hashed from the database's own chunks." : "."));
    }

    // ---- verify: the snapshot alone

    /// <summary>Re-hashes every table file and payload file of a snapshot against its manifest, counts the rows and checks the chunk markers, without any database.</summary>
    public async Task<StateVerifyResult> VerifyAsync(string snapshotDirectory, IProgress<SnapshotProgress>? progress, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(snapshotDirectory);
        var started = TimeProvider.System.GetTimestamp();
        var check = await SnapshotSelfCheck.CheckAsync(snapshotDirectory, progress, ct).ConfigureAwait(false);
        var notes = new List<string> { $"{check.Tables} table files and {check.PayloadFiles} payload files ({check.PayloadBytes} bytes) re-hashed against the manifest of '{Path.GetFullPath(snapshotDirectory)}'." };
        return new StateVerifyResult("snapshot", check.Intact, check.Problems, notes, check.Tables, check.Rows, TimeProvider.System.GetElapsedTime(started));
    }

    // ---- helpers

    private static string ModuleName(string key) => key.Contains('/') ? key[..key.IndexOf('/')] : key;

    private static (List<string> Differences, List<string> Notes) Accept(IReadOnlyList<VerificationDifference> found, IReadOnlyCollection<string> roundedTables)
    {
        var differences = new List<string>();
        var notes = new List<string>();
        foreach (var difference in found)
        {
            if (difference.Table is not null && roundedTables.Contains($"{ModuleName(difference.Module)}.{difference.Table}")
                && difference.Kind is VerificationDifferenceKind.ChangedRow or VerificationDifferenceKind.DigestMismatch)
                notes.Add($"accepted (time values were rounded to the engine's precision by the import): {difference.Message}");
            else
                differences.Add(difference.Message);
        }

        return (differences, notes);
    }

    private static async Task<(long Size, string Hash)> HashFileAsync(string path, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
        var buffer = new byte[81920];
        int read;
        while ((read = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            sha.AppendData(buffer.AsSpan(0, read));
            size += read;
        }

        return (size, Convert.ToHexStringLower(sha.GetHashAndReset()));
    }

    private static string ExportSummary(StateEndpoint source, SnapshotResult result)
    {
        var modules = result.Manifest.Modules;
        var text = new StringBuilder();
        text.AppendLine($"Exported {source.Describe()} to '{result.Directory}': {modules.Count} modules, {result.Tables} tables, {result.Rows} rows, {result.PayloadFiles} payload files ({result.PayloadBytes} bytes) in {result.Elapsed.TotalSeconds:0.#} s.");
        foreach (var module in modules) text.AppendLine($"  {module.Name}{(module.ScopeName is null ? "" : "/" + module.ScopeName)} v{module.Version}: {module.Tables.Sum(t => t.Rows)} rows in {module.Tables.Count} tables");
        text.Append("Secrets are in the snapshot as stored (encrypted); the keys that decrypt them are in the secret store and are not part of it.");
        return text.ToString();
    }

    private static string ImportSummary(StateEndpoint target, SnapshotImportResult result, IBulkLoadStatistics? loader)
    {
        var text = new StringBuilder();
        text.AppendLine($"Imported the snapshot into {target.Describe()}: {result.Modules.Count} modules, {result.Tables} tables, {result.Rows} rows, {result.PayloadFiles} payload files ({result.PayloadBytes} bytes) in {result.Elapsed.TotalSeconds:0.#} s{(result.Migrated ? "; migrated to this version" : "")}.");
        if (loader is { Batches: > 0 }) text.AppendLine($"  loaded with the engine's bulk path ({loader.Batches} batches, {loader.Rows} rows); the rest by batched INSERT.");
        foreach (var warning in result.Warnings) text.AppendLine($"  warning: {warning}");
        return text.ToString().TrimEnd();
    }
}
