using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace DbDataKit.State;

/// <summary>One row of the registry (<c>dbdatakit.modules</c>): a module, in one scope, and where and at which version it was made.</summary>
public sealed record RegisteredModule(string Module, string Scope, string SchemaName, string Prefix, int Version, string AppliedBy, string AppliedAt);

/// <summary>What the registry says about one namespace of the layout, next to what this binary knows.</summary>
/// <param name="Registered">The registry's row for it; null when the module was never applied in this scope.</param>
/// <param name="Problem">Why this service would refuse to open the database, in one sentence; null when it would not (a pending migration is not a problem: opening applies it).</param>
public sealed record ModuleInspection(StateNamespace Namespace, RegisteredModule? Registered, string? Problem)
{
    /// <summary>The version this binary has.</summary>
    public int BinaryVersion => Namespace.Module.Version;

    /// <summary>How many migrations opening the database would apply to this module.</summary>
    public int Pending => Registered is null ? BinaryVersion : Math.Max(0, BinaryVersion - Registered.Version);
}

/// <summary>What <see cref="StateInspector.Inspect"/> found. Nothing was created or changed to find it.</summary>
/// <param name="DatabaseExists">False only for a SQLite file that is not there (nothing was created); a server database is there if it could be opened.</param>
/// <param name="RegistryExists">Whether the registry table is there (as the settings place it).</param>
/// <param name="Registry">Every row of the registry, including modules of other services that share the database and that this layout does not use.</param>
/// <param name="Legacy">When the database was made by the structure that kept one schema version for the whole database: what marks it.</param>
/// <param name="Clashes">Tables the layout would create that already exist while no registry row says the module made them.</param>
public sealed record StateInspection(
    bool DatabaseExists, bool RegistryExists, IReadOnlyList<RegisteredModule> Registry, IReadOnlyList<ModuleInspection> Modules, string? Legacy, IReadOnlyList<string> Clashes)
{
    /// <summary>The modules with something to apply.</summary>
    public IEnumerable<ModuleInspection> PendingModules => Modules.Where(m => m.Pending > 0);

    /// <summary>True when opening the database would create or change something.</summary>
    public bool NeedsMigration => !RegistryExists || Modules.Any(m => m.Pending > 0);
}

/// <summary>
/// Looks at a state database without making or changing anything (plan 29b, C01). <see cref="StateDatabase"/>'s constructors create the schemas, the registry and the tables a layout needs; this opens a
/// connection of its own and only reads: the registry, what version of each module this binary has against what the registry holds, the tables the layout names, the engine's version and, on a server,
/// who the login is. A SQLite file that is not there is reported as missing (it is not created, nor is its folder), and it is opened read-only.
/// </summary>
public sealed class StateInspector : IDisposable
{
    private readonly StateLayout _layout;
    private readonly StateDialect _dialect;

    private StateInspector(StateLayout layout, DbConnection? connection)
    {
        _layout = layout;
        _dialect = StateDialect.For(layout.Engine);
        Connection = connection;
    }

    /// <summary>The open connection; null for a SQLite file that does not exist.</summary>
    public DbConnection? Connection { get; }

    public bool DatabaseExists => Connection is not null;

    public string Engine => _layout.Engine;

    /// <summary>Opens the database of <paramref name="layout"/>'s engine: <paramref name="target"/> is the file for SQLite and the (resolved) connection string for the others. A connection that cannot be made throws the provider's exception.</summary>
    public static StateInspector Open(StateLayout layout, string target)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var dialect = StateDialect.For(layout.Engine);
        if (layout.Engine == StateEngineIds.Sqlite)
        {
            if (!File.Exists(target)) return new StateInspector(layout, null);
            var connection = dialect.CreateConnection(new SqliteConnectionStringBuilder { DataSource = target, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            try
            {
                connection.Open();
                dialect.OnConnectionOpened(connection);
                AttachExistingFiles(connection, layout, target);
            }
            catch { connection.Dispose(); throw; }
            return new StateInspector(layout, connection);
        }

        var server = dialect.CreateConnection(target);
        try { server.Open(); }
        catch { server.Dispose(); throw; }
        return new StateInspector(layout, server);
    }

    /// <summary>Like <see cref="StateDialect.OnConnectionOpened(DbConnection, StateLayout)"/>, but a file that is not there is left alone (attaching would create it).</summary>
    private static void AttachExistingFiles(DbConnection connection, StateLayout layout, string mainFile)
    {
        var main = Path.GetDirectoryName(Path.GetFullPath(mainFile))!;
        foreach (var ns in layout.Namespaces.Where(n => n.File is not null).DistinctBy(n => n.Schema))
        {
            var path = Path.IsPathRooted(ns.File!) ? ns.File! : Path.Combine(main, ns.File!);
            if (!File.Exists(path)) continue;
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"ATTACH DATABASE $path AS {ns.Schema};";
            var parameter = cmd.CreateParameter();
            parameter.ParameterName = "$path";
            parameter.Value = path;
            cmd.Parameters.Add(parameter);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>The engine's own version text: <c>17.2</c> for PostgreSQL, <c>17.0.1000.7</c> for SQL Server, <c>3.45.1</c> for SQLite. Null when there is no database to ask.</summary>
    public string? ServerVersion()
    {
        if (Connection is null) return null;
        switch (Engine)
        {
            case StateEngineIds.Postgres:
                var number = Convert.ToInt32(Scalar("SHOW server_version_num;"), CultureInfo.InvariantCulture);
                return $"{number / 10000}.{number % 10000}";
            case StateEngineIds.MsSql:
                return Convert.ToString(Scalar("SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(64));"), CultureInfo.InvariantCulture);
            default:
                return Convert.ToString(Scalar("SELECT sqlite_version();"), CultureInfo.InvariantCulture);
        }
    }

    /// <summary>The features the connected engine has (<see cref="EngineFeatures"/>), asked through the layout's probe; a SQLite file that is not there yet has none, whatever the probe.</summary>
    public EngineFeatureReport Features() => Connection is null ? EngineFeatures.Judge(Engine, null) : _layout.FeatureProbe.Detect(Connection, Engine);

    /// <summary>The database role (PostgreSQL) or user (SQL Server) the connection is; null on SQLite.</summary>
    public string? CurrentLogin() => Connection is null ? null : Engine switch
    {
        StateEngineIds.Postgres => Convert.ToString(Scalar("SELECT current_user;"), CultureInfo.InvariantCulture),
        StateEngineIds.MsSql => Convert.ToString(Scalar("SELECT USER_NAME();"), CultureInfo.InvariantCulture),
        _ => null,
    };

    /// <summary>
    /// The registry's rows, or null when the registry is not there. A login that may not read it makes the provider's exception: permission denied is a different answer from "not there". On SQL Server a login with
    /// no right at all on the registry cannot tell the two apart (the engine hides what it may not see), and gets "not there".
    /// </summary>
    public IReadOnlyList<RegisteredModule>? ReadRegistry()
    {
        if (Connection is null) return null;
        using var cmd = Connection.CreateCommand();
        cmd.CommandText = $"SELECT module, scope, schema_name, prefix, version, applied_by, applied_at FROM {_layout.Registry};";
        try
        {
            using var reader = cmd.ExecuteReader();
            var rows = new List<RegisteredModule>();
            while (reader.Read())
                rows.Add(new RegisteredModule(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture), reader.GetString(5), Text(reader.GetValue(6))));
            return rows;
        }
        catch (DbException ex) when (IsMissingObject(ex)) { return null; }
    }

    private static string Text(object value) => value switch
    {
        DateTimeOffset at => at.UtcDateTime.ToString("u", CultureInfo.InvariantCulture),
        DateTime at => DateTime.SpecifyKind(at, DateTimeKind.Utc).ToString("u", CultureInfo.InvariantCulture),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary>PostgreSQL 42P01 (undefined table), 3F000 (no such schema); SQL Server 208 (invalid object name); SQLite "no such table".</summary>
    private static bool IsMissingObject(DbException ex) => ex switch
    {
        SqliteException sqlite => sqlite.SqliteErrorCode == 1 && sqlite.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase),
        _ when ex.SqlState is "42P01" or "3F000" => true,
        _ => ex.GetType().Name == "SqlException" && Convert.ToInt32(ex.GetType().GetProperty("Number")?.GetValue(ex), CultureInfo.InvariantCulture) == 208,
    };

    /// <summary>
    /// Compares what the registry holds with what this binary has for every namespace of the layout, and finds the problems that would stop this service opening the database (a module newer than the
    /// binary, a namespace that moved, an edited migration, a database of the old structure) and the tables of the layout that something else already owns.
    /// </summary>
    public StateInspection Inspect()
    {
        if (Connection is null) return new StateInspection(false, false, [], [.. _layout.Namespaces.Select(n => new ModuleInspection(n, null, null))], null, []);

        var rows = ReadRegistry();
        var registryExists = rows is not null;
        string? legacy = registryExists ? null : SafeLegacy();
        var modules = new List<ModuleInspection>();
        var clashes = new List<string>();
        foreach (var ns in _layout.Namespaces)
        {
            var scope = ns.ScopeName ?? "install";
            var row = rows?.FirstOrDefault(r => r.Module == ns.Module.Name && r.Scope == scope);
            string? problem = null;
            if (row is not null)
            {
                if (row.Version > ns.Module.Version)
                    problem = $"the database has '{ns.Key}' at version {row.Version} (written by {row.AppliedBy}) and this binary only knows version {ns.Module.Version}; upgrade this service, a module is never taken back to an older version";
                else if (!string.Equals(row.SchemaName, ns.Placement, StringComparison.OrdinalIgnoreCase) || !string.Equals(row.Prefix, ns.Prefix, StringComparison.Ordinal))
                    problem = $"'{ns.Key}' was created at '{row.SchemaName}' with prefix '{row.Prefix}' and the settings now say '{ns.Placement}' with prefix '{ns.Prefix}'; put the settings back (state.namespaces.{ns.Module.Name}), or move the tables yourself and update the registry";
                else if (!RegistryChecksumMatches(ns, scope, StateMigrator.Checksum(ns.Module, row.Version)))
                    problem = $"a migration of '{ns.Key}' that shipped was edited after it was applied (the checksum in the registry differs); never change a migration that has shipped, append a new one";
            }
            else
            {
                foreach (var table in ns.Tables.Values)
                    if (TableExists(table)) clashes.Add(table);
            }

            modules.Add(new ModuleInspection(ns, row, problem));
        }

        return new StateInspection(true, registryExists, rows ?? [], modules, legacy, clashes);
    }

    private bool RegistryChecksumMatches(StateNamespace ns, string scope, string expected)
    {
        using var cmd = Connection!.CreateCommand();
        cmd.CommandText = $"SELECT checksum FROM {_layout.Registry} WHERE module = {Param("m")} AND scope = {Param("s")};";
        AddParameter(cmd, "m", ns.Module.Name);
        AddParameter(cmd, "s", scope);
        return string.Equals(Convert.ToString(cmd.ExecuteScalar(), CultureInfo.InvariantCulture), expected, StringComparison.Ordinal);
    }

    private string Param(string name) => _dialect.Parameter(name);

    private void AddParameter(DbCommand cmd, string name, object value)
    {
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = _dialect.ParameterName(name);
        parameter.Value = value;
        cmd.Parameters.Add(parameter);
    }

    private string? SafeLegacy()
    {
        try { return _dialect.FindLegacySchemaVersion(Connection!); }
        catch (DbException) { return null; }
    }

    /// <summary>Whether a table of this name is there, looked up in the engine's catalogue (which needs no right on the table or its schema).</summary>
    public bool TableExists(string qualifiedName)
    {
        if (Connection is null) return false;
        var (schema, table) = StateRights.Split(qualifiedName);
        using var cmd = Connection.CreateCommand();
        switch (Engine)
        {
            case StateEngineIds.Postgres:
                cmd.CommandText = $"SELECT 1 FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE c.relkind IN ('r', 'p') AND c.relname = {Param("t")} AND n.nspname = COALESCE({Param("s")}, current_schema());";
                AddParameter(cmd, "t", table);
                AddParameter(cmd, "s", (object?)schema ?? DBNull.Value);
                return cmd.ExecuteScalar() is not null;
            default:
                return _dialect.TableExists(Connection, qualifiedName);
        }
    }

    private object? Scalar(string sql)
    {
        using var cmd = Connection!.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    public void Dispose() => Connection?.Dispose();
}
