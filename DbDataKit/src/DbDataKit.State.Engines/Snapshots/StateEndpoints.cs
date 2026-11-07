using System.Data.Common;
using DbDataKit.Secrets;

namespace DbDataKit.State.Engines.Snapshots;

/// <summary>
/// One state database as a command line or a console names it: the <see cref="StateSettings"/> of a product's <c>state</c> section, and, for SQLite, the file (SQLite is "the file in the product's own
/// directory", which the settings do not hold). A settings value converts to an endpoint without a file, which is enough for a server engine.
/// </summary>
/// <param name="Settings">Engine, connection (a secret reference stays a reference), namespaces.</param>
/// <param name="SqliteFile">The SQLite file; required when the engine is SQLite.</param>
public sealed record StateEndpoint(StateSettings Settings, string? SqliteFile = null)
{
    public static implicit operator StateEndpoint(StateSettings settings) => new(settings);

    /// <summary>The engine id (<see cref="StateEngineIds"/>).</summary>
    public string Engine => Settings.EngineId;

    /// <summary>A phrase for messages that never holds the connection string: the engine, and the file for SQLite.</summary>
    public string Describe() => Settings.IsSqlite ? $"the SQLite state database '{SqliteFile}'" : $"the {EngineName(Engine)} state database";

    internal static string EngineName(string engine) => engine switch
    {
        StateEngineIds.MsSql => "SQL Server",
        StateEngineIds.Postgres => "PostgreSQL",
        _ => "SQLite",
    };
}

/// <summary>Why the snapshot service (or a move) refused or stopped, for a caller that wants to react to the kind and not the words.</summary>
public enum StateServiceRefusal
{
    /// <summary>The engine is SQLite and no file was given.</summary>
    SqlitePathMissing,

    /// <summary>The SQLite file to read does not exist.</summary>
    SourceMissing,

    /// <summary>The database has no registry (it is empty, or not a state database), or lacks a module this software has.</summary>
    NotAStateDatabase,

    /// <summary>A module of the database is older than this software's: it has to be migrated (started once by the product) before it can be exported or compared.</summary>
    NeedsMigration,

    /// <summary>A module of the database is newer than this software's.</summary>
    DatabaseNewer,

    /// <summary>The server could not be reached, or refused the login.</summary>
    ConnectionFailed,

    /// <summary>A secret reference in the connection string is not known to any secret provider.</summary>
    SecretMissing,

    /// <summary>The target already holds modules: an import goes into an empty database.</summary>
    TargetNotEmpty,

    /// <summary>The source and the target are the same database.</summary>
    SameDatabase,

    /// <summary>The snapshot is not intact (a file differs from its manifest).</summary>
    SnapshotNotIntact,

    /// <summary>The settings to write are not valid, or the settings file cannot be written.</summary>
    SettingsInvalid,
}

/// <summary>A refusal of the snapshot service; the message names the database or file and what to do. It never holds a password.</summary>
public sealed class StateServiceException(StateServiceRefusal reason, string message, Exception? inner = null) : InvalidOperationException(message, inner)
{
    public StateServiceRefusal Reason { get; } = reason;
}

/// <summary>An <see cref="IProgress{T}"/> that runs the callback on the reporting thread, in order (<see cref="Progress{T}"/> posts to a context).</summary>
public sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Hides the values of the secrets a connection string refers to, in any text that is about to be shown.</summary>
internal sealed class Redactor
{
    private readonly List<string> _secrets = [];

    public Redactor(ISecretResolver? secrets, params StateEndpoint[] endpoints)
    {
        foreach (var endpoint in endpoints) Add(endpoint, secrets);
    }

    public void Add(StateEndpoint endpoint, ISecretResolver? secrets)
    {
        string? resolved;
        try { resolved = StateOpener.ResolveConnectionString(endpoint.Settings, secrets); }
        catch (InvalidOperationException) { return; }
        if (resolved is null) return;

        _secrets.Add(resolved);
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = resolved };
            foreach (var key in new[] { "Password", "Pwd" })
            {
                if (builder.TryGetValue(key, out var value) && value?.ToString() is { Length: >= 3 } text) _secrets.Add(text);
            }
        }
        catch (ArgumentException) { /* a string the builder cannot parse: the whole string is still hidden */ }
    }

    public string Apply(string text)
    {
        foreach (var secret in _secrets.OrderByDescending(s => s.Length)) text = text.Replace(secret, "***", StringComparison.Ordinal);
        return text;
    }
}

/// <summary>Opens endpoints the way a snapshot needs them: not migrated, checked, and with a message that names the fix.</summary>
internal static class StateEndpoints
{
    public enum Role
    {
        /// <summary>Read: the database must exist, be a state database and be at this software's versions.</summary>
        Read,

        /// <summary>Written by an import: may be new or empty.</summary>
        Target,
    }

    public static StateDatabase Open(StateEndpoint endpoint, IReadOnlyList<StateModule> modules, StateScope scope, Role role, ISecretResolver? secrets, Redactor redactor, string? appliedBy, TimeProvider? time)
    {
        var settings = endpoint.Settings;
        string engine;
        try { engine = settings.EngineId; }
        catch (InvalidOperationException ex) { throw new StateServiceException(StateServiceRefusal.ConnectionFailed, ex.Message); }

        if (engine == StateEngineIds.Sqlite)
        {
            if (string.IsNullOrWhiteSpace(endpoint.SqliteFile))
                throw new StateServiceException(StateServiceRefusal.SqlitePathMissing, "The state engine is SQLite, which is a file: give the path of the SQLite state file.");
            if (role == Role.Read && !File.Exists(endpoint.SqliteFile))
                throw new StateServiceException(StateServiceRefusal.SourceMissing, $"The SQLite state file '{endpoint.SqliteFile}' does not exist; check the path (the file is in the product's data directory).");
            if (role == Role.Target && Path.GetDirectoryName(Path.GetFullPath(endpoint.SqliteFile)) is { Length: > 0 } directory) Directory.CreateDirectory(directory);
        }

        StateDatabase database;
        try { database = StateOpener.OpenWithoutMigrating(settings, endpoint.SqliteFile, modules, scope, secrets, appliedBy, time); }
        catch (InvalidOperationException ex) when (ex.Message.Contains("secret", StringComparison.OrdinalIgnoreCase) && ex.InnerException is SecretNotFoundException)
        {
            throw new StateServiceException(StateServiceRefusal.SecretMissing, ex.Message + " Add the secret (or fix the reference in the state connection) and retry.");
        }
        catch (InvalidOperationException ex)
        {
            throw new StateServiceException(StateServiceRefusal.ConnectionFailed, redactor.Apply(ex.Message));
        }

        IReadOnlyList<StateRegisteredModule> registered;
        try { registered = database.RegisteredModules(); }
        catch (Exception ex) when (ex is DbException or InvalidOperationException)
        {
            throw new StateServiceException(StateServiceRefusal.ConnectionFailed,
                redactor.Apply($"Could not read {endpoint.Describe()}: {ex.Message} Check the state connection (the server, the database, the login and its rights) with 'state test'."));
        }

        if (role == Role.Read) CheckCurrent(endpoint, database, registered);
        else if (registered.Count > 0)
            throw new StateServiceException(StateServiceRefusal.TargetNotEmpty,
                $"The target ({endpoint.Describe()}) already holds the module{(registered.Count > 1 ? "s" : "")} {string.Join(", ", registered.Select(r => r.Module))}: an import goes into an empty database. Create a new empty database (or remove these tables) and retry.");
        return database;
    }

    /// <summary>The registry has to hold every module of the layout at this software's version: a snapshot or a comparison is of a database that was started by a product of this version.</summary>
    private static void CheckCurrent(StateEndpoint endpoint, StateDatabase database, IReadOnlyList<StateRegisteredModule> registered)
    {
        if (registered.Count == 0)
            throw new StateServiceException(StateServiceRefusal.NotAStateDatabase, $"{Capital(endpoint.Describe())} has no module registry: it is empty or not a state database. Check that the settings name the database the product really uses.");

        foreach (var ns in database.Layout!.Namespaces)
        {
            var scope = ns.ScopeName ?? "install";
            var row = registered.FirstOrDefault(r => r.Module == ns.Module.Name && r.Scope == scope);
            if (row is null)
                throw new StateServiceException(StateServiceRefusal.NeedsMigration,
                    $"{Capital(endpoint.Describe())} does not have the module '{ns.Module.Name}' yet. Start the product once (it creates and migrates its modules), then retry.");
            if (row.Version < ns.Module.Version)
                throw new StateServiceException(StateServiceRefusal.NeedsMigration,
                    $"The module '{ns.Module.Name}' in {endpoint.Describe()} is at version {row.Version} and this software is at {ns.Module.Version}. Start the product once so it migrates the database, then retry.");
            if (row.Version > ns.Module.Version)
                throw new StateServiceException(StateServiceRefusal.DatabaseNewer,
                    $"The module '{ns.Module.Name}' in {endpoint.Describe()} is at version {row.Version}, newer than this software's {ns.Module.Version}. Use the version of the tool that matches the product.");
        }
    }

    private static string Capital(string text) => char.ToUpperInvariant(text[0]) + text[1..];

    /// <summary>True when two endpoints name the same database (the same SQLite file, or the same connection string).</summary>
    public static bool Same(StateEndpoint a, StateEndpoint b)
    {
        if (a.Engine != b.Engine) return false;
        if (a.Settings.IsSqlite)
            return a.SqliteFile is not null && b.SqliteFile is not null && string.Equals(Path.GetFullPath(a.SqliteFile), Path.GetFullPath(b.SqliteFile), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return string.Equals(a.Settings.ConnectionString?.Trim(), b.Settings.ConnectionString?.Trim(), StringComparison.Ordinal);
    }
}
