using DbDataKit.Cli;

namespace DbDataKit.State.Cli;

/// <summary>The <c>state grants</c> verb: the statements <see cref="StateGrants"/> writes for the product's layout. It behaves as the console's own verb did before it moved here: 2 for a wrong use, a login that is not a plain name, or an engine with no rights to grant (SQLite).</summary>
internal static class StateGrantsVerb
{
    public static async Task<int> RunAsync(StateVerbContext context, string[] args, CliContext output)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string> { "migrate" }, new HashSet<string>(context.Options.Append("login").Append("group")));
        var group = parsed.Get("group") ?? context.Scope.Group;
        var login = parsed.Get("login") is { Length: > 0 } named ? named : StateLogins.Name(context.Scope.Service, group, context.Program);
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Has("login") && parsed.Get("login") is not { Length: > 0 })
        {
            await output.Error.WriteLineAsync($"Usage: state grants [--login <database role or user>] [--group <name>] [--migrate] {context.OptionsUsage}".TrimEnd());
            return CliExit.Usage;
        }

        try
        {
            var target = context.Locate(parsed);
            var layout = StateLayout.Create(target.Settings.EngineId, context.Modules ?? [], context.Scope with { Group = group }, target.Settings.Namespaces);
            var statements = StateGrants.Statements(layout, login, parsed.Has("migrate") ? GrantLevel.Migrate : GrantLevel.Runtime);
            await output.Out.WriteLineAsync($"-- login {login}: the one login of {context.Product}{(group is { Length: > 0 } ? $", group {group}" : "")}; it is granted these schemas and no others");
            foreach (var statement in statements) await output.Out.WriteLineAsync(statement);
            output.Data["login"] = login;
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
