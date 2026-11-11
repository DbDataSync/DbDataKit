using DbDataKit.Cli;
using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Cli;

/// <summary>
/// <c>state import --from &lt;directory&gt;</c> (plan 29b, X04): loads a snapshot into the state database the product's settings name, which has to be empty (a target that holds a module is refused naming it). The
/// modules are created at the snapshot's versions and then migrated to this software's (<c>--migrate-after false</c> leaves them at the snapshot's). A failed import empties the target again, so it can be retried.
/// It prints how to start the service afterwards.
/// </summary>
internal static class StateImportVerb
{
    public static readonly string Schema = DataSchemas.Object(
    [
        ("directory", DataSchemas.String), ("engine", DataSchemas.String), ("modules", DataSchemas.StringList), ("tables", DataSchemas.Integer), ("rows", DataSchemas.Integer),
        ("payload_files", DataSchemas.Integer), ("payload_bytes", DataSchemas.Integer), ("migrated", DataSchemas.Boolean), ("warnings", DataSchemas.StringList), ("summary", DataSchemas.String),
        ("next", DataSchemas.String), ("error", DataSchemas.String),
    ]);

    public static async Task<int> RunAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var usage = $"Usage: state import --from <directory> [--migrate-after true|false] {context.OptionsUsage}".TrimEnd();
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>(context.Options.Concat(["from", "migrate-after"])));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("from") is not { Length: > 0 } directory)
        {
            await output.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }

        var migrate = true;
        if (parsed.Get("migrate-after") is { } text && !bool.TryParse(text, out migrate))
        {
            await output.Error.WriteLineAsync($"--migrate-after is true or false, not '{text}'. {usage}");
            return CliExit.Usage;
        }

        var (opened, failure) = await StateSnapshotSupport.OpenAsync(context, parsed, output);
        if (opened is null) return failure;

        await output.Out.WriteLineAsync($"Importing '{Path.GetFullPath(directory)}' into {opened.Endpoint.Describe()} (the service must be stopped; the target has to be empty).");
        try
        {
            var options = ImportOptions.Default with { MigrateAfterImport = migrate, ResetOnFailure = true };
            var result = await opened.Service.ImportAsync(directory, opened.Endpoint, opened.Modules, context.Scope, options, StateSnapshotSupport.Lines(output.Out), output.Cancellation);
            var import = result.Import;
            var next = $"Start {context.Product} as usual: it opens this database. '{context.Program} state show' lists the modules and their versions, and '{context.Program} state verify --snapshot {directory} --db' compares the database with the snapshot again.";
            var data = output.Data;
            data["directory"] = Path.GetFullPath(directory);
            data["engine"] = opened.Session.Engine;
            data["modules"] = import.Modules.ToList();
            data["tables"] = import.Tables;
            data["rows"] = import.Rows;
            data["payload_files"] = import.PayloadFiles;
            data["payload_bytes"] = import.PayloadBytes;
            data["migrated"] = import.Migrated;
            data["warnings"] = import.Warnings.ToList();
            data["summary"] = result.Summary;
            data["next"] = next;
            await output.Out.WriteLineAsync(result.Summary);
            await output.Out.WriteLineAsync(next);
            return CliExit.Ok;
        }
        catch (Exception ex) when (StateSnapshotSupport.IsRefusal(ex)) { return await StateSnapshotSupport.RefuseAsync(output, ex); }
    }
}
