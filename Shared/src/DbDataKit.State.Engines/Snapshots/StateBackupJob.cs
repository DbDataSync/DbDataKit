using System.Globalization;
using System.Text.RegularExpressions;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Engines.Snapshots;

/// <summary>Where scheduled snapshots go and how many are kept (plan 29b U03).</summary>
/// <param name="Directory">The folder that holds one sub-folder per backup, <c>state-&lt;UTC time&gt;</c>. It is made when missing and should be a disk other than the state database's.</param>
/// <param name="Keep">How many backups stay: the newest ones; older ones this job wrote are removed after a successful new one. At least 1.</param>
/// <param name="Verify">Check the written snapshot against its own manifest (every file's hash, size and row count) before it counts as a backup.</param>
public sealed record StateBackupSettings(string Directory, int Keep = 7, bool Verify = true);

/// <summary>What one backup did.</summary>
/// <param name="Directory">The new backup's folder.</param>
/// <param name="Pruned">The folders of older backups that were removed.</param>
public sealed record StateBackupResult(string Directory, long Rows, int Tables, int PayloadFiles, long PayloadBytes, bool Verified, IReadOnlyList<string> Pruned, TimeSpan Elapsed);

/// <summary>
/// A snapshot of the state written to a folder on a schedule, kept and checked (plan 29b U03): the system task <c>state-backup</c> of the console's embedded runner runs it. The snapshot is the one <c>state export</c> writes, so a restore is
/// <c>state import --from &lt;folder&gt;</c> into an empty database (and <c>state verify --snapshot</c> checks one by hand). It is written to a folder named <c>state-&lt;time&gt;.partial</c> and renamed only after it was
/// written and verified, so a failed or interrupted backup is never mistaken for one, and only then are older backups pruned: a run that fails prunes nothing. Only folders this job named (and that hold a manifest) are ever removed.
/// <para>
/// The job runs in the process that uses the database, so it exports even though that process is a live user of it (<see cref="SnapshotOptions.ExportEvenIfInUse"/>): on SQLite and PostgreSQL the export reads in one transaction and
/// is consistent as of its start; on SQL Server it reads committed rows table by table, so a row written while it runs can be in one table and not yet in another. Secrets are in it as stored (encrypted), the keys that decrypt them are not.
/// </para>
/// </summary>
public sealed partial class StateBackupJob(StateDatabase state, IReadOnlyList<StateModule> modules, StateScope scope, StateBackupSettings settings, TimeProvider? time = null)
{
    [GeneratedRegex(@"^state-\d{8}T\d{6}Z(-\d+)?$")]
    private static partial Regex BackupName { get; }

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>The folders of the backups in <paramref name="directory"/>, newest first: the ones this job's naming and a manifest say they are.</summary>
    public static IReadOnlyList<string> List(string directory) =>
        !System.IO.Directory.Exists(directory) ? [] :
        [.. System.IO.Directory.EnumerateDirectories(directory).Where(d => BackupName.IsMatch(Path.GetFileName(d)) && File.Exists(Path.Combine(d, "manifest.json"))).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal)];

    public async Task<StateBackupResult> RunAsync(CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(settings.Keep, 1);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Directory);
        var started = _time.GetTimestamp();
        var root = Path.GetFullPath(settings.Directory);
        System.IO.Directory.CreateDirectory(root);
        var name = $"state-{_time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
        var final = Path.Combine(root, name);
        for (var i = 2; System.IO.Directory.Exists(final); i++) final = Path.Combine(root, $"{name}-{i}");   // two backups in one second
        var partial = final + ".partial";
        if (System.IO.Directory.Exists(partial)) System.IO.Directory.Delete(partial, recursive: true);   // what an earlier run left when it was killed

        SnapshotResult written;
        var verified = false;
        try
        {
            written = await StateSnapshotWriter.WriteAsync(state, modules, scope, partial, new SnapshotOptions { ExportEvenIfInUse = true }, null, ct).ConfigureAwait(false);
            if (settings.Verify)
            {
                var check = await new StateSnapshotService().VerifyAsync(partial, null, ct).ConfigureAwait(false);
                if (!check.Equal) throw new InvalidOperationException("The snapshot just written does not match its own manifest, so it is not kept as a backup: " + check.Summary);
                verified = true;
            }

            System.IO.Directory.Move(partial, final);
        }
        catch
        {
            try { if (System.IO.Directory.Exists(partial)) System.IO.Directory.Delete(partial, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* left for the next run to remove */ }
            throw;
        }

        var pruned = new List<string>();
        foreach (var old in List(root).Skip(settings.Keep))
        {
            try
            {
                System.IO.Directory.Delete(old, recursive: true);
                pruned.Add(old);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* an old backup that cannot be removed now is removed by the next run */ }
        }

        return new StateBackupResult(final, written.Rows, written.Tables, written.PayloadFiles, written.PayloadBytes, verified, pruned, _time.GetElapsedTime(started));
    }
}
