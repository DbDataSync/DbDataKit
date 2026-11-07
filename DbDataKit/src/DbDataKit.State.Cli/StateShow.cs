using System.Globalization;
using DbDataKit.Cli;

namespace DbDataKit.State.Cli;

/// <summary>
/// <c>state show</c>: what a product's state is, read-only (plan 29b, C01): the engine and the connection (secrets masked, references shown as written, never resolved), where each module's tables are, the version of
/// each module in the registry against the one in this binary, and what opening the database would apply. It opens no <see cref="StateDatabase"/>, so nothing is created or migrated; a SQLite file that is not there is
/// said to be missing and stays missing.
/// </summary>
internal static class StateShow
{
    public static async Task<int> RunAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "offline" }, new HashSet<string>(context.Options));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await output.Error.WriteLineAsync($"Usage: state show [--offline] {context.OptionsUsage}".TrimEnd());
            return CliExit.Usage;
        }

        StateSession session;
        try { session = StateSession.Create(context, parsed); }
        catch (StateSettingsProblem ex)
        {
            await output.Error.WriteLineAsync(ex.Message);
            return StateExit.Settings;
        }

        var data = output.Data;
        var write = output.Out;
        data["product"] = context.Program;
        data["engine"] = session.Engine;
        await write.WriteLineAsync($"State database of {context.Product}");
        await write.WriteLineAsync($"  engine       {session.Engine}");
        if (session.IsSqlite)
        {
            data["file"] = session.File;
            var info = new FileInfo(session.File);
            if (info.Exists) data["file_size"] = info.Length;
            await write.WriteLineAsync($"  file         {session.File} ({(info.Exists ? Size(info.Length) : "not created yet")})");
        }
        else
        {
            data["connection"] = session.Connection;
            await write.WriteLineAsync($"  connection   {session.Connection}");
        }

        data["registry_table"] = session.Registry;
        await write.WriteLineAsync($"  registry     {session.Registry}");
        if (session.Target.Sources is { } sources)
        {
            // why the product uses this engine: a flag, an environment variable, its settings file or nothing (the default)
            data["sources"] = new Dictionary<string, object?>
            {
                ["engine"] = sources.Engine.ToData(), ["connection"] = sources.Connection.ToData(), ["namespaces"] = sources.Namespaces.ToData(), ["retention"] = sources.Retention.ToData(),
            };
            await write.WriteLineAsync("  settings from");
            await write.WriteLineAsync($"    engine       {sources.Engine}");
            if (!session.IsSqlite) await write.WriteLineAsync($"    connection   {sources.Connection}");
            await write.WriteLineAsync($"    namespaces   {sources.Namespaces}");
            await write.WriteLineAsync($"    retention    {sources.Retention}");
        }

        data["modules_listed"] = context.Modules is not null;

        StateInspection? inspection = null;
        string? failure = null;
        var exit = CliExit.Ok;
        if (!parsed.Has("offline"))
        {
            try
            {
                var target = session.ResolveTarget();
                StateOpener.RegisterProviders();
                using var inspector = StateInspector.Open(session.Layout, target);
                data["connected"] = true;
                if (inspector.ServerVersion() is { } version) { data["engine_version"] = version; await write.WriteLineAsync($"  version      {version}"); }
                if (inspector.CurrentLogin() is { } login) { data["login"] = login; await write.WriteLineAsync($"  login        {login}"); }
                inspection = inspector.Inspect();
            }
            catch (StateSettingsProblem ex) { failure = ex.Message; exit = StateExit.Settings; }
            catch (Exception ex) when (StateDatabaseErrors.IsProviderError(ex))
            {
                (exit, failure) = session.Describe(ex);
                data["connected"] = false;
            }
        }

        data["registry_exists"] = inspection?.RegistryExists ?? false;
        var modules = new List<object?>();
        if (context.Modules is null)
            await write.WriteLineAsync($"  modules      {context.Product} has no module list yet, so namespaces and versions are not shown");
        else
        {
            await write.WriteLineAsync("  modules");
            var rows = session.Layout.Namespaces.Select(ns => (Namespace: ns, Found: inspection?.Modules.FirstOrDefault(m => m.Namespace == ns))).ToList();
            var width = rows.Count == 0 ? 0 : rows.Max(r => r.Namespace.Key.Length);
            foreach (var (ns, found) in rows)
            {
                var item = new Dictionary<string, object?>
                {
                    ["module"] = ns.Module.Name,
                    ["place"] = Place(ns),
                    ["binary_version"] = ns.Module.Version,
                };
                if (ns.ScopeName is not null) item["scope"] = ns.ScopeName;
                if (ns.Schema is not null) item["schema"] = ns.Schema;
                if (ns.Prefix.Length > 0) item["prefix"] = ns.Prefix;
                if (ns.File is not null) item["file"] = ns.File;
                string state;
                if (inspection is null) state = "not read";
                else if (found?.Problem is { } problem) { state = "PROBLEM: " + problem; item["problem"] = problem; }
                else if (found?.Registered is { } row)
                {
                    item["registered_version"] = row.Version;
                    item["applied_by"] = row.AppliedBy;
                    item["pending"] = found.Pending;
                    state = found.Pending == 0 ? "current" : $"{found.Pending} migration(s) pending";
                }
                else
                {
                    item["pending"] = ns.Module.Version;
                    state = inspection.DatabaseExists ? $"not registered: opening creates it ({ns.Module.Version} migration(s))" : $"not created yet ({ns.Module.Version} migration(s))";
                }

                item["status"] = state;
                modules.Add(item);
                var registeredText = found?.Registered is { } r ? $"v{r.Version}" : "-";
                await write.WriteLineAsync($"    {ns.Key.PadRight(width)}  {Place(ns),-44}  binary v{ns.Module.Version}  registered {registeredText,-3}  {state}");
            }
        }

        data["modules"] = modules;
        if (inspection is not null)
        {
            var others = inspection.Registry.Where(r => !session.Layout.Namespaces.Any(n => n.Module.Name == r.Module && (n.ScopeName ?? "install") == r.Scope)).ToList();
            if (others.Count > 0)
            {
                data["other_registered"] = others.Select(r => (object?)new Dictionary<string, object?> { ["module"] = r.Module, ["scope"] = r.Scope, ["version"] = r.Version }).ToList();
                await write.WriteLineAsync($"  also in the registry (other services): {string.Join(", ", others.Select(r => $"{r.Module}/{r.Scope} v{r.Version}"))}");
            }

            var pending = inspection.PendingModules.Sum(m => m.Pending);
            data["pending_migrations"] = pending;
            await write.WriteLineAsync(!inspection.DatabaseExists
                ? "  pending      the database does not exist: the first start creates it and applies every migration"
                : !inspection.RegistryExists
                    ? "  pending      no modules registered: the first start creates the registry and applies every migration"
                    : pending == 0 ? "  pending      nothing" : $"  pending      {pending} migration(s) in {inspection.PendingModules.Count()} module(s): the next start applies them");
            if (inspection.Legacy is { } legacy) await write.WriteLineAsync($"  note         made by the old structure ({legacy}); this service refuses it");
            if (inspection.Registry.Count == 0 && inspection.DatabaseExists && inspection.RegistryExists) await write.WriteLineAsync("  note         no modules registered");
        }
        else if (context.Modules is not null && failure is null)
            await write.WriteLineAsync("  pending      not read (--offline)");

        if (failure is not null)
        {
            data["error"] = failure;
            await output.Error.WriteLineAsync($"The database was not read: {failure}");
        }

        return exit;
    }

    /// <summary>Where a namespace is, in a few words: the schema and prefix, or the file.</summary>
    private static string Place(StateNamespace ns) =>
        ns.File is not null ? $"file {ns.File} (as {ns.Schema})" : string.Join(' ', new[] { ns.Schema is { } s ? $"schema {s}" : null, ns.Prefix.Length > 0 ? $"prefix {ns.Prefix}" : null }.Where(p => p is not null)) is { Length: > 0 } text ? text : "the default schema";

    private static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture)} KB",
        _ => $"{(bytes / 1048576.0).ToString("0.#", CultureInfo.InvariantCulture)} MB",
    };
}
