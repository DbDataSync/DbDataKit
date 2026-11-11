using DbDataKit.Cli;
using DbDataKit.State.Engines.Snapshots;

namespace DbDataKit.State.Cli;

/// <summary>
/// <c>state move --to-engine &lt;engine&gt; --to-connection &lt;connection string with secret references&gt;</c> (plan 29b, C04): exports the product's state, imports it into the named empty database, verifies
/// the copy against the snapshot and against the source, and only then writes the new <c>state:</c> section of the settings file (the previous file is kept beside it). The old database is not touched, so going back is
/// a settings change. It prints the plan first and goes on only with <c>--yes</c> or an answer of yes; it refuses while the service uses the database (stop it first), and a product with no settings file for the
/// section says so. <see cref="StateMoveService"/> is the whole logic; this only parses and prints.
/// </summary>
internal static class StateMoveVerb
{
    public static readonly string Schema = DataSchemas.Object(
    [
        ("from_engine", DataSchemas.String), ("to_engine", DataSchemas.String), ("settings_file", DataSchemas.String), ("backup_file", DataSchemas.String), ("snapshot_directory", DataSchemas.String),
        ("rows", DataSchemas.Integer), ("tables", DataSchemas.Integer), ("verified_against_snapshot", DataSchemas.Boolean), ("verified_against_source", DataSchemas.Boolean),
        ("restart", DataSchemas.String), ("way_back", DataSchemas.String), ("summary", DataSchemas.String), ("plan", DataSchemas.StringList), ("error", DataSchemas.String),
    ]);

    public static async Task<int> RunAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var usage = $"Usage: state move --to-engine sqlite|mssql|postgres [--to-connection <connection string with secret references>] [--to-schema <schema>] [--to-prefix <prefix>] [--keep-snapshot] [--yes] {context.OptionsUsage}".TrimEnd();
        var parsed = CliArgs.Parse(args, new HashSet<string> { "keep-snapshot", "yes" }, new HashSet<string>(context.Options.Concat(["to-engine", "to-connection", "to-schema", "to-prefix"])));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("to-engine") is not { Length: > 0 })
        {
            await output.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }

        var (opened, failure) = await StateSnapshotSupport.OpenAsync(context, parsed, output);
        if (opened is null) return failure;

        var settingsFile = opened.Session.Target.SettingsFile;
        if (settingsFile is null)
        {
            var sentence = $"{StateSnapshotSupport.Capital(context.Product)} keeps no settings file with a state section that 'state move' can write, so it cannot switch it for you. " +
                $"Use '{context.Program} state export --to <directory>', name the new engine the way {context.Product}'s documentation says, and run '{context.Program} state import --from <directory>'.";
            await output.Error.WriteLineAsync(sentence);
            output.Data["error"] = sentence;
            return StateExit.Settings;
        }

        var target = StateSnapshotSupport.Other(parsed, "to-", opened.Session.Settings, opened.Session.File, out var problem);
        if (target is null)
        {
            await output.Error.WriteLineAsync(problem!);
            return CliExit.Usage;
        }

        var keep = parsed.Has("keep-snapshot");
        var plan = Plan(context, opened.Endpoint, target, settingsFile, keep);
        foreach (var line in plan) await output.Out.WriteLineAsync(line);
        output.Data["plan"] = plan.ToList();

        if (!parsed.Has("yes"))
        {
            await output.Out.WriteAsync("Type yes to move the state now: ");
            await output.Out.FlushAsync(output.Cancellation);
            var answer = (await output.In.ReadLineAsync(output.Cancellation))?.Trim();
            if (!string.Equals(answer, "yes", StringComparison.OrdinalIgnoreCase) && !string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
            {
                var sentence = "Not moved: nothing was asked for or confirmed. Run it again with --yes (or answer yes when asked); nothing was changed.";
                await output.Error.WriteLineAsync(sentence);
                output.Data["error"] = sentence;
                return StateExit.NotConfirmed;
            }
        }

        var options = new MoveOptions { DeleteSnapshotOnSuccess = !keep };
        if (context.ConfigureMove is { } configure) options = configure(options);
        var service = new StateMoveService(opened.Secrets, appliedBy: context.Program);
        try
        {
            var result = await service.MoveAsync(opened.Endpoint, target, settingsFile, opened.Modules, context.Scope, options, Lines(output.Out), output.Cancellation);
            var data = output.Data;
            data["from_engine"] = opened.Session.Engine;
            data["to_engine"] = target.Engine;
            data["settings_file"] = result.SettingsFile;
            data["backup_file"] = result.BackupFile;
            data["snapshot_directory"] = result.SnapshotDirectory;
            data["rows"] = result.Import.Rows;
            data["tables"] = result.Import.Tables;
            data["verified_against_snapshot"] = result.VerifiedAgainstSnapshot.Equal;
            data["verified_against_source"] = result.VerifiedAgainstSource.Equal;
            data["restart"] = result.RestartInstruction;
            data["way_back"] = result.WayBack;
            data["summary"] = result.Summary;
            await output.Out.WriteLineAsync(result.Summary);
            await output.Out.WriteLineAsync("Next: " + result.RestartInstruction);
            await output.Out.WriteLineAsync("Way back: " + result.WayBack);
            return CliExit.Ok;
        }
        catch (Exception ex) when (StateSnapshotSupport.IsRefusal(ex)) { return await StateSnapshotSupport.RefuseAsync(output, ex); }
    }

    private static List<string> Plan(StateVerbContext context, StateEndpoint from, StateEndpoint to, string settingsFile, bool keep) =>
    [
        $"Move the state of {context.Product}:",
        $"  from  {from.Describe()}",
        $"  to    {to.Describe()}{(to.Settings.ConnectionString is { Length: > 0 } ? $" ({DbDataKit.Secrets.ConnectionStringReferences.Mask(to.Settings.ConnectionString)})" : "")}",
        "  steps 1. check both databases and the settings file (nothing is written yet)",
        "        2. export the current state to a temporary snapshot (refused while the service uses it: stop the service first)",
        "        3. import it into the target, which has to be empty",
        "        4. verify the target against the snapshot and against the source",
        $"        5. only then write the new state section of '{settingsFile}' (the old file is kept beside it as a backup)",
        keep ? "        6. keep the snapshot" : "        6. remove the temporary snapshot",
        "  The old database is not touched, so the way back is the old settings file. Then restart the service.",
    ];

    /// <summary>One line per step, and per table of the export, import and comparison.</summary>
    private static IProgress<MoveProgress> Lines(TextWriter write)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return new InlineProgress<MoveProgress>(p =>
        {
            if (p.Snapshot is { } snapshot) { if (snapshot.Phase is "table-done") write.WriteLine($"  [{p.Step}] {p.Message}"); }
            else if (p.Verification is { } verification) { if (seen.Add($"{p.Step}/{verification.Module}.{verification.Table}")) write.WriteLine($"  [{p.Step}] {p.Message}"); }
            else write.WriteLine($"[{p.Step}] {p.Message}");
        });
    }
}
