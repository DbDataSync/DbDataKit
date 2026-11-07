using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace DbDataKit.State.Cli;

/// <summary>One line of the report of <c>state test</c>.</summary>
/// <param name="Name">The check: settings, connection, registry, rights, engine, clash, migrations.</param>
/// <param name="Status"><c>ok</c>, <c>note</c> (passed, with something worth saying), <c>failed</c> or <c>skipped</c>.</param>
/// <param name="Message">What was found, or for a failure the one sentence that names the fix.</param>
public sealed record StateTestStep(string Name, string Status, string Message);

/// <summary>What <c>state test</c> found: the steps that ran, in order, ending at the first failure; <see cref="Exit"/> is 0 or one of <see cref="StateExit"/>.</summary>
public sealed record StateTestReport(string Engine, IReadOnlyList<StateTestStep> Steps, int Exit)
{
    public bool Ok => Exit == 0;

    public StateTestStep? Failed => Steps.FirstOrDefault(s => s.Status == "failed");
}

/// <summary>
/// The checks of <c>state test</c>, in order, stopping at the first failure with one sentence that names the fix (plan 29b, C01): the settings are usable; the connection works; the login can read the registry (or
/// create it on an empty database); it has the rights the service runs with; the engine is a supported version; no table of the layout belongs to something else; and what is pending can be applied by this
/// login. Nothing is created or changed except a probe file on SQLite, which is made and removed to learn whether the folder is writable.
/// </summary>
internal static class StateChecks
{
    public static StateTestReport Run(StateVerbContext context, StateSession session)
    {
        var steps = new List<StateTestStep>();
        StateTestReport Done(int exit) => new(session.Engine, steps, exit);
        StateTestReport Fail(string name, int exit, string sentence) { steps.Add(new StateTestStep(name, "failed", sentence)); return Done(exit); }
        void Ok(string name, string message, string status = "ok") => steps.Add(new StateTestStep(name, status, message));

        Ok("settings", $"{session.Engine}, {(session.IsSqlite ? session.File : session.Connection)}");
        var program = context.Program;

        string target;
        try { target = session.ResolveTarget(); }
        catch (StateSettingsProblem ex) { steps.RemoveAt(0); return Fail("settings", StateExit.Settings, ex.Message); }

        StateOpener.RegisterProviders();
        StateInspector inspector;
        try { inspector = StateInspector.Open(session.Layout, target); }
        catch (Exception ex) when (StateDatabaseErrors.IsProviderError(ex) || ex is InvalidOperationException)
        {
            var (exit, sentence) = session.Describe(ex);
            return Fail("connection", exit, sentence);
        }

        using (inspector)
        {
            // ---- the connection works
            string? login = null;
            string? version = null;
            try
            {
                login = inspector.CurrentLogin();
                version = context.EngineVersion is { } injected ? injected(inspector.Connection) : inspector.ServerVersion() ?? SqliteVersion();
            }
            catch (Exception ex) when (StateDatabaseErrors.IsProviderError(ex))
            {
                var (exit, sentence) = session.Describe(ex);
                return Fail("connection", exit, sentence);
            }

            Ok("connection", inspector.DatabaseExists
                ? (session.IsSqlite ? $"opened {session.File}" : $"connected as '{login}'")
                : $"there is no state file yet at {session.File}; the first start creates it", inspector.DatabaseExists ? "ok" : "note");

            // ---- the login can read the registry (or make it)
            IReadOnlyList<RegisteredModule>? rows;
            try { rows = inspector.ReadRegistry(); }
            catch (Exception ex) when (StateDatabaseErrors.IsProviderError(ex))
            {
                var kind = StateDatabaseErrors.Classify(ex);
                return kind == StateErrorKind.PermissionDenied
                    ? Fail("registry", StateExit.Registry, $"The login '{login}' may not read the registry {session.Registry}: ask a database administrator to run '{program} state grants --login {login}' and run the statements it prints.")
                    : Fail("registry", session.Describe(ex).Exit, session.Describe(ex).Sentence);
            }

            if (rows is null)
            {
                if (inspector.DatabaseExists && !session.IsSqlite && !StateRights.CanCreate(inspector.Connection!, session.Layout))
                    return Fail("registry", StateExit.Registry, $"The login '{login}' cannot see the registry {session.Registry} and may not create it: ask a database administrator to run '{program} state grants --login {login}' (add --migrate if the database is still empty), and run the statements it prints.");
                Ok("registry", session.IsSqlite && !inspector.DatabaseExists ? "nothing there yet" : $"{session.Registry} is not there yet: the first start creates it", "note");
            }
            else
                Ok("registry", $"{session.Registry} is readable; {rows.Count} module(s) registered");

            // ---- the rights the layout needs
            if (session.IsSqlite)
            {
                if (WritableProblem(session.File) is { } problem)
                    return Fail("rights", StateExit.Rights, problem);
                Ok("rights", "the file and its folder are writable");
            }
            else
            {
                var missing = StateRights.Missing(inspector.Connection!, session.Layout, GrantLevel.Runtime, rows);
                if (missing.Count > 0)
                    return Fail("rights", StateExit.Rights, $"The login '{login}' lacks {Listed(missing)}: ask a database administrator to run '{program} state grants --login {login}' and run the statements it prints.");
                Ok("rights", rows is null ? "nothing to check until the tables exist" : $"'{login}' has the rights the running service needs");
            }

            // ---- the engine version
            var support = version is null ? null : EngineSupport.Check(session.Engine, version);
            if (support is { Supported: false })
                return Fail("engine", StateExit.EngineVersion, support.Message);
            Ok("engine", support?.Message ?? "the engine did not report a version", support is { Tested: false } ? "note" : "ok");

            // ---- clashes and refusals, from the registry
            if (context.Modules is null)
            {
                steps.Add(new StateTestStep("clash", "skipped", $"{context.Product} has no module list yet, so there is nothing to compare"));
                steps.Add(new StateTestStep("migrations", "skipped", $"{context.Product} has no module list yet"));
                return Done(0);
            }

            StateInspection inspection;
            try { inspection = inspector.Inspect(); }
            catch (Exception ex) when (StateDatabaseErrors.IsProviderError(ex))
            {
                var (exit, sentence) = session.Describe(ex);
                return Fail("clash", exit, sentence);
            }

            if (inspection.Legacy is { } legacy)
                return Fail("clash", StateExit.Refused, $"The database was made by the old structure that keeps one schema version for the whole database ({legacy}) and is never converted: point the service at a new, empty database (or a new file) and set it up again.");

            if (inspection.Clashes.Count > 0)
            {
                var table = inspection.Clashes[0];
                var owner = session.Layout.Namespaces.First(n => n.Tables.Values.Contains(table, StringComparer.OrdinalIgnoreCase)).Module.Name;
                return Fail("clash", StateExit.SchemaClash, $"The table {table} already exists and the registry does not say the module '{owner}' made it: give '{owner}' another schema or prefix (state.namespaces.{owner}) or point the state at an empty database.");
            }

            if (inspection.Modules.FirstOrDefault(m => m.Problem is not null) is { } refused)
                return Fail("clash", StateExit.Refused, $"This service would refuse the database: {refused.Problem}.");
            Ok("clash", "no table of the layout belongs to something else");

            // ---- pending migrations, and whether this login may apply them
            var pending = inspection.PendingModules.ToList();
            if (pending.Count == 0)
            {
                Ok("migrations", "nothing is pending");
                return Done(0);
            }

            var total = pending.Sum(m => m.Pending);
            var list = string.Join(", ", pending.Select(m => $"{m.Namespace.Key} {m.Registered?.Version ?? 0} to {m.BinaryVersion}"));
            if (!session.IsSqlite)
            {
                var needs = StateRights.Missing(inspector.Connection!, session.Layout, GrantLevel.Migrate, rows);
                if (needs.Count > 0)
                    return Fail("migrations", StateExit.PendingBlocked, $"{total} migration(s) are pending ({list}) and the login '{login}' may not apply them (it lacks {Listed(needs)}): run the service once as the login that upgrades the database, or ask a database administrator to run '{program} state grants --login {login} --migrate'.");
            }

            Ok("migrations", $"{total} migration(s) pending ({list}); the next start applies them", "note");
            return Done(0);
        }
    }

    private static string Listed(IReadOnlyList<string> missing) => missing.Count <= 3 ? string.Join(", ", missing) : $"{string.Join(", ", missing.Take(3))} and {missing.Count - 3} more";

    private static string SqliteVersion()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT sqlite_version();";
        return Convert.ToString(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    /// <summary>A sentence when the file (or, where there is none yet, the nearest folder that exists) cannot be written to by this account; null when it can.</summary>
    internal static string? WritableProblem(string file)
    {
        try
        {
            if (File.Exists(file))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"The state file {file} cannot be written by this account: give the account that runs the service read and write access to it (and to its folder).";
        }

        var folder = Path.GetDirectoryName(file)!;
        while (!Directory.Exists(folder) && Path.GetDirectoryName(folder) is { Length: > 0 } parent) folder = parent;
        var probe = Path.Combine(folder, $".dbdatakit-write-probe-{Guid.NewGuid():N}");
        try
        {
            using (File.Create(probe)) { }
            File.Delete(probe);
            return null;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return $"The folder {folder} cannot be written by this account (the state file {file} needs it for its journal): give the account that runs the service write access to it.";
        }
    }
}
