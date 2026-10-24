using DbDataKit.Connections.Postgres;
using System.Data.Common;
using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Npgsql;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.Postgres;

/// <summary>
/// PostgreSQL, on Npgsql.
/// <para>
/// Most of this is not its own. Every writer, four of the five readers and one of the two staging
/// providers is <c>DbDataSync.Drivers.Generic</c>'s, driven by <see cref="PostgresDialect"/>. That is
/// the claim phases 17 and 18 made — a new engine is a dialect, a connection factory and a catalog —
/// and it held unbroken for twenty phases.
/// </para>
/// <para>
/// **Two things broke it, and what broke it is the same thing twice**: a mechanism that is not a
/// statement, so no <see cref="SqlDialect"/> hook could have expressed it.
/// <see cref="PgCopyStagingProvider"/> (phase 38) is a protocol on the connection, and
/// <see cref="PgLogicalSlotReader"/> (phase 34) reads a Postgres function no other engine has. The
/// generic staging provider stays registered alongside the first, as the fallback for anything binary
/// <c>COPY</c> will not carry.
/// </para>
/// </summary>
public sealed class PostgresDriver : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider, IProvisioner, ITableRowEstimator
{
    /// <summary>The catalog this driver's own components use, for anything composing a generic
    /// component for this engine from outside the driver.</summary>
    public ITableCatalog Catalog => PostgresCatalog.Instance;

    /// <summary>What a script generating SQL for this engine is told about it.</summary>
    public SqlDialect Dialect => PostgresDialect.Instance;

    public string DriverType => DriverIds.Postgres;

    /// <summary>Postgres's default listening port, pre-filled on a new connection. Declared here
    /// rather than in the SPA, which had a table of these that a third driver would have made stale.</summary>
    public int? DefaultPort => 5432;

    /// <summary>Phase 109j: the <c>KnownLibraries</c> id this driver's own <c>NpgsqlDbType</c>/
    /// <c>NpgsqlConnectionStringBuilder</c> usage depends on — the same id
    /// <see cref="DbDataSync.State.PostgresStateDialect.LibraryId"/> and
    /// <c>DriverConnectionFactory.BuiltInDriverLibraryIds</c> already resolve this exact package
    /// through, restated here as a literal for the same reason <c>MsSqlDriver</c>'s own copy is.</summary>
    public string? RequiredLibraryId => "npgsql";

    public IReadOnlyList<IChangeReader> Readers { get; } =
    [
        new WatermarkReader(PostgresDialect.Instance, PostgresValueBinding.Instance),
        new TriggerAuditReader(PostgresDialect.Instance, PostgresCatalog.Instance),
        new BatchReloadReader(PostgresDialect.Instance, PostgresValueBinding.Instance),
        new KeyReconcileReader(PostgresDialect.Instance, PostgresValueBinding.Instance),
        // Phase 34: the first Postgres-specific reader. Logical decoding is a plain SQL function
        // returning rows, which is what lets it be a reader at all rather than a streaming subsystem.
        new PgLogicalSlotReader(PostgresDialect.Instance),
    ];

    /// <summary>
    /// Binary COPY first, batched INSERT second — phase 38. Both stage into the same table with the
    /// same DDL, so a mapping can be moved between them without anything downstream noticing; the
    /// generic one stays registered because binary COPY converts nothing and an instance that will not
    /// permit COPY, or a column whose value Npgsql cannot write in the column's own wire format, needs
    /// a path that works. Order matters only for which one a new mapping is offered first.
    /// </summary>
    public IReadOnlyList<IStagingProvider> StagingProviders { get; } =
    [
        new PgCopyStagingProvider(PostgresDialect.Instance, PostgresCatalog.Instance),
        new BatchInsertStagingProvider(PostgresDialect.Instance, PostgresCatalog.Instance),
    ];

    public IReadOnlyList<IChangeWriter> Writers { get; } =
    [
        new DeleteInsertWriter(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance),
        new KeyReconcileDeleteWriter(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance),
        new KeyReconcileScd2CloseWriter(PostgresDialect.Instance, PostgresCatalog.Instance, PostgresValueBinding.Instance),
        new SnapshotWriter(PostgresDialect.Instance, PostgresCatalog.Instance),
        new Scd2Writer(PostgresDialect.Instance, PostgresCatalog.Instance),
    ];

    /// <summary>The shared PostgreSQL failure mapper (SQLSTATE to kinds), for <see cref="DriverFailures"/>.</summary>
    public static DbDataKit.Connections.IFailureMapper FailureMapper { get; } = new PostgresFailureMapper();

    public DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        // The shared composer builds the string (plan 13, A08): the operator's own string is still the base and the credential goes on
        // top, escaped by the builder. SSL Mode is VerifyFull unless the operator's string or `properties` name another mode.
        string connectionString = SharedConnection.Compose(connection, credential, Composition);

        // Stamped here, at the one moment the config and the connection are in the same hand. Every
        // command raised through CreateTimedCommand() reads it back off the connection.
        return new NpgsqlConnection(connectionString).WithCommandTimeout(connection);
    }

    private static readonly SharedConnectionOptions Composition = new()
    {
        Composer = new PostgresComposer(),
        DefaultDatabase = "postgres",
        // Npgsql spells the connect timeout `Timeout`, and means the same thing SqlClient's ConnectTimeout does.
        ConnectTimeoutKeys = ["Timeout"],
        TlsKeys = ["SSL Mode", "SslMode", "Root Certificate", "RootCertificate", "SSL Root Certificate"],
        ReadTls = (written, tls) =>
        {
            if (written.TryGetValue("sslmode", out var mode))
            {
                if (mode.Equals("disable", StringComparison.OrdinalIgnoreCase)) tls.Encrypt = false;
                else if (!mode.StartsWith("verify", StringComparison.OrdinalIgnoreCase)) tls.Verify = false; // allow, prefer, require: encrypted when it can be, not verified
            }
            if (written.TryGetValue("rootcertificate", out var root)) tls.CaFile = root;
            else if (written.TryGetValue("sslrootcertificate", out var sslRoot)) tls.CaFile = sslRoot;
        },
        // Kerberos/GSSAPI or peer auth, depending on the server's pg_hba.conf. Npgsql needs a username either way; there
        // is no equivalent of SQL Server's "integrated security" flag that supplies one.
        IntegratedNeedsUser = true,
    };

    /// <summary>
    /// <c>datistemplate = false</c> drops template0/template1; <c>datallowconn</c> drops anything the
    /// server will not accept a connection to, which would only ever produce a failure downstream.
    /// </summary>
    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = "SELECT datname FROM pg_database WHERE datistemplate = false AND datallowconn = true ORDER BY datname;";

        var results = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            results.Add(reader.GetString(0));
        return results;
    }

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken)
    {
        await PostgresDialect.Instance.UseDatabaseAsync(connection, database, cancellationToken);
        return await PostgresCatalog.Instance.ListTablesAsync(connection, cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        await PostgresDialect.Instance.UseDatabaseAsync(connection, database, cancellationToken);
        return await PostgresCatalog.Instance.GetColumnsAsync(connection, schema, table, cancellationToken);
    }

    /// <summary>
    /// Reads <c>pg_class.reltuples</c>, the planner's own row estimate — set by the last
    /// <c>ANALYZE</c>/<c>VACUUM</c>, no scan. It is <c>-1</c> on a table that has never been analysed
    /// (PostgreSQL 14+); that and a missing table both come back as null rather than as a count.
    /// </summary>
    public async Task<long?> EstimateRowCountAsync(DbConnection connection, TableRef table, CancellationToken cancellationToken)
    {
        await PostgresDialect.Instance.UseDatabaseAsync(connection, table.Database, cancellationToken);

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT c.reltuples::bigint
            FROM pg_class c
            JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = @schema AND c.relname = @table;
            """;
        cmd.AddParameter("@schema", table.Schema);
        cmd.AddParameter("@table", table.Table);

        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
            return null;
        var estimate = Convert.ToInt64(value);
        return estimate < 0 ? null : estimate;
    }

    /// <summary>Round-trips <c>version()</c> — no user object, no permission beyond connecting.</summary>
    public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.CommandText = "SELECT version();";
            var version = await cmd.ExecuteScalarAsync(cancellationToken) as string;

            return new ConnectionTestResult(true, Stopwatch.GetElapsedTime(started), version?.Split('\n')[0].Trim(), null);
        }
        catch (DbException ex)
        {
            return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), null, DriverFailures.Describe(ex, DriverIds.Postgres));
        }
    }

    public string? DefaultTestQuery => "SELECT version();";

    public IReadOnlyList<string> SupportedActions => PostgresProvisioner.SupportedActions;

    public Task<ProvisioningPlan> PlanAsync(DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken) =>
        PostgresProvisioner.PlanAsync(connection, request, cancellationToken);
}
