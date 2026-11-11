using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.Secrets;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using DbDataKit.State.Cli;

namespace DbDataKit.Server;

/// <summary>
/// A state database the console knows by name (plan 29b, C06, M8): where a server's state can be moved to. The connection string never holds a password: it holds the reference <c>Password=secret:dbdatakit:server:state:&lt;name&gt;</c>, and the
/// password itself is in the console's secret store like the key it signs with.
/// </summary>
/// <param name="Name">Lower-case letters, digits and dashes.</param>
/// <param name="Engine"><c>postgres</c>, <c>mssql</c> or <c>sqlite</c>.</param>
/// <param name="Connection">The connection string of a server engine, with the password as a secret reference; null for SQLite.</param>
/// <param name="Path">The SQLite file on the console's machine; null for a server engine.</param>
/// <param name="Note">What it is for, for the list.</param>
public sealed record StateConnection(string Name, string Engine, string? Connection, string? Path, string? Note);

/// <summary>The state connections of a console, in <c>state-connections.json</c> in its directory (no secret in it: references only) and the passwords in its secret store.</summary>
public sealed partial class ConsoleStateConnections(string directory, EncryptedFileSecretStore secrets)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _lock = new();

    public string File => System.IO.Path.Combine(directory, "state-connections.json");

    [GeneratedRegex("^[a-z0-9][a-z0-9-]{0,39}$")]
    private static partial Regex NameRegex();

    public static bool ValidName(string? name) => name is not null && NameRegex().IsMatch(name);

    /// <summary>The secret that holds the password of a connection: the name the console stores it under, and the name a server needs it under (<c>dbdatastore secret set dbdatakit:server:state:&lt;name&gt;</c>).</summary>
    public static SecretName SecretNameOf(string name) => new("server", "state", name);

    public static string ReferenceOf(string name) => SecretNameOf(name).ToRef().ToString();

    public IReadOnlyList<StateConnection> All()
    {
        lock (_lock)
        {
            if (!System.IO.File.Exists(File)) return [];
            try { return JsonSerializer.Deserialize<List<StateConnection>>(System.IO.File.ReadAllText(File), Json) ?? []; }
            catch (JsonException ex) { throw new InvalidOperationException($"{File} is not a list of state connections ({ex.Message}); fix or remove the file.", ex); }
        }
    }

    public StateConnection? Find(string name) => All().FirstOrDefault(c => c.Name == name);

    /// <summary>Adds or replaces a connection. The password (when given) goes into the secret store and the connection string refers to it.</summary>
    public async Task<StateConnection> SaveAsync(string name, string engine, string? connection, string? sqlitePath, string? password, string? note, CancellationToken ct)
    {
        if (!ValidName(name)) throw new ArgumentException("The name is lower-case letters, digits and dashes, starting with a letter or digit (at most 40).");
        var id = StateEngine(engine);
        StateConnection entry;
        var existing = Find(name);
        if (id == StateEngineIds.Sqlite)
        {
            if (string.IsNullOrWhiteSpace(sqlitePath) || !System.IO.Path.IsPathRooted(sqlitePath)) throw new ArgumentException("A SQLite state is a file on this console's machine: give its full path.");
            entry = new StateConnection(name, "sqlite", null, System.IO.Path.GetFullPath(sqlitePath), Clean(note));
        }
        else
        {
            if (string.IsNullOrWhiteSpace(connection)) throw new ArgumentException("The connection string is needed (server, database and login; the password goes in its own field).");
            DbConnectionStringBuilder builder;
            try { builder = new DbConnectionStringBuilder { ConnectionString = connection }; }
            catch (ArgumentException) { throw new ArgumentException("The connection string cannot be read: write it as Key=Value;Key=Value."); }
            foreach (var key in new[] { "Password", "Pwd" })
                if (builder.ContainsKey(key)) throw new ArgumentException("Leave the password out of the connection string: type it in the password field, and the console keeps it in its secret store.");
            // a connection that had a password keeps its reference when the password field is left empty
            var hadPassword = existing?.Connection is { } old && old.Contains(ReferenceOf(name), StringComparison.Ordinal);
            if (!password.IsNullOrEmpty() || hadPassword) builder["Password"] = ReferenceOf(name);

            entry = new StateConnection(name, id == StateEngineIds.MsSql ? "mssql" : "postgres", builder.ConnectionString, null, Clean(note));
            if (!password.IsNullOrEmpty()) await secrets.SetAsync(SecretNameOf(name), SecretValue.From(password!), ct);
        }

        lock (_lock)
        {
            var list = All().Where(c => c.Name != name).Append(entry).OrderBy(c => c.Name, StringComparer.Ordinal).ToList();
            System.IO.Directory.CreateDirectory(directory);
            var temporary = File + ".tmp";
            System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(list, Json));
            System.IO.File.Move(temporary, File, overwrite: true);
        }

        return entry;
    }

    public async Task<bool> RemoveAsync(string name, CancellationToken ct)
    {
        lock (_lock)
        {
            var list = All().ToList();
            if (list.RemoveAll(c => c.Name == name) == 0) return false;
            System.IO.File.WriteAllText(File, JsonSerializer.Serialize(list, Json));
        }

        try { await secrets.DeleteAsync(SecretNameOf(name), ct); }
        catch (Exception ex) when (ex is IOException or KeyNotFoundException) { /* no password was stored */ }
        return true;
    }

    private static string? Clean(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 200)];

    private static string StateEngine(string engine)
    {
        try { return new StateSettings(engine, null).EngineId; }
        catch (InvalidOperationException) { throw new ArgumentException("The engine is postgres, mssql or sqlite."); }
    }

    /// <summary>The connection as the state libraries take it: the settings, the SQLite file, and the console's secret store then the environment to resolve the password reference.</summary>
    public StateTarget TargetOf(StateConnection connection) => new(
        new StateSettings(connection.Engine, connection.Connection) { Namespaces = StateNamespaceSettings.Defaults },
        connection.Path ?? System.IO.Path.Combine(directory, "unused-state.db"),
        () => new SecretResolverChain([secrets, new EnvSecretProvider()]));

    /// <summary>What <c>state test</c> finds on this connection, run by the shared library with the console's own modules: connect, the registry readable or empty, the rights to create the tables, the engine version and the features.</summary>
    public StateConnectionReport Test(StateConnection connection) =>
        StateConnectionCheck.Run(TargetOf(connection), "dbdatakit", "the console", ConsoleState.Modules, StateScope.Install, forMove: false);

    /// <summary>A short, comparable identity of a database as a connection string names it: engine, server, port and database. Passwords and references never take part.</summary>
    public static string IdentityOf(string? engine, string? connection, string? sqlitePath = null)
    {
        var id = (engine ?? "").ToLowerInvariant() switch { "postgresql" => "postgres", "sqlserver" or "sql server" => "mssql", var e => e };
        if (id == "sqlite") return "sqlite|" + (sqlitePath is null ? "" : System.IO.Path.GetFullPath(sqlitePath));
        string? server = null, database = null, port = null;
        try
        {
            var builder = new DbConnectionStringBuilder { ConnectionString = connection ?? "" };
            string? First(params string[] keys) => keys.Select(k => builder.TryGetValue(k, out var v) ? Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture) : null).FirstOrDefault(v => !string.IsNullOrEmpty(v));
            server = First("host", "server", "data source", "address", "addr", "network address");
            port = First("port");
            database = First("database", "initial catalog");
        }
        catch (ArgumentException) { return "?" + id; }

        // SQL Server writes the port after a comma: "host,1433"
        if (port is null && server is not null && server.Contains(',')) { var parts = server.Split(',', 2); server = parts[0]; port = parts[1].Trim(); }
        server = (server ?? "").Trim().ToLowerInvariant();
        if (server is "localhost" or "::1" or "(local)" or ".") server = "127.0.0.1";
        port ??= id == "mssql" ? "1433" : "5432";
        return $"{id}|{server}:{port}|{(database ?? "").ToLowerInvariant()}";
    }
}

internal static class StringExtensions
{
    public static bool IsNullOrEmpty(this string? text) => string.IsNullOrEmpty(text);
}
