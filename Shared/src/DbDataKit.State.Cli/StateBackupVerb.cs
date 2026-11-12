using System.Globalization;
using DbDataKit.Cli;
using DbDataKit.State.Engines.Snapshots;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Cli;

/// <summary>
/// <c>state backup --to &lt;folder&gt;</c> and <c>state restore --from &lt;folder&gt;</c> (plan 22a GP20): the backup of the state <b>while the service runs</b>, and the restore into an empty database. A backup is the snapshot <c>state export</c> writes (<c>state-snapshot/1</c>: a manifest with every
/// module's version and each file's size, row count and SHA-256; no second format), written to <c>&lt;folder&gt;/state-&lt;UTC time&gt;</c> by way of a <c>.partial</c> folder that is renamed only after the snapshot was re-hashed against its own manifest, so a backup that exists is one that verified.
/// It reads in one transaction on SQLite and PostgreSQL (consistent as of its start while writers go on) and table by table on SQL Server (see <see cref="StateBackupJob"/>).
/// <para>
/// A restore verifies the backup before it touches the target (one changed byte is found and the target is left alone), takes the newest backup of a folder that holds several, and then imports it as <c>state import</c> does: the target has to be empty (a database that holds a module is refused naming it),
/// a backup of a newer program than this one is refused, and a failed import empties the target again.
/// </para>
/// </summary>
internal static class StateBackupVerb
{
    public static readonly string BackupSchema = DataSchemas.Object(
    [
        ("directory", DataSchemas.String), ("engine", DataSchemas.String), ("modules", DataSchemas.Integer), ("tables", DataSchemas.Integer), ("rows", DataSchemas.Integer), ("payload_files", DataSchemas.Integer),
        ("payload_bytes", DataSchemas.Integer), ("verified", DataSchemas.Boolean), ("summary", DataSchemas.String), ("error", DataSchemas.String),
    ]);

    public static async Task<int> BackupAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var usage = $"Usage: state backup --to <folder> {context.OptionsUsage}".TrimEnd();
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>(context.Options.Append("to")));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("to") is not { Length: > 0 } folder)
        {
            await output.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }

        var (opened, failure) = await StateSnapshotSupport.OpenAsync(context, parsed, output);
        if (opened is null) return failure;

        var root = Path.GetFullPath(folder);
        Directory.CreateDirectory(root);
        var name = $"state-{DateTimeOffset.UtcNow.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}";
        var final = Path.Combine(root, name);
        for (var i = 2; Directory.Exists(final); i++) final = Path.Combine(root, $"{name}-{i}");
        var partial = final + ".partial";
        if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true);

        await output.Out.WriteLineAsync($"Backing up {opened.Endpoint.Describe()} to '{final}' (the service may keep running).");
        try
        {
            var result = await opened.Service.ExportAsync(opened.Endpoint, opened.Modules, context.Scope, partial, new SnapshotOptions { ExportEvenIfInUse = true }, StateSnapshotSupport.Lines(output.Out), output.Cancellation);
            var check = await opened.Service.VerifyAsync(partial, null, output.Cancellation);
            if (!check.Equal)
            {
                Directory.Delete(partial, recursive: true);
                var sentence = "The backup just written does not match its own manifest, so it was removed and is not a backup: " + check.Summary;
                await output.Error.WriteLineAsync(sentence);
                output.Data["error"] = sentence;
                return StateExit.VerifyDiffers;
            }

            Directory.Move(partial, final);
            var data = output.Data;
            data["directory"] = final;
            data["engine"] = opened.Session.Engine;
            data["modules"] = result.Snapshot.Manifest.Modules.Count;
            data["tables"] = result.Snapshot.Tables;
            data["rows"] = result.Snapshot.Rows;
            data["payload_files"] = result.Snapshot.PayloadFiles;
            data["payload_bytes"] = result.Snapshot.PayloadBytes;
            data["verified"] = true;
            data["summary"] = result.Summary;
            await output.Out.WriteLineAsync(result.Summary);
            await output.Out.WriteLineAsync($"Verified against its manifest. Restore it into an empty database with '{context.StateCommand("restore")} --from {final}'.");
            return CliExit.Ok;
        }
        catch (Exception ex) when (StateSnapshotSupport.IsRefusal(ex))
        {
            try { if (Directory.Exists(partial)) Directory.Delete(partial, recursive: true); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { /* the next backup removes it */ }
            return await StateSnapshotSupport.RefuseAsync(output, ex);
        }
    }

    public static async Task<int> RestoreAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var usage = $"Usage: state restore --from <backup folder, or a folder of backups: the newest is used> [--migrate-after true|false] {context.OptionsUsage}".TrimEnd();
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>(context.Options.Concat(["from", "migrate-after"])));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("from") is not { Length: > 0 } from)
        {
            await output.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }

        var directory = Path.GetFullPath(from);
        if (!File.Exists(Path.Combine(directory, "manifest.json")))
        {
            var newest = StateBackupJob.List(directory).FirstOrDefault();
            if (newest is null)
            {
                await output.Error.WriteLineAsync($"'{directory}' is not a backup (it has no manifest.json) and holds none (a folder named state-<time> with a manifest). Check the path.");
                return StateExit.SnapshotInvalid;
            }

            await output.Out.WriteLineAsync($"Using the newest backup, '{newest}'.");
            directory = newest;
        }

        // the backup is checked before the target is touched: one changed byte stops here and leaves the database as it was
        try
        {
            var check = await new StateSnapshotService().VerifyAsync(directory, null, output.Cancellation);
            if (!check.Equal)
            {
                var sentence = "The backup does not match its own manifest, so nothing was restored: " + check.Summary;
                await output.Error.WriteLineAsync(sentence);
                output.Data["error"] = sentence;
                return StateExit.VerifyDiffers;
            }
        }
        catch (Exception ex) when (StateSnapshotSupport.IsRefusal(ex)) { return await StateSnapshotSupport.RefuseAsync(output, ex); }

        await output.Out.WriteLineAsync("The backup matches its manifest.");
        var rewritten = args.ToList();
        var at = rewritten.FindIndex(a => a is "--from");
        if (at >= 0 && at + 1 < rewritten.Count) rewritten[at + 1] = directory;
        else rewritten = [.. rewritten.Where(a => !a.StartsWith("--from=", StringComparison.Ordinal)), "--from", directory];
        return await StateImportVerb.RunAsync(context, [.. rewritten], output);
    }
}
