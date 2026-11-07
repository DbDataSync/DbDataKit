using DbDataKit.Cli;
using DbDataKit.State.Engines.Snapshots;

namespace DbDataKit.State.Cli;

/// <summary>
/// <c>state verify</c> (plan 29b, X04): three ways to compare, each exiting 0 when everything is equal and <see cref="StateExit.VerifyDiffers"/> when not, with the tables and the first differing rows named.
/// <list type="bullet">
/// <item><c>--snapshot &lt;dir&gt;</c>: the snapshot against its own manifest (every file re-hashed, rows counted); no database, no settings needed.</item>
/// <item><c>--snapshot &lt;dir&gt; --db</c> (or <c>--against &lt;snapshot directory&gt;</c>): the product's current state against the snapshot.</item>
/// <item><c>--other-engine/--other-connection/--other-sqlite/--other-schema/--other-prefix</c> (or <c>--against &lt;settings file&gt;</c>, whose <c>state:</c> section names the other side): the current state against another database,
/// named as the <c>state</c> section names one, its connection with secret references only.</item>
/// </list>
/// <c>--rehash-payloads</c> also reads every payload's bytes from the database and hashes them (slow for large payloads). The service should be stopped, or the comparison may see a write in between.
/// </summary>
internal static class StateVerifyVerb
{
    public static readonly string Schema = DataSchemas.Object(
    [
        ("form", DataSchemas.String), ("equal", DataSchemas.Boolean), ("tables", DataSchemas.Integer), ("rows", DataSchemas.Integer),
        ("differences", DataSchemas.StringList), ("notes", DataSchemas.StringList), ("elapsed_seconds", DataSchemas.String), ("error", DataSchemas.String),
    ], "form", "equal");

    private static readonly string[] OtherOptions = ["other-engine", "other-connection", "other-sqlite", "other-schema", "other-prefix"];

    public static async Task<int> RunAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var usage = $"Usage: state verify --snapshot <directory> [--db] | --against <snapshot directory or settings file> | --other-engine sqlite|mssql|postgres [--other-connection <connection string with secret references>] [--other-sqlite <file>] [--other-schema <schema>] [--other-prefix <prefix>]  [--rehash-payloads] {context.OptionsUsage}".TrimEnd();
        var parsed = CliArgs.Parse(args, new HashSet<string> { "db", "rehash-payloads" }, new HashSet<string>(context.Options.Concat(["snapshot", "against"]).Concat(OtherOptions)));
        var snapshot = parsed.Get("snapshot");
        var against = parsed.Get("against");
        var hasOther = OtherOptions.Any(parsed.Has);
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || (snapshot is null && against is null && !hasOther)
            || (snapshot is not null && (against is not null || hasOther)) || (against is not null && hasOther) || (parsed.Has("db") && snapshot is null))
        {
            await output.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }

        var options = new StateVerifyOptions { RehashPayloads = parsed.Has("rehash-payloads") };

        // an --against that is a directory with a manifest is the snapshot form; a file is a settings file
        if (against is not null && Directory.Exists(against)) { snapshot = against; against = null; }
        else if (against is not null && !File.Exists(against))
        {
            await output.Error.WriteLineAsync($"--against '{against}' is neither a snapshot directory nor a settings file; check the path.");
            return StateExit.SnapshotInvalid;
        }

        try
        {
            StateVerifyResult result;
            if (snapshot is not null && !parsed.Has("db") && against is null)
            {
                // the snapshot alone: no database is opened, so no settings are read
                await output.Out.WriteLineAsync($"Checking the snapshot '{Path.GetFullPath(snapshot)}' against its own manifest (no database).");
                result = await new StateSnapshotService().VerifyAsync(snapshot, StateSnapshotSupport.Lines(output.Out), output.Cancellation);
            }
            else
            {
                var (opened, failure) = await StateSnapshotSupport.OpenAsync(context, parsed, output);
                if (opened is null) return failure;
                var progress = StateSnapshotSupport.VerifyLines(output.Out);
                if (snapshot is not null)
                {
                    await output.Out.WriteLineAsync($"Comparing {opened.Endpoint.Describe()} with the snapshot '{Path.GetFullPath(snapshot)}'.");
                    result = await opened.Service.VerifyAsync(opened.Endpoint, snapshot, opened.Modules, context.Scope, options, progress, output.Cancellation);
                }
                else
                {
                    StateEndpoint? other;
                    string? problem;
                    if (against is not null) other = FromSettingsFile(against, out problem);
                    else other = StateSnapshotSupport.Other(parsed, "other-", opened.Session.Settings, null, out problem);
                    if (other is null)
                    {
                        await output.Error.WriteLineAsync(problem!);
                        return problem!.Contains("secret reference", StringComparison.Ordinal) || problem.Contains("--other-", StringComparison.Ordinal) ? CliExit.Usage : StateExit.Settings;
                    }

                    await output.Out.WriteLineAsync($"Comparing {opened.Endpoint.Describe()} with {other.Describe()}.");
                    result = await opened.Service.VerifyAsync(opened.Endpoint, other, opened.Modules, context.Scope, options, progress, output.Cancellation);
                }
            }

            var data = output.Data;
            data["form"] = result.Form;
            data["equal"] = result.Equal;
            data["tables"] = result.Tables;
            data["rows"] = result.Rows;
            data["differences"] = result.Differences.ToList();
            data["notes"] = result.Notes.ToList();
            data["elapsed_seconds"] = result.Elapsed.TotalSeconds.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            await output.Out.WriteLineAsync(result.Summary);
            if (result.Equal) return CliExit.Ok;
            await output.Error.WriteLineAsync($"The comparison found {result.Differences.Count} difference(s); the tables and rows are named above. Do not start the product on a database that should be equal until they are understood.");
            return StateExit.VerifyDiffers;
        }
        catch (Exception ex) when (StateSnapshotSupport.IsRefusal(ex)) { return await StateSnapshotSupport.RefuseAsync(output, ex); }
    }

    /// <summary>The other side from a settings file: its <c>state:</c> section, and for SQLite the <c>state.db</c> beside the file.</summary>
    private static StateEndpoint? FromSettingsFile(string file, out string? problem)
    {
        problem = null;
        try
        {
            var settings = StateSettingsWriter.Load(file);
            var sqlite = settings.IsSqlite ? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(file))!, "state.db") : null;
            return new StateEndpoint(settings, sqlite);
        }
        catch (InvalidOperationException ex) { problem = ex.Message; return null; }
    }
}
