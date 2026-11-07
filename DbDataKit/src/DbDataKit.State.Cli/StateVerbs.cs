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
/// Verbs that come later (the snapshot verbs, plan 29b X04) are written in files of their own and added to <see cref="Create"/>; the three above do not change when they are.
/// </summary>
public static class StateVerbs
{
    /// <summary>The <c>state</c> noun: <c>show</c>, <c>test</c> and <c>grants</c> over the product's own state.</summary>
    public static Noun Create(StateVerbContext context) => new("state", "The state database: where it is, whether it works, and the rights it needs",
    [
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

    public static readonly string Show = DataSchemas.Object(
    [
        ("product", DataSchemas.String), ("engine", DataSchemas.String), ("file", DataSchemas.String), ("file_size", DataSchemas.Integer), ("connection", DataSchemas.String),
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
