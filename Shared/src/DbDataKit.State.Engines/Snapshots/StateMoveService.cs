using System.Text;
using DbDataKit.Secrets;
using DbDataKit.State.Snapshots;
using DbDataKit.State.Verification;

namespace DbDataKit.State.Engines.Snapshots;

/// <summary>The steps of a move, in order.</summary>
public enum MoveStep
{
    /// <summary>Checks before anything is written: both databases open, the target is empty, the settings can be written.</summary>
    Preflight,

    /// <summary>The source is exported to the temporary snapshot (refused while it is in use).</summary>
    Export,

    /// <summary>The snapshot is imported into the empty target.</summary>
    Import,

    /// <summary>The target is verified against the snapshot's manifest and payload files.</summary>
    VerifySnapshot,

    /// <summary>The target is verified against the source database.</summary>
    VerifyDatabases,

    /// <summary>A sample of the copied secrets is decrypted with the keys this process has (plan 29b C04); only when the product named samples (<see cref="MoveOptions.SecretSamples"/>).</summary>
    CheckSecrets,

    /// <summary>The new settings are written.</summary>
    WriteSettings,

    /// <summary>The temporary snapshot is removed (when asked to).</summary>
    Cleanup,

    Done,
}

/// <summary>A line of progress: which step, and what it says; the snapshot or verification detail is there when the step has it.</summary>
public sealed record MoveProgress(MoveStep Step, string Message, SnapshotProgress? Snapshot = null, VerificationProgress? Verification = null);

/// <summary>
/// A column of the state that holds a value encrypted with a key that is not in the snapshot (it lives in the secret store): a signing key's private half, a group's secret. A move decrypts up to
/// <see cref="Sample"/> values of it in the target before it writes the settings, so a target whose service could not read them is found while the old settings are still in force (plan 29b C04).
/// </summary>
/// <param name="Module">The module that owns the table, as the registry names it.</param>
/// <param name="Table">The logical table name.</param>
/// <param name="Column">The column that holds the encrypted value (text).</param>
/// <param name="CanDecrypt">True when this process's keys decrypt the value as stored; false or throwing is a failure. It never receives anything but ciphertext, and must not put the value in an exception.</param>
/// <param name="Sample">How many rows to try.</param>
public sealed record StateSecretSample(string Module, string Table, string Column, Func<string, bool> CanDecrypt, int Sample = 5);

/// <summary>What a move may be asked to do differently.</summary>
public sealed record MoveOptions
{
    /// <summary>Where the temporary snapshot goes (empty or absent). Default: a new directory beside the settings file, <c>state-move-&lt;time&gt;-&lt;id&gt;</c>, readable by its owner only on Unix (it holds the state in clear, secrets as stored).</summary>
    public string? SnapshotDirectory { get; init; }

    /// <summary>Remove the temporary snapshot when the move succeeded. Default true. A failed move always keeps it, for inspection.</summary>
    public bool DeleteSnapshotOnSuccess { get; init; } = true;

    public SnapshotOptions Export { get; init; } = SnapshotOptions.Default;

    public ImportOptions Import { get; init; } = ImportOptions.Default;

    public VerifyOptions Verify { get; init; } = new();

    /// <summary>
    /// Hash the bytes of every payload in the target (against the snapshot's files) and on both sides of the database comparison. Default true: a move is verified completely; turn it off for a very large payload store when
    /// the stored hashes and file hashes are enough.
    /// </summary>
    public bool RehashPayloads { get; init; } = true;

    /// <summary>When an import fails, drop what it created so the target is empty again (it was empty before, or the move would have refused). Default true. A failed verification leaves the target as it is.</summary>
    public bool ResetTargetOnImportFailure { get; init; } = true;

    /// <summary>A seam for tests: runs on the freshly imported target before it is verified (a test corrupts a row here to see the move refuse).</summary>
    public Func<StateDatabase, CancellationToken, Task>? AfterImport { get; init; }

    /// <summary>The encrypted columns to try to decrypt in the target before the settings are written; empty: none is checked (a product that keeps no encrypted value in its state, or that has not named them).</summary>
    public IReadOnlyList<StateSecretSample> SecretSamples { get; init; } = [];

    /// <summary>The command that restarts this product's service (<c>dbdatakit service restart</c>), named in the instruction after a move; null: the instruction says to restart the service without naming a command.</summary>
    public string? RestartCommand { get; init; }

    /// <summary>The name added to the settings file for the copy of its previous content (<c>.before-state-move</c>).</summary>
    public string BackupSuffix { get; init; } = ".before-state-move";
}

/// <summary>What <see cref="StateMoveService.Preflight"/> found fit: the databases as a sentence names them.</summary>
public sealed record MovePreflight(string Source, string Target);

/// <summary>A move that did not finish. The settings file is unchanged; <see cref="SnapshotDirectory"/> is the temporary snapshot, kept when it holds anything.</summary>
public sealed class StateMoveException(MoveStep step, string message, string? snapshotDirectory, Exception? inner = null) : InvalidOperationException(message, inner)
{
    public MoveStep Step { get; } = step;

    /// <summary>The kept snapshot, or null when none was written.</summary>
    public string? SnapshotDirectory { get; } = snapshotDirectory;

    /// <summary>Always false: no failure after the settings were written is possible (the write is the last step that can fail), and none before it changes them.</summary>
    public bool SettingsChanged => false;
}

/// <summary>A move that succeeded.</summary>
/// <param name="RestartInstruction">What the operator does next: restart the service so it opens the new database.</param>
/// <param name="WayBack">How to return to the old database.</param>
/// <param name="BackupFile">The copy of the previous settings file, or null when there was none.</param>
/// <param name="SnapshotDirectory">The temporary snapshot when it was kept, else null.</param>
public sealed record MoveResult(
    string SettingsFile, string? BackupFile, string? SnapshotDirectory, SnapshotResult Export, SnapshotImportResult Import, StateVerifyResult VerifiedAgainstSnapshot, StateVerifyResult VerifiedAgainstSource,
    string RestartInstruction, string WayBack, string Summary);

/// <summary>
/// Moves a product's state from one database to another (plan 29b decisions M1 and M5, the core of slice C04): export the source to a temporary snapshot (refused while a live instance uses it), import into the
/// empty target, verify the target against the snapshot and against the source, and <b>only then</b> write the new <c>state</c> settings (atomically, through <see cref="StateSettingsWriter"/>). The old database is never
/// changed, so going back is restoring the previous settings, which are kept as <c>&lt;settings file&gt;.before-state-move</c>. A failure at any step leaves the settings file as it was and the temporary snapshot where it is.
/// </summary>
public sealed class StateMoveService(ISecretResolver? secrets = null, TimeProvider? time = null, string? appliedBy = null)
{
    private readonly ISecretResolver? _secrets = secrets;
    private readonly TimeProvider? _time = time;
    private readonly string? _appliedBy = appliedBy;
    private readonly StateSnapshotService _snapshots = new(secrets, time, appliedBy);

    public async Task<MoveResult> MoveAsync(
        StateEndpoint from, StateEndpoint to, string settingsFile, IReadOnlyList<StateModule> modules, StateScope scope, MoveOptions? options, IProgress<MoveProgress>? progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFile);
        ArgumentNullException.ThrowIfNull(modules);
        options ??= new MoveOptions();
        var redactor = new Redactor(_secrets, from, to);
        var settingsPath = Path.GetFullPath(settingsFile);
        string? snapshot = null;
        var step = MoveStep.Preflight;

        try
        {
            // ---- preflight: everything that can be known before the first write
            Report(progress, step, "Checking the source, the target and the settings file.");
            var (source, target) = PreflightCore(from, to, settingsPath, modules, scope, redactor);
            snapshot = PrepareSnapshotDirectory(settingsPath, options);

            // ---- export (the writer refuses a source a live instance uses, naming it)
            step = MoveStep.Export;
            Report(progress, step, $"Exporting {from.Describe()} to '{snapshot}'.");
            var exported = await StateSnapshotWriter.WriteAsync(source, modules, scope, snapshot, options.Export, Forward(progress, step), ct).ConfigureAwait(false);

            // ---- import into the empty target
            step = MoveStep.Import;
            Report(progress, step, $"Importing the snapshot into {to.Describe()}.");
            var imported = await _snapshots.ImportCoreAsync(snapshot, target, to, modules, scope, options.Import with { ResetOnFailure = options.Import.ResetOnFailure || options.ResetTargetOnImportFailure }, Forward(progress, step), redactor, ct).ConfigureAwait(false);
            if (options.AfterImport is not null) await options.AfterImport(target, ct).ConfigureAwait(false);

            // ---- verify: the target against the snapshot, then against the source database
            var verify = new StateVerifyOptions { Compare = options.Verify, RehashPayloads = options.RehashPayloads, RoundedTables = imported.Import.RoundedTables };
            step = MoveStep.VerifySnapshot;
            Report(progress, step, "Verifying the target against the snapshot (row counts, digests, payload files).");
            var againstSnapshot = await StateSnapshotService.VerifyAgainstSnapshotAsync(target, snapshot, verify, ForwardVerification(progress, step), ct).ConfigureAwait(false);
            if (!againstSnapshot.Equal) throw new StateServiceException(StateServiceRefusal.SnapshotNotIntact, "The target does not match the snapshot.\n" + againstSnapshot.Summary);

            step = MoveStep.VerifyDatabases;
            Report(progress, step, "Verifying the target against the source database.");
            var againstSource = await StateSnapshotService.VerifyDatabasesAsync(source, target, verify, ForwardVerification(progress, step), ct).ConfigureAwait(false);
            if (!againstSource.Equal) throw new StateServiceException(StateServiceRefusal.SnapshotNotIntact, "The target does not match the source database.\n" + againstSource.Summary);

            // ---- a sample of the secrets, decrypted with the keys this process has
            if (options.SecretSamples.Count > 0)
            {
                step = MoveStep.CheckSecrets;
                Report(progress, step, "Decrypting a sample of the copied secrets in the target.");
                CheckSecrets(target, options.SecretSamples);
            }

            // ---- only now: the settings
            step = MoveStep.WriteSettings;
            Report(progress, step, $"Writing the new state settings to '{settingsPath}'.");
            var backup = WriteSettings(settingsPath, to, options);

            // ---- the snapshot, unless it is to be kept
            step = MoveStep.Cleanup;
            string? kept = snapshot;
            if (options.DeleteSnapshotOnSuccess)
            {
                try
                {
                    Directory.Delete(snapshot, recursive: true);
                    kept = null;
                    Report(progress, step, "Removed the temporary snapshot.");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Report(progress, step, $"The temporary snapshot '{snapshot}' could not be removed ({ex.Message}); remove it by hand.");
                }
            }

            var restart = $"Restart the service now{(options.RestartCommand is { Length: > 0 } command ? $" ('{command}')" : "")} so that it opens the new state database ({to.Describe()}). Until it is restarted it keeps using the old one.";
            var wayBack = WayBack(settingsPath, backup, from);
            var result = new MoveResult(settingsPath, backup, kept, exported, imported.Import, againstSnapshot, againstSource, restart, wayBack, "");
            result = result with { Summary = Summary(result, from, to, againstSource) };
            Report(progress, MoveStep.Done, "The move is complete.");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var kept = snapshot is not null && Directory.Exists(snapshot) && Directory.EnumerateFileSystemEntries(snapshot).Any() ? snapshot : null;
            if (snapshot is not null && kept is null) TryDeleteEmpty(snapshot);
            var text = new StringBuilder();
            text.Append($"The move stopped at the step '{step}': {redactor.Apply(ex.Message)}");
            text.Append($"\nThe settings file '{settingsPath}' was not changed and the old database was not touched.");
            if (kept is not null) text.Append($" The temporary snapshot was kept for inspection in '{kept}'.");
            if (step is MoveStep.VerifySnapshot or MoveStep.VerifyDatabases) text.Append($" The target holds the imported data; empty or recreate it before trying again.");
            throw new StateMoveException(step, text.ToString(), kept, ex);
        }
    }

    /// <summary>
    /// The checks of a move that need no write: the source and the target are not the same database, the settings file can take the new section, the source opens and is at this software's versions, the target opens (its login works)
    /// and is empty. Nothing is created, copied or changed. Raises <see cref="StateServiceException"/> naming the fix; it never holds a password.
    /// </summary>
    public MovePreflight Preflight(StateEndpoint from, StateEndpoint to, string settingsFile, IReadOnlyList<StateModule> modules, StateScope scope)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsFile);
        var redactor = new Redactor(_secrets, from, to);
        try { PreflightCore(from, to, Path.GetFullPath(settingsFile), modules, scope, redactor); }
        catch (StateServiceException ex) { throw new StateServiceException(ex.Reason, redactor.Apply(ex.Message)); }
        return new MovePreflight(from.Describe(), to.Describe());
    }

    private (StateDatabase Source, StateDatabase Target) PreflightCore(StateEndpoint from, StateEndpoint to, string settingsPath, IReadOnlyList<StateModule> modules, StateScope scope, Redactor redactor)
    {
        if (StateEndpoints.Same(from, to)) throw new StateServiceException(StateServiceRefusal.SameDatabase, "The source and the target are the same database; name another target.");
        CheckSettingsCanBeWritten(settingsPath, to);
        var source = StateEndpoints.Open(from, modules, scope, StateEndpoints.Role.Read, _secrets, redactor, _appliedBy, _time);
        var target = StateEndpoints.Open(to, modules, scope, StateEndpoints.Role.Target, _secrets, redactor, _appliedBy, _time);
        return (source, target);
    }

    /// <summary>Reads the sampled rows of every named column from the target and decrypts each; the first that fails stops the move naming the table and the key of the row (never the value).</summary>
    private static void CheckSecrets(StateDatabase target, IReadOnlyList<StateSecretSample> samples)
    {
        foreach (var sample in samples)
        {
            if (target.Layout is null || !target.Layout.Has(sample.Module)) continue;   // a product that does not have the module has nothing to decrypt
            var table = target.Layout.Table(sample.Module, sample.Table);
            var values = new List<string>();
            using (var connection = target.OpenConnection())
            {
                using var command = target.Command(connection, $"SELECT {sample.Column} FROM {table} WHERE {sample.Column} IS NOT NULL");
                using var reader = command.ExecuteReader();
                while (values.Count < sample.Sample && reader.Read()) values.Add(reader.GetString(0));
            }

            for (var i = 0; i < values.Count; i++)
            {
                bool ok;
                try { ok = sample.CanDecrypt(values[i]); }
                catch (Exception ex) when (ex is not OperationCanceledException) { ok = false; }
                if (!ok)
                    throw new StateServiceException(StateServiceRefusal.SecretsUnreadable,
                        $"The target holds {values.Count} sampled value(s) of {sample.Module}.{sample.Table}.{sample.Column} ({table}), and the keys this process has cannot decrypt value {i + 1} of them. The keys live in the secret store, not in the snapshot: give the target's service the same secret store (or restore the key that protected these values), then move again. The settings were not changed.");
            }
        }
    }

    private static void Report(IProgress<MoveProgress>? progress, MoveStep step, string message) => progress?.Report(new MoveProgress(step, message));

    private static IProgress<SnapshotProgress>? Forward(IProgress<MoveProgress>? progress, MoveStep step) =>
        progress is null ? null : new InlineProgress<SnapshotProgress>(p => progress.Report(new MoveProgress(step, DescribeSnapshotProgress(p), p)));

    private static IProgress<VerificationProgress>? ForwardVerification(IProgress<MoveProgress>? progress, MoveStep step) =>
        progress is null ? null : new InlineProgress<VerificationProgress>(p => progress.Report(new MoveProgress(step, $"{p.Module}.{p.Table} ({p.Step}, {p.TablesDone}/{p.TablesTotal} tables)", Verification: p)));

    private static string DescribeSnapshotProgress(SnapshotProgress p) => p.Phase switch
    {
        "table-start" => $"{p.Module}.{p.Table}: started",
        "table-done" => $"{p.Module}.{p.Table}: done, {p.Rows} rows so far",
        "module-done" => $"module {p.Module}: done, {p.Rows} rows so far",
        "done" => $"done: {p.Rows} rows, {p.PayloadBytes} payload bytes",
        _ => $"{p.Module}.{p.Table}: {p.Phase}, {p.Rows} rows, {p.PayloadBytes} payload bytes",
    };

    /// <summary>Writes the new section to a copy of the settings file first, so a setting the writer refuses stops the move before an hour of copying and not after it.</summary>
    private static void CheckSettingsCanBeWritten(string settingsPath, StateEndpoint to)
    {
        var directory = Path.GetDirectoryName(settingsPath)!;
        if (!Directory.Exists(directory)) throw new StateServiceException(StateServiceRefusal.SettingsInvalid, $"The settings file's directory '{directory}' does not exist.");
        var scratch = Path.Combine(directory, $".state-move-check-{Guid.NewGuid():N}.yaml");
        try
        {
            if (File.Exists(settingsPath)) File.Copy(settingsPath, scratch);
            try { new StateSettingsWriter().Write(scratch, to.Settings); }
            catch (InvalidOperationException ex) { throw new StateServiceException(StateServiceRefusal.SettingsInvalid, ex.Message.Replace(scratch, settingsPath, StringComparison.Ordinal)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new StateServiceException(StateServiceRefusal.SettingsInvalid, $"The directory '{directory}' cannot be written: {ex.Message}");
            }
        }
        finally
        {
            if (File.Exists(scratch)) File.Delete(scratch);
        }
    }

    private string PrepareSnapshotDirectory(string settingsPath, MoveOptions options)
    {
        var directory = options.SnapshotDirectory is { Length: > 0 } given
            ? Path.GetFullPath(given)
            : Path.Combine(Path.GetDirectoryName(settingsPath)!, $"state-move-{(_time ?? TimeProvider.System).GetUtcNow():yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}");
        if (File.Exists(directory) || (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory).Any()))
            throw new StateServiceException(StateServiceRefusal.SettingsInvalid, $"The snapshot directory '{directory}' is not empty; name an empty or absent directory.");
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return directory;
    }

    private static string? WriteSettings(string settingsPath, StateEndpoint to, MoveOptions options)
    {
        string? backup = null;
        if (File.Exists(settingsPath))
        {
            backup = settingsPath + options.BackupSuffix;
            for (var i = 2; File.Exists(backup); i++) backup = $"{settingsPath}{options.BackupSuffix}.{i}";
            File.Copy(settingsPath, backup);
        }

        try
        {
            new StateSettingsWriter().Write(settingsPath, to.Settings);
        }
        catch
        {
            if (backup is not null) File.Delete(backup);
            throw;
        }

        return backup;
    }

    private static string WayBack(string settingsPath, string? backup, StateEndpoint from) =>
        (backup is null
            ? $"To go back: stop the service and delete '{settingsPath}' (it did not exist before the move), then start it."
            : $"To go back: stop the service, copy '{backup}' over '{settingsPath}', then start it.")
        + $" The old database ({from.Describe()}) was not changed and still holds the state as it was when the move began; anything the service writes to the new database after the move is not in it. Remove the old database yourself when you no longer need it.";

    private static void TryDeleteEmpty(string directory)
    {
        try { if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an empty folder left behind is untidy and nothing else */ }
    }

    private static string Summary(MoveResult result, StateEndpoint from, StateEndpoint to, StateVerifyResult againstSource)
    {
        var text = new StringBuilder();
        text.AppendLine($"Moved the state from {from.Describe()} to {to.Describe()}: {result.Import.Modules.Count} modules, {result.Import.Tables} tables, {result.Import.Rows} rows, {result.Import.PayloadFiles} payload files ({result.Import.PayloadBytes} bytes).");
        text.AppendLine("Verified against the snapshot: " + result.VerifiedAgainstSnapshot.Summary.Split('\n')[0]);
        text.AppendLine("Verified against the old database: " + againstSource.Summary.Split('\n')[0]);
        foreach (var warning in result.Import.Warnings) text.AppendLine($"Warning: {warning}");
        text.AppendLine($"The state settings in '{result.SettingsFile}' now name the new database" + (result.BackupFile is null ? "." : $"; the previous file is kept as '{result.BackupFile}'."));
        if (result.SnapshotDirectory is not null) text.AppendLine($"The temporary snapshot is in '{result.SnapshotDirectory}'.");
        text.AppendLine(result.RestartInstruction);
        text.Append(result.WayBack);
        return text.ToString();
    }
}
