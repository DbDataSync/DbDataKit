using System.Data.Common;
using DbDataKit.Secrets;

namespace DbDataKit.State.Cli;

/// <summary>The three places a suite's state databases can be (plan 29 section 5).</summary>
public enum StateTopologyKind
{
    /// <summary>Each service its own SQLite file in its own directory: what a single machine gets with no settings at all.</summary>
    Service,

    /// <summary>One SQL Server or PostgreSQL database for a serving group's or a runner group's instances.</summary>
    Group,

    /// <summary>One SQL Server or PostgreSQL database for every service of the install, each service's modules in namespaces of their own.</summary>
    Install,
}

/// <summary>Raised for arguments of <c>state init</c> that cannot make settings; the message is the sentence that names the fix.</summary>
public sealed class StateTopologyException(string message) : ArgumentException(message);

/// <summary>
/// What each topology means as a service's <see cref="StateSettings"/> (plan 29b C03). The presets are data and pure functions: <see cref="Settings"/> works out the <c>state</c> section one service should have,
/// <see cref="ScopeFor"/> the group and service names the layout and the grants use. Nothing here opens a connection or writes a file; <c>state init</c> does, and the installers call that.
/// <list type="bullet">
/// <item><c>service</c>: <c>engine: sqlite</c> and nothing else, the namespaces and retention the file already had.</item>
/// <item><c>group</c> and <c>install</c>: a server engine and a connection whose password is a secret reference, with the namespaces of the layout's defaults (a schema per module, <c>dbdatakit_&lt;module&gt;</c>, and the scope's
/// name added for a module kept per group or per service); namespaces the operator already customised for the same engine are kept. They differ in what the scope says: a group database is one group's, an install database
/// holds every service's modules, which the service name in the service-scoped namespaces keeps apart.</item>
/// </list>
/// </summary>
public static class StateTopology
{
    public static bool TryParse(string? text, out StateTopologyKind kind)
    {
        kind = default;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "service": kind = StateTopologyKind.Service; return true;
            case "group": kind = StateTopologyKind.Group; return true;
            case "install": kind = StateTopologyKind.Install; return true;
            default: return false;
        }
    }

    /// <summary>The canonical engine name settings are written with: <c>sqlite</c>, <c>mssql</c> or <c>postgres</c>.</summary>
    public static string EngineName(string engineId) => engineId == StateEngineIds.MsSql ? "mssql" : engineId == StateEngineIds.Postgres ? "postgres" : "sqlite";

    /// <summary>
    /// The settings a service in <paramref name="kind"/> should have, keeping the retention (and, for the same server engine, the namespaces) of <paramref name="current"/>.
    /// </summary>
    /// <param name="kind">The topology.</param>
    /// <param name="current">What the service has now.</param>
    /// <param name="program">The command's name, for the sentence that says how to store a secret.</param>
    /// <param name="engine">For <c>group</c> and <c>install</c>: <c>mssql</c> or <c>postgres</c> (<c>sqlserver</c> and <c>postgresql</c> too). Ignored for <c>service</c>.</param>
    /// <param name="connection">For <c>group</c> and <c>install</c>: a connection string whose password is a secret reference (or the whole string a reference). A literal password is refused.</param>
    /// <exception cref="StateTopologyException">The arguments cannot make working settings.</exception>
    public static StateSettings Settings(StateTopologyKind kind, StateSettings current, string? engine, string? connection, string program)
    {
        if (kind == StateTopologyKind.Service)
            return new StateSettings("sqlite") { Namespaces = current.IsSqlite ? current.Namespaces : StateNamespaceSettings.Defaults, Retention = current.Retention };

        var which = kind.ToString().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(engine))
            throw new StateTopologyException($"--topology {which} needs --engine mssql or --engine postgres.");
        string id;
        try { id = new StateSettings(engine).EngineId; }
        catch (InvalidOperationException) { throw new StateTopologyException($"--engine '{engine}' is not one of mssql or postgres."); }
        if (id == StateEngineIds.Sqlite)
            throw new StateTopologyException($"--topology {which} is a SQL Server or PostgreSQL database shared by several instances: use --engine mssql or --engine postgres (a SQLite file for each service is --topology service).");
        if (string.IsNullOrWhiteSpace(connection))
            throw new StateTopologyException($"--topology {which} needs --connection: the connection string of the database, with its password as a secret reference, for example --connection \"Host=db;Database=dbdatakit;Username=svc;Password=secret:state-db\" or --connection env:DBDATAKIT_STATE_CONNECTION.");
        RequireReferencedPassword(connection, "--connection", program);

        var keep = !current.IsSqlite && current.EngineId == id;
        return new StateSettings(EngineName(id), connection.Trim()) { Namespaces = keep ? current.Namespaces : StateNamespaceSettings.Defaults, Retention = current.Retention };
    }

    /// <summary>Refuses a connection string with a password written in it, in a sentence that names the two ways to write it as a reference and how to store the secret.</summary>
    public static void RequireReferencedPassword(string connection, string option, string program)
    {
        if (ConnectionStringReferences.IsWholeReference(connection.Trim(), out _)) return;
        if (ConnectionStringReferences.CredentialValues(connection).Count == 0) return;
        throw new StateTopologyException(
            $"{option} has a password written in it, and a password is never written into a settings file or a command line. Store it as a secret ('{program} secret set state-db' reads the value from a prompt or standard input) and write Password=secret:state-db in the connection string, " +
            $"or put it in an environment variable and write Password=env:NAME; a whole connection string can be one reference too ({option} env:NAME or {option} secret:NAME).");
    }

    /// <summary>
    /// The group and service names a layout is made with: <paramref name="group"/> (the <c>--group</c> option) over the product's own, <c>main</c> when neither says, and the service's own name or the program's.
    /// The names must match what the running product uses, or its modules would be looked for in other namespaces.
    /// </summary>
    public static StateScope ScopeFor(StateTopologyKind kind, StateScope product, string? group, string program) =>
        kind switch
        {
            StateTopologyKind.Install => new StateScope(Valid(group ?? product.Group ?? "main", "--group"), Valid(product.Service ?? program, "the service name")),
            _ => new StateScope(Valid(group ?? product.Group ?? "main", "--group"), product.Service ?? "main"),
        };

    private static string Valid(string name, string what) =>
        name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !char.IsAsciiDigit(name[0])
            ? name
            : throw new StateTopologyException($"{what} '{name}' is not a plain name (letters, digits and underscores, not starting with a digit): it becomes part of a schema name.");

    /// <summary>True when two settings put the state in the same place with the same namespaces (what <c>state init</c> calls "nothing to change").</summary>
    public static bool Same(StateSettings a, StateSettings b) =>
        a.EngineId == b.EngineId && (a.IsSqlite || string.Equals(a.ConnectionString?.Trim(), b.ConnectionString?.Trim(), StringComparison.Ordinal)) &&
        string.Equals(Normalized(a), Normalized(b), StringComparison.Ordinal);

    private static string Normalized(StateSettings s) => StateSettingsWriter.Render(new StateSettings(EngineName(s.EngineId)) { Namespaces = s.Namespaces, Retention = s.Retention });

    /// <summary>The statement that creates a schema when it is not there, for the engine; empty for SQLite.</summary>
    public static string CreateSchema(string engine, string schema) => engine switch
    {
        StateEngineIds.Postgres => $"CREATE SCHEMA IF NOT EXISTS {schema};",
        StateEngineIds.MsSql => $"IF SCHEMA_ID(N'{schema}') IS NULL EXEC(N'CREATE SCHEMA [{schema}]');",
        _ => "",
    };

    internal static DbConnection Connect(string engine, string connectionString)
    {
        var connection = StateDialect.For(engine).CreateConnection(connectionString);
        try { connection.Open(); }
        catch { connection.Dispose(); throw; }
        return connection;
    }
}
