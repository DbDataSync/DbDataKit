using DbDataKit.Cli;

namespace DbDataKit.State.Cli;

/// <summary>The <c>state grants</c> verb: the statements <see cref="StateGrants"/> writes for the product's layout. It behaves as the console's own verb did before it moved here: 2 for a wrong use, a login that is not a plain name, or an engine with no rights to grant (SQLite).</summary>
internal static class StateGrantsVerb
{
    public static async Task<int> RunAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "migrate" }, new HashSet<string>(context.Options.Append("login")));
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("login") is not { Length: > 0 } login)
        {
            await output.Error.WriteLineAsync($"Usage: state grants --login <database role or user> [--migrate] {context.OptionsUsage}".TrimEnd());
            return CliExit.Usage;
        }

        try
        {
            var target = context.Locate(parsed);
            var layout = StateLayout.Create(target.Settings.EngineId, context.Modules ?? [], context.Scope, target.Settings.Namespaces);
            var statements = StateGrants.Statements(layout, login, parsed.Has("migrate") ? GrantLevel.Migrate : GrantLevel.Runtime);
            foreach (var statement in statements) await output.Out.WriteLineAsync(statement);
            output.Data["statements"] = statements.ToList();
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            await output.Error.WriteLineAsync(ex.Message);
            return CliExit.Usage;
        }
    }
}
