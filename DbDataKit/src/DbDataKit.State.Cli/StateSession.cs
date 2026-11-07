using System.Data.Common;
using DbDataKit.Cli;
using DbDataKit.Secrets;

namespace DbDataKit.State.Cli;

/// <summary>Raised for settings that cannot be used; the message is the sentence that names the fix.</summary>
internal sealed class StateSettingsProblem(string message) : Exception(message);

/// <summary>
/// One run of <c>state show</c> or <c>state test</c> against one product's state: the effective settings, the layout they and the product's modules make, and the connection as it may be shown. Nothing is
/// opened until a verb asks. The password is resolved only for <see cref="ResolveTarget"/>, and every message that leaves here goes through <see cref="Scrub"/>, which removes the resolved credentials.
/// </summary>
internal sealed class StateSession
{
    private readonly List<string> _secrets = [];

    private StateSession(StateVerbContext context, StateTarget target, StateLayout layout)
    {
        Context = context;
        Target = target;
        Layout = layout;
    }

    public StateVerbContext Context { get; }

    public StateTarget Target { get; }

    public StateLayout Layout { get; }

    public StateSettings Settings => Target.Settings;

    public string Engine => Layout.Engine;

    public bool IsSqlite => Engine == StateEngineIds.Sqlite;

    public string File => Path.GetFullPath(Target.SqlitePath);

    /// <summary>The connection string with every secret masked and every reference left as written; the SQLite file for SQLite.</summary>
    public string Connection => IsSqlite ? File : ConnectionStringReferences.Mask(Settings.ConnectionString);

    /// <summary>The registry's name as the layout places it.</summary>
    public string Registry => Layout.Registry;

    /// <summary>Reads the settings and works out the layout, or raises a <see cref="StateSettingsProblem"/> naming what is wrong.</summary>
    public static StateSession Create(StateVerbContext context, CliArgs args)
    {
        StateTarget target;
        try { target = context.Locate(args); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException) { throw new StateSettingsProblem(ex.Message); }

        string engine;
        try { engine = target.Settings.EngineId; }
        catch (InvalidOperationException ex) { throw new StateSettingsProblem($"{ex.Message} Set state.engine (or the product's option for it) to one of them."); }

        if (engine != StateEngineIds.Sqlite && string.IsNullOrWhiteSpace(target.Settings.ConnectionString))
            throw new StateSettingsProblem($"The state engine is {target.Settings.Engine}, which needs a connection string: set state.connection (a password can be a reference such as Password=secret:state-db).");

        StateLayout layout;
        try { layout = StateLayout.Create(engine, context.Modules ?? [], context.Scope, target.Settings.Namespaces); }
        catch (InvalidOperationException ex) { throw new StateSettingsProblem(ex.Message); }
        return new StateSession(context, target, layout);
    }

    /// <summary>What <c>StateInspector.Open</c> takes: the file for SQLite, the connection string with its secret references resolved for the others. Registers the credentials it resolved for <see cref="Scrub"/>.</summary>
    public string ResolveTarget()
    {
        if (IsSqlite) return File;
        var text = Settings.ConnectionString!;
        string resolved;
        try { resolved = StateOpener.ResolveConnectionString(text, Target.Secrets?.Invoke()); }
        catch (InvalidOperationException ex)
        {
            throw new StateSettingsProblem($"{ex.Message} Store the secret ({Context.Program} secret set <name>) or set the environment variable it names.");
        }

        _secrets.AddRange(ConnectionStringReferences.CredentialValues(resolved));
        if (ConnectionStringReferences.IsWholeReference(text, out _)) _secrets.Add(resolved);
        return resolved;
    }

    /// <summary>The text with every credential this run resolved replaced by <c>***</c>, in case a provider quotes one in a message.</summary>
    public string Scrub(string text)
    {
        foreach (var secret in _secrets.Where(s => s.Length >= 3).OrderByDescending(s => s.Length))
            text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }

    /// <summary>The server, login and database of the connection string as written (references and masks included), for a sentence that says which.</summary>
    public (string? Server, string? User, string? Database) Parts()
    {
        if (IsSqlite) return (null, null, null);
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = Connection };
            return (First(builder, "host", "server", "data source", "address", "addr", "network address"), First(builder, "username", "user id", "uid", "user"), First(builder, "database", "initial catalog"));
        }
        catch (ArgumentException) { return (null, null, null); }
    }

    private static string? First(DbConnectionStringBuilder builder, params string[] keys) =>
        keys.Select(k => builder.TryGetValue(k, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null).FirstOrDefault(v => !string.IsNullOrEmpty(v));

    /// <summary>The exit code and the one sentence for an exception raised while connecting or reading.</summary>
    public (int Exit, string Sentence) Describe(Exception exception)
    {
        var (server, user, database) = Parts();
        var kind = StateDatabaseErrors.Classify(exception);
        var where = server is null ? "the server" : $"the server '{server}'";
        return kind switch
        {
            StateErrorKind.Unreachable => (StateExit.Unreachable, $"Cannot reach {where}: start the database server, or correct the host and port in state.connection."),
            StateErrorKind.LoginRefused => (StateExit.LoginRefused, $"The login{(user is null ? "" : $" '{user}'")} was refused by {where} (a wrong password or an unknown login): correct the password in state.connection (the secret it refers to), or create the login."),
            StateErrorKind.DatabaseMissing => (StateExit.Unreachable, $"The database{(database is null ? "" : $" '{database}'")} does not exist on {where}: create it, or correct the database name in state.connection."),
            StateErrorKind.PermissionDenied => (StateExit.Registry, $"The login may not read the state registry {Registry}: ask a database administrator to run '{Context.Program} state grants --login <the login>'."),
            _ => (StateExit.Unreachable, IsSqlite
                ? $"The state file {File} cannot be opened ({Scrub(FirstLine(exception))}): check that it is a SQLite database this account may read, and that no other program has it locked."
                : $"The connection failed ({Scrub(FirstLine(exception))}): check state.connection."),
        };
    }

    private static string FirstLine(Exception exception)
    {
        var text = (exception.InnerException?.Message is { Length: > 0 } inner && exception.Message.Length == 0 ? inner : exception.Message).Trim();
        var end = text.IndexOfAny(['\r', '\n']);
        return end < 0 ? text : text[..end];
    }
}
