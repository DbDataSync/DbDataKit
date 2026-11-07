using System.Data.Common;
using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.State;

/// <summary>
/// Applies a layout's modules to a database and records each one's version in the registry <c>dbdatakit_modules</c> (plan 29 sections 3.1 and 3.2). A module's missing
/// migrations are applied in layout order (a module after its dependencies), each migration in one transaction with the registry row that says it was applied, under a lock that
/// keeps two services starting on one database from both doing it.
/// <para>
/// It refuses, with a message naming the database, the module and what differs: a database made by the old structure (one schema version for the whole database; never converted),
/// a module whose version is newer than this service knows, a module whose shipped migrations were edited since they were applied (the registry keeps their checksum), and a
/// namespace whose schema or prefix is not what it was created with.
/// </para>
/// </summary>
internal static class StateMigrator
{
    public static void Apply(DbConnection connection, StateDatabase database, StateLayout layout, string appliedBy)
    {
        var dialect = database.Dialect;

        if (InSync(connection, database, layout)) return;

        using var lockHandle = dialect.AcquireMigrationLock(connection);
        using var whole = dialect.MigratesInOneTransaction ? dialect.BeginMigrationTransaction(connection) : null;

        var registryExists = dialect.TableExists(connection, layout.Registry);
        if (!registryExists)
        {
            if (dialect.FindLegacySchemaVersion(connection) is { } marker)
                throw new InvalidOperationException(
                    $"The state database {Describe(connection)} was made by the structure that keeps one schema version for the whole database ({marker}). " +
                    "The suite's state databases are now built from modules, each versioned on its own, and an old database is not converted: " +
                    "point the service at a new, empty database (or a new file) and set it up again.");

        }

        // Whenever something is applied, not only the first time: a second group's namespace is a new schema in a database that already has its registry.
        // After the legacy check, so a database that is refused is left as it was found.
        foreach (var schema in layout.Schemas)
            Execute(connection, whole, dialect.CreateSchema(schema));
        if (!registryExists)
            Execute(connection, whole, StateTemplates.Render(RegistryTemplate(layout), dialect));

        var rows = registryExists ? Read(connection, whole, layout) : [];
        foreach (var ns in layout.Namespaces)
        {
            var module = ns.Module;
            var scope = ScopeOf(ns);
            rows.TryGetValue((module.Name, scope), out var row);

            if (row is not null)
            {
                if (row.Version > module.Version)
                    throw new InvalidOperationException(
                        $"The state database {Describe(connection)} has the module '{ns.Key}' at version {row.Version}, written by {row.AppliedBy}, but this service only knows version {module.Version}. " +
                        "Upgrade this service; a module is never taken back to an older version.");

                if (!string.Equals(row.SchemaName, ns.Placement, StringComparison.OrdinalIgnoreCase) || !string.Equals(row.Prefix, ns.Prefix, StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        $"The module '{ns.Key}' in the state database {Describe(connection)} was created at '{row.SchemaName}' with prefix '{row.Prefix}', and the settings now say '{ns.Placement}' and prefix '{ns.Prefix}'. " +
                        $"Put the settings back (state.namespaces.{module.Name}), or move the tables yourself and update the registry.");

                if (Checksum(module, row.Version) != row.Checksum)
                    throw new InvalidOperationException(
                        $"The first {row.Version} migrations of the module '{ns.Key}' are not the ones the state database {Describe(connection)} applied: a migration that shipped was edited. " +
                        "Never change a migration that has shipped; append a new one.");
            }

            for (var i = row?.Version ?? 0; i < module.Version; i++)
            {
                var statements = StateTemplates.SplitStatements(StateTemplates.Render(layout.Render(module.Migrations[i], module.Name), dialect));
                var version = i + 1;
                InTransaction(connection, dialect, whole, transaction =>
                {
                    foreach (var statement in statements) Execute(connection, transaction, statement);
                    Record(connection, database, transaction, layout, ns, scope, version, Checksum(module, version), appliedBy);
                });
            }

            if (row is null && module.Version == 0)
                InTransaction(connection, dialect, whole, transaction => Record(connection, database, transaction, layout, ns, scope, 0, Checksum(module, 0), appliedBy));
        }

        whole?.Commit();
    }

    /// <summary>
    /// For the snapshot importer: creates the schemas of the layout and the registry (when missing), under no transaction of the caller's. The same statements <see cref="Apply"/> runs first.
    /// </summary>
    internal static void PrepareForImport(DbConnection connection, StateDatabase database, StateLayout layout)
    {
        var dialect = database.Dialect;
        var registryExists = dialect.TableExists(connection, layout.Registry);   // before the transaction: SQL Server refuses a command outside one that is pending
        using var transaction = dialect.BeginMigrationTransaction(connection);
        foreach (var schema in layout.Schemas)
            Execute(connection, transaction, dialect.CreateSchema(schema));
        if (!registryExists)
            Execute(connection, transaction, StateTemplates.Render(RegistryTemplate(layout), dialect));
        transaction.Commit();
    }

    /// <summary>
    /// For the snapshot importer: applies the first <paramref name="version"/> migrations of one module (not all of them) inside the caller's transaction and records that version and its checksum in
    /// the registry. A module at its own version is what <see cref="Apply"/> produces; an older one is brought up by <see cref="Apply"/> afterwards.
    /// </summary>
    internal static void ApplyModuleTo(DbConnection connection, StateDatabase database, StateLayout layout, StateNamespace ns, int version, DbTransaction transaction, string appliedBy)
    {
        var module = ns.Module;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(version, module.Version);
        var dialect = database.Dialect;
        for (var i = 0; i < version; i++)
        {
            foreach (var statement in StateTemplates.SplitStatements(StateTemplates.Render(layout.Render(module.Migrations[i], module.Name), dialect)))
                Execute(connection, transaction, statement);
        }

        Record(connection, database, transaction, layout, ns, ScopeOf(ns), version, Checksum(module, version), appliedBy);
    }

    /// <summary>The module names (with their scope) recorded in the registry, or none when there is no registry.</summary>
    internal static IReadOnlyList<(string Module, string Scope, int Version)> RegisteredModules(DbConnection connection, StateDatabase database, StateLayout layout)
    {
        if (!database.Dialect.TableExists(connection, layout.Registry)) return [];
        return [.. Read(connection, null, layout).Select(r => (r.Key.Module, r.Key.Scope, r.Value.Version))];
    }

    /// <summary>True when the registry exists and every module of the layout is recorded at its current version, with the names it was made with: nothing to apply.</summary>
    private static bool InSync(DbConnection connection, StateDatabase database, StateLayout layout)
    {
        if (!database.Dialect.TableExists(connection, layout.Registry)) return false;
        var rows = Read(connection, null, layout);
        return layout.Namespaces.All(ns => rows.TryGetValue((ns.Module.Name, ScopeOf(ns)), out var row)
            && row.Version == ns.Module.Version
            && row.Checksum == Checksum(ns.Module, row.Version)
            && string.Equals(row.SchemaName, ns.Placement, StringComparison.OrdinalIgnoreCase)
            && row.Prefix == ns.Prefix);
    }

    private static string RegistryTemplate(StateLayout layout) => $$$"""
        CREATE TABLE {{{layout.Registry}}} (
            module       {{key}} NOT NULL,
            scope        {{key}} NOT NULL,
            schema_name  {{key}} NOT NULL,
            prefix       {{key}} NOT NULL,
            version      {{int}} NOT NULL,
            checksum     {{key}} NOT NULL,
            applied_at   {{time}} NOT NULL,
            applied_by   {{key}} NOT NULL,
            PRIMARY KEY (module, scope)
        );
        """;

    private sealed record Row(string SchemaName, string Prefix, int Version, string Checksum, string AppliedBy);

    private static Dictionary<(string Module, string Scope), Row> Read(DbConnection connection, DbTransaction? transaction, StateLayout layout)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = $"SELECT module, scope, schema_name, prefix, version, checksum, applied_by FROM {layout.Registry};";
        using var reader = cmd.ExecuteReader();
        var rows = new Dictionary<(string, string), Row>();
        while (reader.Read())
            rows[(reader.GetString(0), reader.GetString(1))] = new Row(reader.GetString(2), reader.GetString(3), reader.Int32(4), reader.GetString(5), reader.GetString(6));
        return rows;
    }

    private static void Record(DbConnection connection, StateDatabase database, DbTransaction transaction, StateLayout layout, StateNamespace ns, string scope, int version, string checksum, string appliedBy)
    {
        var sql = database.Dialect.Upsert(
            layout.Registry,
            "module, scope, schema_name, prefix, version, checksum, applied_at, applied_by",
            "$module, $scope, $schema_name, $prefix, $version, $checksum, $applied_at, $applied_by",
            "module, scope",
            "schema_name = EXCLUDED.schema_name, prefix = EXCLUDED.prefix, version = EXCLUDED.version, checksum = EXCLUDED.checksum, applied_at = EXCLUDED.applied_at, applied_by = EXCLUDED.applied_by");
        using var cmd = database.Command(connection, transaction, sql);
        cmd.Bind(database, "module", ns.Module.Name)
            .Bind(database, "scope", scope)
            .Bind(database, "schema_name", ns.Placement)
            .Bind(database, "prefix", ns.Prefix)
            .Bind(database, "version", (long)version)
            .Bind(database, "checksum", checksum)
            .Bind(database, "applied_at", database.Time.GetUtcNow())
            .Bind(database, "applied_by", appliedBy);
        cmd.ExecuteNonQuery();
    }

    private static string ScopeOf(StateNamespace ns) => ns.ScopeName ?? "install";

    /// <summary>The hash of the migrations 0 to <paramref name="version"/> as written (before any name is rendered), so the same module hashes the same in every namespace and on every engine.</summary>
    internal static string Checksum(StateModule module, int version)
    {
        using var sha = SHA256.Create();
        foreach (var migration in module.Migrations.Take(version))
        {
            sha.TransformBlock(Encoding.UTF8.GetBytes(migration.ReplaceLineEndings("\n")), 0, Encoding.UTF8.GetByteCount(migration.ReplaceLineEndings("\n")), null, 0);
            sha.TransformBlock([0], 0, 1, null, 0);
        }
        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexStringLower(sha.Hash!);
    }

    private static void InTransaction(DbConnection connection, StateDialect dialect, DbTransaction? whole, Action<DbTransaction> work)
    {
        if (whole is not null)
        {
            work(whole);
            return;
        }

        using var transaction = dialect.BeginMigrationTransaction(connection);
        work(transaction);
        transaction.Commit();
    }

    private static void Execute(DbConnection connection, DbTransaction? transaction, string? sql)
    {
        if (sql is null) return;
        using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static string Describe(DbConnection connection) =>
        connection.GetType().Name.Contains("Sqlite", StringComparison.Ordinal) ? $"'{connection.DataSource}'" : $"'{connection.Database}' on '{connection.DataSource}'";
}
