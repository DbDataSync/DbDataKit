using DbDataKit.Cli;

namespace DbDataKit.State.Cli;

/// <summary>
/// The shared <c>state</c> noun of every product's command line (plan 29b, M7: the same verbs everywhere). A product lists it with the other nouns, passing what only it knows:
/// <code>
/// StateVerbs.Create(new StateVerbContext("dbdatakit", "the console", args => new StateTarget(settings, path, secrets), Modules, StateScope.Install, ["repo"], "[--repo &lt;console directory&gt;]"))
/// </code>
/// <list type="bullet">
/// <item><c>state show</c>: the effective engine, the connection (secrets masked), the namespaces, each module's registered version, what is pending. Read-only; creates and migrates nothing.</item>
/// <item><c>state test</c>: connects, reads the registry, checks the rights and the engine version, finds a schema clash and the migrations this login may not apply; stops at the first failure with one sentence that names the fix, and an exit code per kind (<see cref="StateExit"/>).</item>
/// <item><c>state grants</c>: the <c>GRANT</c> statements a login needs (<see cref="StateGrants"/>).</item>
/// <item><c>state init</c>: puts the state in one of the three topologies (<see cref="StateTopology"/>): writes the settings, creates the schemas and, with an administrator's connection, the service's rights.</item>
/// </list>
/// <item><c>state export</c>, <c>import</c>, <c>verify</c>, <c>move</c>: the snapshot verbs (plan 29b X04, C04), each in a file of its own; they work offline (the service has to be stopped).</item>
/// Verbs that come later are written in files of their own and added to <see cref="Create"/>; the ones above do not change when they are.
/// </summary>
public static class StateVerbs
{
    /// <summary>The <c>state</c> noun: <c>show</c>, <c>test</c> and <c>grants</c> over the product's own state.</summary>
    public static Noun Create(StateVerbContext context) => new("state", "The state database: where it is, whether it works, and the rights it needs",
    [
        new Verb("export", "Write the state to a snapshot directory (a manifest and one file per table), one progress line per table; offline: the service must be stopped, and a live instance refuses it by name", EffectClass.RepoFilesOnly,
            $"--to <directory> [--include-secrets-check] {context.OptionsUsage}".TrimEnd(),
            (args, output) => StateExportVerb.RunAsync(context, args, output),
            StateExportVerb.Schema),
        new Verb("import", "Load a snapshot into the empty state database the settings name, then migrate it to this version; offline, and it prints how to start the service afterwards", EffectClass.TargetWrites,
            $"--from <directory> [--migrate-after true|false] {context.OptionsUsage}".TrimEnd(),
            (args, output) => StateImportVerb.RunAsync(context, args, output),
            StateImportVerb.Schema),
        new Verb("backup", "Write a verified backup of the state to <folder>/state-<time> while the service runs (a snapshot of the same format as export, consistent as of its start on SQLite and PostgreSQL); it counts only after it was re-hashed against its manifest", EffectClass.TargetWrites,
            $"--to <folder> {context.OptionsUsage}".TrimEnd(),
            (args, output) => StateBackupVerb.BackupAsync(context, args, output),
            StateBackupVerb.BackupSchema),
        new Verb("restore", "Restore a backup (or the newest of a folder of backups) into the empty state database the settings name: the backup is verified first, a database that holds a module and a backup of a newer program are refused; offline", EffectClass.TargetWrites,
            $"--from <backup folder> [--migrate-after true|false] {context.OptionsUsage}".TrimEnd(),
            (args, output) => StateBackupVerb.RestoreAsync(context, args, output),
            StateImportVerb.Schema),
        new Verb("verify", "Compare a snapshot with its manifest (--snapshot), the state with a snapshot (--snapshot --db or --against) or with another database (--other-engine, --other-connection with secret references, or --against a settings file); names the table and rows that differ", EffectClass.TargetReadOnly,
            $"--snapshot <directory> [--db] | --against <snapshot directory or settings file> | --other-engine sqlite|mssql|postgres [--other-connection <string>] [--other-sqlite <file>] [--other-schema <s>] [--other-prefix <p>] [--rehash-payloads] {context.OptionsUsage}".TrimEnd(),
            (args, output) => StateVerifyVerb.RunAsync(context, args, output),
            StateVerifyVerb.Schema),
        new Verb("move", "Move the state to another engine or database offline: export, import into the empty target, verify, and only then write the new settings; the old database is untouched; needs --yes or an answer of yes, and the service stopped", EffectClass.TargetWrites,
            $"--to-engine sqlite|mssql|postgres [--to-connection <string with secret references>] [--to-schema <s>] [--to-prefix <p>] [--to-sqlite <file>] [--keep-snapshot] [--yes] {context.OptionsUsage}".TrimEnd(),
            (args, output) => StateMoveVerb.RunAsync(context, args, output),
            StateMoveVerb.Schema),
        new Verb("show", "Show the effective engine, the connection (secrets masked), where each module's tables are, each module's version in the registry and what is pending; read-only, creates and migrates nothing", EffectClass.TargetReadOnly,
            $"[--offline] {context.OptionsUsage}".TrimEnd(),
            (args, output) => StateShow.RunAsync(context, args, output),
            StateSchemas.Show),
        new Verb("test", "Connect, read the registry, check the rights and the engine version, and look for a schema clash and for migrations this login may not apply; stops at the first failure with the fix", EffectClass.TargetReadOnly,
            context.OptionsUsage,
            (args, output) => StateTest.RunAsync(context, args, output),
            StateSchemas.Test),
        new Verb("grants", $"Print the GRANT statements a login needs for {context.Product}'s modules (the login it runs as, or with --migrate the one that upgrades the database); run them as a database administrator", EffectClass.OfflineOnly,
            $"--login <database role or user> [--migrate] {context.OptionsUsage}".TrimEnd(),
            (args, output) => StateGrantsVerb.RunAsync(context, args, output),
            DataSchemas.Object([("statements", DataSchemas.StringList)], "statements")),
        new Verb("init", StateInitVerb.Summary, EffectClass.TargetWrites, StateInitVerb.Usage(context), (args, output) => StateInitVerb.RunAsync(context, args, output), StateInitVerb.Schema),
    ]);
}

/// <summary>The JSON Schemas of the documents of <c>state show</c> and <c>state test</c>.</summary>
internal static class StateSchemas
{
    private static readonly string Module = DataSchemas.Object(
    [
        ("module", DataSchemas.String), ("scope", DataSchemas.String), ("place", DataSchemas.String), ("schema", DataSchemas.String), ("prefix", DataSchemas.String), ("file", DataSchemas.String),
        ("binary_version", DataSchemas.Integer), ("registered_version", DataSchemas.Integer), ("applied_by", DataSchemas.String), ("pending", DataSchemas.Integer), ("problem", DataSchemas.String), ("status", DataSchemas.String),
    ], "module", "place", "binary_version", "status");

    private static readonly string Other = DataSchemas.Object([("module", DataSchemas.String), ("scope", DataSchemas.String), ("version", DataSchemas.Integer)], "module", "scope", "version");

    private static readonly string Source = DataSchemas.Object([("kind", DataSchemas.String), ("name", DataSchemas.String)], "kind");

    public static readonly string Show = DataSchemas.Object(
    [
        ("product", DataSchemas.String), ("engine", DataSchemas.String), ("file", DataSchemas.String), ("file_size", DataSchemas.Integer), ("connection", DataSchemas.String),
        ("sources", DataSchemas.Object([("engine", Source), ("connection", Source), ("namespaces", Source), ("retention", Source)])),
        ("registry_table", DataSchemas.String), ("registry_exists", DataSchemas.Boolean), ("modules_listed", DataSchemas.Boolean), ("connected", DataSchemas.Boolean),
        ("engine_version", DataSchemas.String), ("login", DataSchemas.String), ("modules", DataSchemas.ListOf(Module)), ("other_registered", DataSchemas.ListOf(Other)),
        ("pending_migrations", DataSchemas.Integer), ("error", DataSchemas.String),
    ]);

    public static readonly string Test = DataSchemas.Object(
    [
        ("engine", DataSchemas.String), ("ok", DataSchemas.Boolean),
        ("steps", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("status", DataSchemas.String), ("message", DataSchemas.String)], "name", "status", "message"))),
        ("failed", DataSchemas.String),
    ]);
}
