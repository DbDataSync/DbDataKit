using DbDataKit.Cli;

namespace DbDataKit.State.Cli;

/// <summary>A module the registry of a state database lists: what it was applied at.</summary>
public sealed record FoundModule(string Module, string Scope, int Version, string AppliedBy);

/// <summary>What <see cref="StateConnectionCheck.Run"/> found about one state database.</summary>
/// <param name="Engine">The engine id.</param>
/// <param name="Ok">True when every check passed (what <c>state test</c> would exit 0 for) and, for a move target, the database is empty.</param>
/// <param name="Steps">The steps of <c>state test</c> that ran, ending at the first failure, then the empty check of a move.</param>
/// <param name="Sentence">One sentence: what is fine, or for a failure the fix. Never holds a password.</param>
/// <param name="Reachable">The connection step passed.</param>
/// <param name="Empty">The registry is not there or lists no module.</param>
/// <param name="Modules">What the registry lists (the module versions found there); empty when it could not be read.</param>
public sealed record StateConnectionReport(string Engine, bool Ok, IReadOnlyList<StateTestStep> Steps, string Sentence, bool Reachable, bool Empty, IReadOnlyList<FoundModule> Modules);

/// <summary>
/// The checks of <c>state test</c> on one database as a library call (plan 29b, C06), for a console that holds state databases as connections: the same checks, run by the same code (<see cref="StateChecks"/>), with the answer as one sentence naming the fix. For
/// the target of a move it also requires the database to be empty. The credentials are resolved through the target's own resolver and hidden again in every sentence.
/// </summary>
public static class StateConnectionCheck
{
    /// <param name="target">The database as a product's settings name it (its connection string may hold secret references the target's resolver knows).</param>
    /// <param name="program">The command's name, in the sentences that name a fix.</param>
    /// <param name="product">What the state belongs to, for the sentences.</param>
    /// <param name="modules">The modules whose tables and engine features are checked.</param>
    /// <param name="forMove">The database is the target of a move: it has to be empty, and the login has to be able to create the tables.</param>
    /// <param name="scope">The group and service the layout is made for.</param>
    /// <param name="seams">For tests: the context's seams (an engine version, a feature probe).</param>
    public static StateConnectionReport Run(StateTarget target, string program, string product, IReadOnlyList<StateModule> modules, StateScope scope, bool forMove, StateVerbContext? seams = null)
    {
        var context = (seams ?? new StateVerbContext(program, product, _ => target, modules, scope, [], "")) with { Locate = _ => target, Modules = modules, Program = program, Product = product, Scope = scope };
        StateSession session;
        try { session = StateSession.Create(context, CliArgs.Parse([], new HashSet<string>(), new HashSet<string>())); }
        catch (StateSettingsProblem ex)
        {
            var step = new StateTestStep("settings", "failed", ex.Message);
            return new StateConnectionReport(Engine(target), false, [step], ex.Message, false, false, []);
        }

        var report = StateChecks.Run(context, session);
        var steps = report.Steps.Select(s => s with { Message = session.Scrub(s.Message) }).ToList();
        var reachable = steps.Any(s => s.Name == "connection" && s.Status != "failed");
        var found = new List<FoundModule>();
        var empty = false;
        if (reachable && (session.IsSqlite ? File.Exists(session.File) : true))
        {
            try
            {
                using var inspector = StateInspector.Open(session.Layout, session.ResolveTarget());
                var rows = inspector.ReadRegistry();
                empty = rows is null || rows.Count == 0;
                found.AddRange((rows ?? []).Select(r => new FoundModule(r.Module, r.Scope, r.Version, r.AppliedBy)));
            }
            catch (Exception ex) when (StateDatabaseErrors.IsProviderError(ex) || ex is InvalidOperationException) { /* the checks above already said why; the modules are unknown */ }
        }
        else if (reachable) empty = true;

        var sentence = report.Failed is { } failed ? failed.Message : $"The connection works ({steps.Count} checks passed).";
        var ok = report.Ok;
        if (ok)
        {
            if (forMove && !empty)
            {
                var message = $"The database already holds {string.Join(", ", found.Select(m => m.Module).Distinct())}: a move goes into an empty database. Create a new empty database and point the connection at it.";
                steps.Add(new StateTestStep("empty", "failed", message));
                sentence = message;
                ok = false;
            }
            else
            {
                if (forMove) steps.Add(new StateTestStep("empty", "ok", "the database is empty"));
                var notes = steps.Where(s => s.Status == "note").Select(s => s.Message).ToList();
                sentence = empty
                    ? "The connection works and the database is empty; it can take a state."
                    : $"The connection works; the registry lists {found.Count} module(s).";
                if (notes.Count > 0 && !empty) sentence += " " + notes[^1];
            }
        }

        return new StateConnectionReport(session.Engine, ok, steps, session.Scrub(sentence), reachable, empty, found);
    }

    private static string Engine(StateTarget target)
    {
        try { return target.Settings.EngineId; }
        catch (InvalidOperationException) { return target.Settings.Engine ?? ""; }
    }
}
