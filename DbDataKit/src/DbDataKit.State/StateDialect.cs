using System.Data.Common;

namespace DbDataKit.State;

/// <summary>
/// Everything the state store needs to know about the engine underneath it — see phase 63.
/// <para>
/// **Separate from the replication dialects (<c>DbDataKit.Dialect</c>)**, which serve schemas that belong to somebody
/// else and map any column type. The state store's schema is fixed and internal — tables a product wrote — so what it
/// needs is a much smaller and much more opinionated surface: upsert syntax, identity DDL, row limiting and the four
/// type words the schema templates use.
/// </para>
/// </summary>
public abstract class StateDialect
{
    /// <summary>Looked up on <see cref="StateDialectRegistry.Default"/> — the three built-ins are
    /// always there; anything else must have been registered first (a compiled plugin, 109h) or this
    /// throws naming the built-ins and what a custom one requires.</summary>
    public static StateDialect For(string engine) => StateDialectRegistry.Default.Get(engine);

    public abstract string Engine { get; }

    public abstract DbConnection CreateConnection(string connectionString);

    /// <summary>
    /// How a parameter is written into statement text. Every store writes <c>$name</c> and this
    /// rewrites it, so the SQL in those files stays one readable string rather than an interpolation
    /// of dialect calls.
    /// </summary>
    public virtual string Parameter(string name) => $"@{name}";

    /// <summary>The name a parameter is *bound* under, which is not always how it is written — SQLite
    /// accepts the sigil, and the other two providers want it stripped or prefixed their own way.</summary>
    public abstract string ParameterName(string name);

    // ---- Structural differences ----

    /// <summary>
    /// Caps a result set. Written as a suffix so the caller's query reads in its natural order.
    /// <para>
    /// SQL Server has no <c>LIMIT</c>, and its <c>OFFSET/FETCH</c> requires an <c>ORDER BY</c> — every
    /// limited query in this project has one, which is not a coincidence: an unordered "first N" is a
    /// question without an answer.
    /// </para>
    /// </summary>
    public abstract string Limit(string parameterName);

    /// <summary>
    /// An insert that does nothing when it would violate <paramref name="conflictTarget"/>.
    /// <para>
    /// The idiom the queue, the lock table and the log's journal-replay guard all depend on for
    /// idempotency, so it has to mean exactly the same thing on all three engines: the row is either
    /// inserted or already there, never an error, and never a second row.
    /// </para>
    /// </summary>
    /// <param name="conflictTarget">
    /// The columns whose uniqueness is being relied on, or null for "whatever unique constraint this
    /// hits". Null is only correct where exactly one such constraint exists on the table.
    /// </param>
    /// <param name="conflictWhere">
    /// The predicate of a **partial** unique index, when that is what is being relied on. The work
    /// queue's uniqueness is exactly this shape — one in-flight row per mapping, where "in-flight" is
    /// a set of statuses — and getting it wrong is not a syntax error but a silently wrong answer:
    /// without the predicate, a mapping that has ever run could never be queued again.
    /// </param>
    public abstract string InsertOrIgnore(
        string table, string columns, string values, string? conflictTarget, string? conflictWhere = null);

    /// <summary>
    /// An insert that overwrites the conflicting row's <paramref name="updates"/> instead.
    /// </summary>
    /// <param name="updates">
    /// Assignments in <c>Column = EXCLUDED.Column</c> form, with the incoming row spelled
    /// <c>EXCLUDED</c> — rewritten per engine. SQL Server's MERGE calls it <c>source</c>.
    /// </param>
    public abstract string Upsert(
        string table, string columns, string values, string conflictTarget, string updates);

    // ---- Schema ----

    /// <summary>An auto-assigned integer primary key column.</summary>
    public abstract string IdentityKey(string column);

    /// <summary>
    /// Free text of any length — a log message, an error summary, a note.
    /// <para>
    /// Distinct from <see cref="KeyText"/> for one reason, and it is SQL Server's: an index cannot
    /// cover an <c>NVARCHAR(MAX)</c> column. Any column this schema indexes or keys has to be bounded
    /// there, so the schema has to say which columns those are. SQLite and Postgres do not care and
    /// get <c>TEXT</c> for both.
    /// </para>
    /// </summary>
    public abstract string Text { get; }

    /// <summary>Text that participates in a primary key, a foreign key or an index. Bounded on SQL
    /// Server — see <see cref="Text"/>.</summary>
    public abstract string KeyText { get; }

    public abstract string Integer { get; }

    // ---- The convention types (plan 29 section 3.3), one token each in a template ----

    /// <summary>A UUID: <c>UNIQUEIDENTIFIER</c>, <c>UUID</c>, and on SQLite lower-case text (<see cref="StateCommandExtensions.Bind"/> writes it that way). Ids are UUID v7.</summary>
    public abstract string Uuid { get; }

    /// <summary>A UTC instant: <c>DATETIMEOFFSET(7)</c>, <c>TIMESTAMPTZ</c>, and on SQLite fixed-width ISO 8601 text, which sorts as time does. Columns of this type are named <c>*_at</c>.</summary>
    public abstract string Time { get; }

    /// <summary>A JSON document: <c>NVARCHAR(MAX)</c>, <c>JSONB</c>, <c>TEXT</c>. Never part of a key. Written as <c>CAST($doc AS {{json}})</c>, which PostgreSQL needs to take text into a <c>jsonb</c> column.</summary>
    public abstract string Json { get; }

    public abstract string Bool { get; }

    /// <summary>Bytes: <c>VARBINARY(MAX)</c>, <c>BYTEA</c>, <c>BLOB</c>. Large values do not belong in an ordinary table at all (the payloads module, plan 29 section 3.6).</summary>
    public abstract string Blob { get; }

    /// <summary>
    /// How a column is added to an existing table. SQLite and Postgres write <c>ADD COLUMN</c>; SQL
    /// Server writes <c>ADD</c> and rejects the keyword. One word, and it is the whole of the
    /// difference between the three engines' <c>ALTER TABLE</c>.
    /// </summary>
    public virtual string AddColumn => "ADD COLUMN";

    /// <summary>
    /// How an index is dropped. SQLite and Postgres name it alone — an index name is unique within the
    /// schema there. SQL Server scopes index names to their table and insists on being told which.
    /// <para>
    /// Needed at all because SQLite cannot drop an indexed column, and phase 73 drops one: a migration
    /// that changes a column's nullability there has no <c>ALTER COLUMN</c> to reach for, only
    /// drop-and-re-add, and the index over it has to go first.
    /// </para>
    /// </summary>
    public virtual string DropIndex(string index, string table) => $"DROP INDEX {index}";

    /// <summary>
    /// The applied-migration count, and how it is recorded.
    /// <para>
    /// SQLite has <c>PRAGMA user_version</c>, a single integer that costs nothing to read on every
    /// connection. The other two need a table, which is the same idea written down.
    /// </para>
    /// </summary>
    public abstract int GetSchemaVersion(DbConnection connection);

    public abstract void SetSchemaVersion(DbConnection connection, int version);

    /// <summary>Runs once on every freshly opened connection, before anything else.</summary>
    public virtual void OnConnectionOpened(DbConnection connection) { }

    /// <summary>
    /// Whether a failed statement is worth trying again.
    /// <para>
    /// **False everywhere but SQLite**, and that is the finding rather than an omission. SQLite's
    /// retry loop exists because one writer holds a lock on a *file* and everybody else is told to go
    /// away; a client-server engine queues that contention internally and the caller simply waits. See
    /// <see cref="SqliteStateDialect"/> for the one place this returns true.
    /// </para>
    /// </summary>
    public virtual bool ShouldRetry(Exception exception) => false;

    // ---- What the module migrator needs (plan 29a Q02) ----

    /// <summary>The statement that creates a schema that does not exist yet; null on an engine with no schemas in a database (SQLite).</summary>
    public virtual string? CreateSchema(string schema) => null;

    /// <summary>Whether a table exists. <paramref name="qualifiedName"/> is the rendered name, schema included where there is one.</summary>
    public abstract bool TableExists(DbConnection connection, string qualifiedName);

    /// <summary>
    /// Takes the lock that keeps two services opening one database from migrating it at once, for as long as the result lives; null where the engine needs none (see
    /// <see cref="MigratesInOneTransaction"/>).
    /// </summary>
    public virtual IDisposable? AcquireMigrationLock(DbConnection connection) => null;

    /// <summary>
    /// True where the whole migration is one transaction that takes the write lock when it begins (SQLite), so a second process waits and then finds the work done. False where a
    /// lock is taken first and each migration is its own transaction.
    /// </summary>
    public virtual bool MigratesInOneTransaction => false;

    /// <summary>Begins the migration's transaction, taking the write lock at once where the engine has only one writer.</summary>
    public virtual DbTransaction BeginMigrationTransaction(DbConnection connection) => connection.BeginTransaction();

    /// <summary>
    /// What the database says if it was made by the old structure, one schema version for the whole database (plan 29 section 3.1's clean break): a description of the marker
    /// found, or null for a database that was never used that way.
    /// </summary>
    public abstract string? FindLegacySchemaVersion(DbConnection connection);
}
