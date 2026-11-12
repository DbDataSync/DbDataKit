using DbDataKit.Cli;
using DbDataKit.Secrets;

namespace DbDataKit.State.Cli;

/// <summary>
/// <c>state export --to &lt;directory&gt;</c> (plan 29b, X04): writes the product's current state to a snapshot directory (<c>state-snapshot/1</c>), one progress line per table. It works offline: the service has to be stopped,
/// and a live instance (or a SQLite file another process holds) refuses the export with its name. The directory has to be new or empty. Secrets are in the snapshot as stored (encrypted); with
/// <c>--include-secrets-check</c> the snapshot is searched for the resolved credentials of the connection string, and removed if any is there.
/// </summary>
internal static class StateExportVerb
{
    public static readonly string Schema = DataSchemas.Object(
    [
        ("directory", DataSchemas.String), ("engine", DataSchemas.String), ("modules", DataSchemas.Integer), ("tables", DataSchemas.Integer), ("rows", DataSchemas.Integer),
        ("payload_files", DataSchemas.Integer), ("payload_bytes", DataSchemas.Integer), ("secrets_checked", DataSchemas.Boolean), ("summary", DataSchemas.String), ("error", DataSchemas.String),
    ]);

    public static async Task<int> RunAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var usage = $"Usage: state export --to <directory> [--include-secrets-check] {context.OptionsUsage}".TrimEnd();
        var parsed = CliArgs.Parse(args, new HashSet<string> { "include-secrets-check" }, new HashSet<string>(context.Options.Append("to")));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("to") is not { Length: > 0 } directory)
        {
            await output.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }

        var (opened, failure) = await StateSnapshotSupport.OpenAsync(context, parsed, output);
        if (opened is null) return failure;

        await output.Out.WriteLineAsync($"Exporting {opened.Endpoint.Describe()} to '{Path.GetFullPath(directory)}' (the service must be stopped).");
        try
        {
            var result = await opened.Service.ExportAsync(opened.Endpoint, opened.Modules, context.Scope, directory, null, StateSnapshotSupport.Lines(output.Out), output.Cancellation);
            var data = output.Data;
            data["directory"] = result.Snapshot.Directory;
            data["engine"] = opened.Session.Engine;
            data["modules"] = result.Snapshot.Manifest.Modules.Count;
            data["tables"] = result.Snapshot.Tables;
            data["rows"] = result.Snapshot.Rows;
            data["payload_files"] = result.Snapshot.PayloadFiles;
            data["payload_bytes"] = result.Snapshot.PayloadBytes;
            data["summary"] = result.Summary;
            await output.Out.WriteLineAsync(result.Summary);

            if (parsed.Has("include-secrets-check"))
            {
                var secrets = SecretsOf(opened);
                var found = StateSnapshotSupport.FindSecrets(result.Snapshot.Directory, secrets);
                data["secrets_checked"] = true;
                if (found.Count > 0)
                {
                    Directory.Delete(result.Snapshot.Directory, recursive: true);
                    var sentence = $"The snapshot held a credential of the state connection in {string.Join(", ", found)}; it was removed. Do not use this snapshot; report this as a defect.";
                    await output.Error.WriteLineAsync(sentence);
                    data["error"] = sentence;
                    return StateExit.SecretInSnapshot;
                }

                await output.Out.WriteLineAsync(secrets.Count == 0
                    ? "Secrets check: the state connection has no password to look for; nothing found."
                    : $"Secrets check: {secrets.Count} credential(s) of the state connection looked for in every file of the snapshot; none found.");
            }

            await output.Out.WriteLineAsync($"Next: copy the directory where it is needed and run '{context.StateCommand("import")} --from <directory>' against an empty database.");
            return CliExit.Ok;
        }
        catch (Exception ex) when (StateSnapshotSupport.IsRefusal(ex)) { return await StateSnapshotSupport.RefuseAsync(output, ex); }
    }

    /// <summary>The credential values of the connection string, resolved: what must not be in a snapshot.</summary>
    private static List<string> SecretsOf(StateSnapshotSupport.Opened opened)
    {
        if (opened.Session.IsSqlite) return [];
        var secrets = new List<string>();
        try
        {
            if (opened.Session.Settings.ConnectionString is not { Length: > 0 } text) return secrets;
            var resolved = StateOpener.ResolveConnectionString(text, opened.Secrets);
            secrets.AddRange(ConnectionStringReferences.CredentialValues(resolved));
            if (ConnectionStringReferences.IsWholeReference(opened.Session.Settings.ConnectionString, out _)) secrets.Add(resolved);
        }
        catch (InvalidOperationException) { /* the export itself already failed on it */ }
        return secrets;
    }
}
