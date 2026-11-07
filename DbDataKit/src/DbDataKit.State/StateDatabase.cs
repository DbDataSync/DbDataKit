using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace DbDataKit.State;

/// <summary>
/// A product's state database, shared by its server and every process it starts. The product says which tables it has
/// through a <see cref="StateSchema"/>; the shared fragments (<see cref="IdentitySchema"/>, <see cref="NotificationSchema"/>) are part of it.
/// <para>
/// SQLite by default and on any deployment that has not said otherwise — phase 63 added SQL Server and
/// PostgreSQL as alternatives for operators who would rather this lived on infrastructure they already
/// run. What engine is in use changes nothing above this class: the stores are written against one SQL
/// text and one parameter spelling, and <see cref="StateDialect"/> renders the differences.
/// </para>
/// </summary>
public partial class StateDatabase
{
    /// <summary>
    /// Matches the <c>$name</c> placeholders every store writes.
    /// <para>
    /// The stores keep writing SQLite's spelling and this rewrites it, rather than each query being
    /// assembled from dialect calls. That is deliberate: a query written as one readable string is a
    /// query somebody can check against what the database will run, and phase 63 was a port of ~2,400
    /// lines that had to be provably unchanged in meaning. Interpolating a dialect call per parameter
    /// would have made every one of those lines a new thing to verify.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"\$([A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex ParameterPlaceholder { get; }

    private const int MaxRetryAttempts = 5;

    private readonly string _connectionString;

    public StateDialect Dialect { get; }

    private readonly StateSchema? _schema;

    private readonly string _appliedBy;

    /// <summary>False for a database opened by <see cref="OpenForImport"/>: nothing is created or migrated until the snapshot importer (or <see cref="ApplyMigrations"/>) does it.</summary>
    private bool _migrate = true;

    /// <summary>
    /// Where the database's modules are (plan 29): null for a database built from a <see cref="StateSchema"/> (the structure being replaced, kept until every product has
    /// moved), set for one built from modules.
    /// </summary>
    public StateLayout? Layout { get; }

    /// <summary>The clock for every time the state layer writes itself (the registry's <c>applied_at</c>, retention, ids); tests give it a fake one.</summary>
    public TimeProvider Time { get; }

    /// <summary>A SQLite file built from <paramref name="modules"/>, for the process described by <paramref name="scope"/> (<see cref="StateScope.Standalone"/> when omitted).</summary>
    public static StateDatabase OpenModules(string sqliteFilePath, IEnumerable<StateModule> modules, StateScope? scope = null, TimeProvider? time = null) =>
        new(sqliteFilePath, StateLayout.Create(StateEngineIds.Sqlite, modules, scope ?? StateScope.Standalone), null, time);

    /// <summary>A SQLite file built from modules.</summary>
    public StateDatabase(string sqliteFilePath, StateLayout layout, string? appliedBy = null, TimeProvider? time = null)
        : this(StateEngineIds.Sqlite, SqliteConnectionString(sqliteFilePath), layout, appliedBy, time)
    {
    }

    /// <summary>
    /// A database built from modules (plan 29): opening it applies each module's missing migrations and records them in the registry, or refuses with a message naming the
    /// database (an old one, a module newer than this service, an edited migration).
    /// </summary>
    /// <param name="layout">From <see cref="StateLayout.Create"/>, for the same <paramref name="engine"/>.</param>
    /// <param name="appliedBy">Who is recorded as applying a migration: the service and its version. Defaults to the entry assembly.</param>
    public StateDatabase(string engine, string connectionString, StateLayout layout, string? appliedBy = null, TimeProvider? time = null)
        : this(engine, connectionString, layout, appliedBy, time, migrate: true)
    {
    }

    private StateDatabase(string engine, string connectionString, StateLayout layout, string? appliedBy, TimeProvider? time, bool migrate)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (layout.Engine != engine)
            throw new ArgumentException($"The layout was made for the engine {layout.Engine}, and the database is {engine}.", nameof(layout));
        Dialect = StateDialect.For(engine);
        _connectionString = connectionString;
        Layout = layout;
        Time = time ?? TimeProvider.System;
        _appliedBy = appliedBy ?? DefaultAppliedBy();

        _migrate = migrate;
        if (!migrate) return;
        using var connection = OpenRawConnection();
        EnsureSchema(connection);
    }

    /// <summary>
    /// A database built from modules that is opened <b>without</b> creating or migrating anything: the target of a snapshot import (<c>StateSnapshotImporter</c>), which creates each module at the
    /// snapshot's version itself. Everything else opens a database with the constructors, which migrate. Until <see cref="ApplyMigrations"/> runs (the importer does it when asked to migrate after the
    /// import) the stores of this instance will not find their tables.
    /// </summary>
    public static StateDatabase OpenForImport(string engine, string connectionString, StateLayout layout, string? appliedBy = null, TimeProvider? time = null) =>
        new(engine, connectionString, layout, appliedBy, time, migrate: false);

    /// <summary>A SQLite file opened for a snapshot import; see <see cref="OpenForImport"/>.</summary>
    public static StateDatabase OpenSqliteForImport(string sqliteFilePath, StateLayout layout, string? appliedBy = null, TimeProvider? time = null) =>
        new(StateEngineIds.Sqlite, SqliteConnectionString(sqliteFilePath), layout, appliedBy, time, migrate: false);

    /// <summary>Applies every missing migration of the layout (what opening a database does) to a database opened for import; from then on it behaves like any other.</summary>
    public void ApplyMigrations()
    {
        if (Layout is null) throw new InvalidOperationException("Only a database built from modules has migrations to apply.");
        using var connection = OpenRawConnection();
        StateMigrator.Apply(connection, this, Layout, _appliedBy);
        _migrate = true;
    }

    internal string AppliedBy => _appliedBy;

    private static string DefaultAppliedBy()
    {
        var name = System.Reflection.Assembly.GetEntryAssembly()?.GetName();
        return name?.Name is { } n ? $"{n} {name.Version}" : "unknown";
    }

    /// <summary>A SQLite file.</summary>
    public StateDatabase(string sqliteFilePath, StateSchema schema)
        : this(StateEngineIds.Sqlite, SqliteConnectionString(sqliteFilePath), schema)
    {
    }

    public StateDatabase(string engine, string connectionString, StateSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        Dialect = StateDialect.For(engine);
        _connectionString = connectionString;
        _schema = schema;
        Time = TimeProvider.System;
        _appliedBy = "";

        using var connection = OpenRawConnection();
        EnsureSchema(connection);
    }

    /// <summary>
    /// Pooling=False: Microsoft.Data.Sqlite pools native sqlite3 handles by connection string by
    /// default. Combined with this file being written by *other processes* (DbDataSync.TaskRunner), a
    /// pooled handle reused across separate OpenConnection() calls risks observing a stale snapshot
    /// from whenever it was first opened instead of picking up commits made by other processes since —
    /// each caller here already treats a connection as fully short-lived (open, one query, dispose),
    /// so there is no pooling benefit worth that risk.
    /// <para>
    /// Not applied to the other two engines, where a connection reaches a server rather than a file
    /// and pooling is how they are meant to be used.
    /// </para>
    /// </summary>
    protected static string SqliteConnectionString(string sqliteFilePath)
    {
        var dir = Path.GetDirectoryName(sqliteFilePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        return new SqliteConnectionStringBuilder { DataSource = sqliteFilePath, Pooling = false }.ToString();
    }

    /// <summary>
    /// Opens a connection and re-verifies the schema exists on *every* call, not just once at
    /// construction — cheap (a single version read in the common case) and makes this self-healing
    /// against a real, still not fully root-caused problem observed in this project's sandboxed dev
    /// environment: a long-lived process (DbDataSync.Api) that spawns child processes
    /// (DbDataSync.TaskRunner) writing to this same file could, after the child had been running for
    /// tens of seconds, start seeing an apparently-schema-less view of the database on a fresh
    /// connection ("no such table"), despite migration having already run successfully at startup and
    /// other processes' writes to the same path being independently verifiable on disk. Disabling
    /// pooling and WAL mode did not fully resolve it either. Given the underlying cause sits below
    /// this application (most likely something about how this sandbox's process/filesystem isolation
    /// interacts with a Node-launched process tree spawning further dotnet child processes — see
    /// architecture/implementation/done/phase-006-spa.md), re-checking and re-applying the schema on
    /// every open is a pragmatic, low-cost way to make correctness not depend on fully understanding
    /// that cause.
    /// </summary>
    public DbConnection OpenConnection()
    {
        var connection = OpenRawConnection();
        // A server engine is verified once, when the database is opened (a pooled connection to a server is not subject to the file problem described above);
        // the older structure keeps re-checking on every open on every engine.
        if (_migrate && (Layout is null || Dialect.Engine == StateEngineIds.Sqlite))
            EnsureSchema(connection);
        return connection;
    }

    private DbConnection OpenRawConnection()
    {
        var connection = Dialect.CreateConnection(_connectionString);
        connection.Open();
        Dialect.OnConnectionOpened(connection);
        if (Layout is not null) Dialect.OnConnectionOpened(connection, Layout);
        return connection;
    }

    /// <summary>
    /// A command whose <c>$name</c> placeholders have been rewritten for this engine — what every
    /// store creates instead of calling <c>connection.CreateCommand()</c> directly.
    /// </summary>
    public DbCommand Command(DbConnection connection, string sql) => Command(connection, sql, module: null);

    /// <summary>
    /// The same for a store of one module, whose statements may say <c>{table:name}</c> for the module's own tables as well as <c>{table:module.name}</c>; a database built from
    /// modules renders those, and the convention type tokens (<c>CAST($doc AS {{json}})</c>), before the placeholders are rewritten.
    /// </summary>
    public DbCommand Command(string module, DbConnection connection, string sql) => Command(connection, sql, module);

    private DbCommand Command(DbConnection connection, string sql, string? module)
    {
        if (Layout is not null)
            sql = StateTemplates.Render(Layout.Render(sql, module), Dialect);
        var command = connection.CreateCommand();
        command.CommandText = Dialect.Engine == StateEngineIds.Sqlite
            ? sql
            : ParameterPlaceholder.Replace(sql, match => Dialect.Parameter(match.Groups[1].Value));
        return command;
    }

    /// <summary>The same, enlisted in a transaction — the form every multi-statement write uses.</summary>
    public DbCommand Command(DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = Command(connection, sql);
        command.Transaction = transaction;
        return command;
    }

    public DbCommand Command(string module, DbConnection connection, DbTransaction? transaction, string sql)
    {
        var command = Command(module, connection, sql);
        command.Transaction = transaction;
        return command;
    }

    /// <summary>A new id (plan 29 section 3.3): a UUID v7, time-ordered, taken from <see cref="Time"/> so a test's clock orders them.</summary>
    public Guid NewId() => Guid.CreateVersion7(Time.GetUtcNow());

    /// <summary>
    /// An update guarded by the row's <c>row_version</c> (plan 29 section 3.3), for a table several instances write: sets <paramref name="set"/>, adds one to the version, and applies
    /// only if the row is still at <paramref name="expectedVersion"/>. False says another writer got there first; the caller reads again and retries or gives up.
    /// </summary>
    /// <param name="module">The module whose <c>{table:name}</c> the statement may use unqualified.</param>
    /// <param name="table">The table's name within the module.</param>
    /// <param name="set">Assignments without <c>row_version</c>: <c>value_text = $value, updated_at = $now</c>.</param>
    /// <param name="where">The row's key condition: <c>key = $key</c>.</param>
    /// <param name="bind">Binds the parameters <paramref name="set"/> and <paramref name="where"/> use.</param>
    public bool UpdateVersioned(string module, DbConnection connection, DbTransaction? transaction, string table, string set, string where, long expectedVersion, Action<DbCommand> bind)
    {
        using var command = Command(module, connection, transaction, $"UPDATE {{table:{table}}} SET {set}, row_version = row_version + 1 WHERE {where} AND row_version = $expected_row_version;");
        bind(command);
        command.Bind(this, "expected_row_version", expectedVersion);
        return command.ExecuteNonQuery() == 1;
    }

    /// <summary>
    /// Begins a transaction for the stores of <paramref name="module"/>. On SQLite, Microsoft.Data.Sqlite begins an immediate transaction by default, which takes the write lock on every attached file, the
    /// main one included, and so cancels what putting a module in a file of its own is for. For a module in a file of its own this begins a deferred transaction, which locks only the files it writes to. Anywhere
    /// else it is <see cref="DbConnection.BeginTransaction()"/>. A store of a module that may be placed in a file (the runner's progress, run logs) begins its transactions here.
    /// </summary>
    public DbTransaction BeginTransaction(DbConnection connection, string? module = null) =>
        module is not null && Dialect.Engine == StateEngineIds.Sqlite && Layout?.Namespace(module).File is not null && connection is SqliteConnection sqlite
            ? sqlite.BeginTransaction(deferred: true)
            : connection.BeginTransaction();

    /// <summary>Caps a result set — <c>LIMIT</c>, or SQL Server's <c>OFFSET/FETCH</c>. Interpolated
    /// into a query's text, which is why it takes a parameter *name* rather than a value.</summary>
    public string Limit(string parameterName) => Dialect.Limit(parameterName);

    /// <summary>
    /// Runs a unit of work, retrying it if the engine says it is worth retrying.
    /// <para>
    /// **Only SQLite ever says so.** Its retry loop exists because a writer holds a lock on the whole
    /// file and every other process is refused rather than queued. A client-server engine queues that
    /// contention internally, so there is nothing here for an application-level loop to improve — the
    /// plan doc suspected as much and this is the confirmation, not an omission.
    /// </para>
    /// </summary>
    public T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception ex) when (Dialect.ShouldRetry(ex) && attempt < MaxRetryAttempts)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(50 * attempt));
            }
        }
    }

    public void Retry(Action action) => Retry<object?>(() =>
    {
        action();
        return null;
    });

    private void EnsureSchema(DbConnection connection)
    {
        if (Layout is not null)
        {
            StateMigrator.Apply(connection, this, Layout, _appliedBy);
            return;
        }

        var scripts = _schema!.ScriptsFor(Dialect);
        var currentVersion = Dialect.GetSchemaVersion(connection);

        for (var i = currentVersion; i < scripts.Count; i++)
        {
            using (var transaction = connection.BeginTransaction())
            {
                foreach (var statement in scripts[i])
                {
                    using var cmd = connection.CreateCommand();
                    cmd.Transaction = transaction;
                    cmd.CommandText = statement;
                    cmd.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            Dialect.SetSchemaVersion(connection, i + 1);
        }
    }
}

/// <summary>Parameter binding, in the one shape every store uses.</summary>
public static class StateCommandExtensions
{
    /// <summary>
    /// Binds a value, taking the parameter's bare name — the <c>$</c>, <c>@</c> or nothing is the
    /// engine's business, not the caller's.
    /// </summary>
    /// <param name="type">
    /// Stated only where it has to be: a parameter that is null *and* appears solely in an
    /// <c>IS NULL</c> test gives Postgres nothing to infer from, and it refuses to plan the statement
    /// rather than guessing.
    /// </param>
    public static DbCommand Bind(
        this DbCommand command, StateDatabase database, string name, object? value,
        System.Data.DbType? type = null)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = database.Dialect.ParameterName(name);
        // the convention types (plan 29 section 3.3) are written in their one shape only on a database built from modules; the older structure keeps handing the provider what it was given
        parameter.Value = (database.Layout is null ? value : StateValues.ForParameter(database.Dialect, value)) ?? DBNull.Value;
        if (type is not null)
            parameter.DbType = type.Value;
        command.Parameters.Add(parameter);
        return command;
    }
}
