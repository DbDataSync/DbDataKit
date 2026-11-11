using System.Data.Common;
using System.Text.RegularExpressions;
using DbDataKit.Cli;
using DbDataKit.Secrets;

namespace DbDataKit.State.Cli;

/// <summary>
/// <c>state init --topology service|group|install</c> (plan 29b C03): puts a service's state in one of the three places of plan 29 section 5, in order and each step reported:
/// <list type="number">
/// <item>checks the arguments (a plain password in a connection string is refused);</item>
/// <item>refuses to point a product whose state holds data at another place (<c>state move</c> does that);</item>
/// <item>on a server engine: the schemas and, with <c>--login</c>, the login's rights (<see cref="StateGrants"/>), printed, or with <c>--apply</c> and <c>--admin-connection</c> run through the administrator's connection
/// (which also applies the service's modules, because a grant on a table needs the table);</item>
/// <item>writes the <c>state</c> section with <see cref="StateSettingsWriter"/> (nothing when the settings already say so);</item>
/// <item>runs the checks of <c>state test</c> against the new settings and prints them.</item>
/// </list>
/// Running it again changes nothing: the settings compare equal, the schemas exist, a grant repeats harmlessly and no migration is pending.
/// </summary>
internal static partial class StateInitVerb
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex PlainName { get; }

    private static readonly string[] OwnOptions = ["topology", "engine", "connection", "admin-connection", "login", "group", "settings"];

    public const string Summary = "Put the state in one of the three places: a SQLite file per service, one database per group, or one for the whole install; writes the settings, and for a database prints (or with --apply creates) the schemas and the service's rights, then runs 'state test'";

    public static string Usage(StateVerbContext context) =>
        $"--topology service|group|install [--engine mssql|postgres --connection <connection with Password=secret:NAME or env:NAME>] [--login <role or user> [--migrate]] [--admin-connection <reference> --apply] [--group <name>] [--settings <file>] {context.OptionsUsage.Replace("[--engine mssql|postgres] ", "")}".TrimEnd();

    public static readonly string Schema = DataSchemas.Object(
    [
        ("topology", DataSchemas.String), ("engine", DataSchemas.String), ("changed", DataSchemas.Boolean), ("settings_file", DataSchemas.String), ("applied", DataSchemas.Boolean), ("ok", DataSchemas.Boolean),
        ("steps", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("status", DataSchemas.String), ("message", DataSchemas.String)], "name", "status", "message"))),
        ("statements", DataSchemas.StringList), ("overridden_by_environment", DataSchemas.StringList),
    ], "topology", "steps");

    private sealed class Steps(CliContext output)
    {
        public List<object?> List { get; } = [];

        public async Task Add(string name, string status, string message)
        {
            List.Add(new Dictionary<string, object?> { ["name"] = name, ["status"] = status, ["message"] = message });
            await output.Out.WriteLineAsync($"{status.ToUpperInvariant(),-7} {name,-11} {message}");
        }
    }

    public static async Task<int> RunAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var valued = new HashSet<string>(context.Options.Concat(OwnOptions));
        var parsed = CliArgs.Parse(args, new HashSet<string> { "apply", "migrate" }, valued);
        string usage = $"Usage: state init --topology service|group|install [--engine mssql|postgres --connection <connection>] [--login <role or user> [--migrate]] [--admin-connection <reference> --apply] [--group <name>] [--settings <file>] {context.OptionsUsage}".TrimEnd();
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || !StateTopology.TryParse(parsed.Get("topology"), out var kind))
        {
            await output.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }

        var steps = new Steps(output);
        output.Data["topology"] = kind.ToString().ToLowerInvariant();
        output.Data["steps"] = steps.List;
        output.Data["applied"] = false;
        output.Data["changed"] = false;
        output.Data["ok"] = false;
        async Task<int> Stop(int exit, string step, string message)
        {
            await steps.Add(step, "failed", message);
            await output.Error.WriteLineAsync(message);
            return exit;
        }

        // ---- 1. the arguments
        var apply = parsed.Has("apply");
        var login = parsed.Get("login");
        var adminText = parsed.Get("admin-connection");
        if (login is not null && !PlainName.IsMatch(login))
            return await Stop(CliExit.Usage, "arguments", $"--login '{login}' is not a plain database role or user name (letters, digits and underscores, not starting with a digit).");
        if (parsed.Has("migrate") && login is null)
            return await Stop(CliExit.Usage, "arguments", "--migrate says which right the login gets, and needs --login.");
        if (apply && adminText is null)
            return await Stop(CliExit.Usage, "arguments", "--apply runs the statements as a database administrator: give --admin-connection (a connection string whose password is a reference, Password=secret:NAME or env:NAME).");

        StateTarget target;
        StateSettings current;
        string? file;
        try
        {
            var locate = CliArgs.Parse(WithoutOption(args, "engine"), new HashSet<string> { "apply", "migrate" }, new HashSet<string>(context.Options.Concat(OwnOptions).Where(o => o != "engine")));
            target = context.Locate(locate);
            file = parsed.Get("settings") ?? target.SettingsFile;
            current = file is not null && File.Exists(file) ? StateSettingsWriter.Load(file) : target.Settings;
            if (string.IsNullOrWhiteSpace(current.Engine) && string.IsNullOrWhiteSpace(current.ConnectionString)) current = target.Settings;
            _ = current.EngineId;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException)
        {
            return await Stop(StateExit.Settings, "arguments", ex.Message);
        }

        StateSettings settings;
        StateScope scope;
        StateLayout layout;
        try
        {
            if (adminText is not null) StateTopology.RequireReferencedPassword(adminText, "--admin-connection", context.Program);
            settings = StateTopology.Settings(kind, current, parsed.Get("engine"), parsed.Get("connection"), context.Program);
            scope = StateTopology.ScopeFor(kind, context.Scope, parsed.Get("group"), context.Program);
            layout = StateLayout.Create(settings.EngineId, context.Modules ?? [], scope, settings.Namespaces);
        }
        catch (StateTopologyException ex) { return await Stop(CliExit.Usage, "arguments", ex.Message); }
        catch (InvalidOperationException ex) { return await Stop(StateExit.Settings, "arguments", ex.Message); }

        var engine = settings.EngineId;
        var isServer = engine != StateEngineIds.Sqlite;
        await steps.Add("arguments", "ok", $"{kind.ToString().ToLowerInvariant()}: {StateTopology.EngineName(engine)}{(isServer ? ", " + ConnectionStringReferences.Mask(settings.ConnectionString) : ", a file per service")}");
        output.Data["engine"] = StateTopology.EngineName(engine);

        // ---- 2. a state that has data is moved, not re-pointed
        var same = StateTopology.Same(current, settings);
        if (!same && PopulatedBy(context, target, current, parsed) is { } populated)
            return await Stop(StateExit.Populated, "current", $"The state this {context.Product} has now ({populated}) already holds data, and pointing {context.Product} at another place would leave it behind: use '{context.Program} state move' (plan 29b slice C04, which X04's export and import make possible) to carry it over, then this setting follows.");
        await steps.Add("current", "ok", same ? "the settings already say this" : "nothing is stored yet that this would leave behind");

        // ---- 3. the schemas and the service's rights
        var statements = new List<string>();
        var applied = false;
        if (isServer)
        {
            var schemaStatements = layout.Schemas.Select(s => StateTopology.CreateSchema(engine, s)).Where(s => s.Length > 0).ToList();
            var grants = login is null ? [] : StateGrants.Statements(layout, login, parsed.Has("migrate") ? GrantLevel.Migrate : GrantLevel.Runtime).ToList();
            statements.AddRange(schemaStatements);
            statements.AddRange(grants);
            output.Data["statements"] = statements;

            if (!apply)
            {
                await steps.Add("schemas", "printed", $"{schemaStatements.Count} statement(s) create the schemas; nothing was run (add --admin-connection and --apply to run them)");
                foreach (var statement in schemaStatements) await output.Out.WriteLineAsync("  " + statement);
                if (login is null)
                    await steps.Add("grants", "skipped", $"no --login given; to print the rights of the login the service runs as: {context.Program} state grants --login <name>");
                else
                {
                    await steps.Add("grants", "printed", $"{grants.Count} statement(s) give '{login}' the {(parsed.Has("migrate") ? "migrate" : "runtime")} rights on this service's namespaces only; a database administrator runs them after the modules exist (init --apply makes them first)");
                    foreach (var statement in grants) await output.Out.WriteLineAsync("  " + statement);
                }
            }
            else
            {
                var (exit, message) = await ApplyAsync(context, target, settings, scope, layout, adminText!, schemaStatements, grants, login, steps);
                if (exit != 0) return await Stop(exit, "apply", message!);
                applied = true;
            }
        }
        output.Data["applied"] = applied;

        // ---- 4. the settings file
        if (same)
            await steps.Add("settings", "ok", file is null ? "nothing to write: the settings already say this" : $"{file} already says this; not changed");
        else if (file is null)
            return await Stop(CliExit.Usage, "settings", $"{context.Product} keeps its state settings in no file of its own that {context.Program} knows: give --settings <file> (the settings file the service reads), or set them where the service reads them (engine {StateTopology.EngineName(engine)}).");
        else
        {
            try { new StateSettingsWriter().Write(file, settings); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException) { return await Stop(StateExit.Settings, "settings", ex.Message); }
            output.Data["changed"] = true;
            await steps.Add("settings", "changed", $"wrote the state section of {file}");
        }

        output.Data["settings_file"] = file;

        // an environment variable beats the file (the settings order), so what was just written does not take effect until it is unset (plan 29b C07; `state show` says the same under "settings from")
        if (target.Sources is { } effective)
        {
            var overriding = new[] { ("engine", effective.Engine), ("connection", effective.Connection), ("namespaces", effective.Namespaces), ("retention", effective.Retention) }.Where(s => s.Item2.Kind == StateSourceKind.Environment).ToList();
            if (overriding.Count > 0)
            {
                var sentence = $"{string.Join(", ", overriding.Select(s => $"the {s.Item1} still comes from {s.Item2}"))}: it wins over {(file is null ? "the settings" : file)}, so unset it (or set it to the same value) for the state section to apply; '{context.Program} state show' lists where each setting comes from.";
                output.Data["overridden_by_environment"] = overriding.Select(s => (object?)s.Item1).ToList();
                await steps.Add("override", "note", sentence);
            }
        }

        // ---- 5. state test on what was written
        var after = context with { Locate = _ => new StateTarget(settings, target.SqlitePath, target.Secrets, file) };
        StateSession session;
        try { session = StateSession.Create(after, parsed); }
        catch (StateSettingsProblem ex) { return await Stop(StateExit.Settings, "test", ex.Message); }
        var report = StateChecks.Run(after, session);
        foreach (var step in report.Steps)
            await steps.Add("test:" + step.Name, step.Status, session.Scrub(step.Message));

        // a database the printed statements have not been run on cannot pass yet: that is not a failure of init
        var pendingByDesign = isServer && !applied && report.Exit != 0;
        output.Data["ok"] = report.Ok || pendingByDesign;
        if (pendingByDesign)
        {
            await output.Out.WriteLineAsync($"Not usable yet: run the statements above (or init again with --admin-connection and --apply), then '{context.Program} state test'.");
            return CliExit.Ok;
        }

        return report.Exit;
    }

    private static string[] WithoutOption(string[] args, string name)
    {
        var result = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == $"--{name}") { i++; continue; }
            if (args[i].StartsWith($"--{name}=", StringComparison.Ordinal)) continue;
            result.Add(args[i]);
        }

        return [.. result];
    }

    /// <summary>A phrase naming where the current state is when it holds registered modules; null when it is empty, missing or cannot be read (nothing can be said to be left behind).</summary>
    private static string? PopulatedBy(StateVerbContext context, StateTarget target, StateSettings current, CliArgs parsed)
    {
        try
        {
            var old = context with { Locate = _ => new StateTarget(current, target.SqlitePath, target.Secrets, target.SettingsFile) };
            var session = StateSession.Create(old, parsed);
            StateOpener.RegisterProviders();
            using var inspector = StateInspector.Open(session.Layout, session.ResolveTarget());
            var rows = inspector.ReadRegistry();
            return rows is { Count: > 0 } ? $"{rows.Count} module(s) registered in {(session.IsSqlite ? session.File : session.Connection)}" : null;
        }
        catch (Exception ex) when (ex is StateSettingsProblem or InvalidOperationException or ArgumentException or DbException or IOException) { return null; }
    }

    private static async Task<(int Exit, string? Message)> ApplyAsync(
        StateVerbContext context, StateTarget target, StateSettings settings, StateScope scope, StateLayout layout, string adminText,
        List<string> schemaStatements, List<string> grants, string? login, Steps steps)
    {
        var engine = settings.EngineId;
        string admin;
        var secrets = new List<string>();
        try
        {
            admin = StateOpener.ResolveConnectionString(adminText, target.Secrets?.Invoke());
            secrets.AddRange(ConnectionStringReferences.CredentialValues(admin));
            if (ConnectionStringReferences.IsWholeReference(adminText, out _)) secrets.Add(admin);
        }
        catch (InvalidOperationException ex)
        {
            return (StateExit.Settings, $"{ex.Message} Store the secret ({context.Program} secret set <name>) or set the environment variable it names.");
        }

        string Scrub(string text)
        {
            foreach (var secret in secrets.Where(s => s.Length >= 3).OrderByDescending(s => s.Length)) text = text.Replace(secret, "***", StringComparison.Ordinal);
            return text;
        }

        string First(Exception ex)
        {
            var text = ex.Message.Trim();
            var end = text.IndexOfAny(['\r', '\n']);
            return Scrub(end < 0 ? text : text[..end]);
        }

        StateOpener.RegisterProviders();
        try
        {
            using (var connection = StateTopology.Connect(engine, admin)) Run(connection, schemaStatements);
            await steps.Add("schemas", "ok", $"{schemaStatements.Count} schema(s) exist");

            // a grant on a table needs the table, and the registry: the modules are applied as the administrator
            var module = new StateSettings(StateTopology.EngineName(engine), admin) { Namespaces = settings.Namespaces };
            _ = StateOpener.Open(module, target.SqlitePath, context.Modules ?? [], scope, null, $"{context.Program} state init");
            await steps.Add("modules", "ok", $"{(context.Modules ?? []).Count} module(s) are at the version of this program");

            if (login is null)
                await steps.Add("grants", "skipped", "no --login given");
            else
            {
                using var connection = StateTopology.Connect(engine, admin);
                Run(connection, grants);
                await steps.Add("grants", "ok", $"{grants.Count} statement(s) run for '{login}'");
            }

            return (0, null);
        }
        catch (DbException ex) { return (StateExit.Admin, $"The administrator's connection failed or a statement was refused ({First(ex)}): check --admin-connection, and that the account may create schemas and grant rights in this database."); }
        catch (InvalidOperationException ex) { return (StateExit.Refused, Scrub(ex.Message)); }
    }

    private static void Run(DbConnection connection, IEnumerable<string> statements)
    {
        foreach (var statement in statements)
        {
            using var command = connection.CreateCommand();
            command.CommandText = statement;
            command.ExecuteNonQuery();
        }
    }
}
