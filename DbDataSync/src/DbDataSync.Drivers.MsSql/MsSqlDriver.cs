using DbDataKit.Connections.SqlServer;
using System.Data.Common;
using System.Diagnostics;
using DbDataSync.Core.Config;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;
using DbDataSync.Core.Sql;

namespace DbDataSync.Drivers.MsSql;

public sealed class MsSqlDriver : IDriver, IConnectionTester, IDialectProvider, ITableCatalogProvider, IProvisioner, ITableRowEstimator
{
    /// <summary>The catalog this driver's own components use, for anything composing a generic
    /// component for this engine from outside the driver.</summary>
    public ITableCatalog Catalog => MsSqlCatalog.Instance;

    /// <summary>What a script generating SQL for this engine is told about it.</summary>
    public SqlDialect Dialect => MsSqlDialect.Instance;

    public string DriverType => DriverIds.MsSql;

    /// <summary>SQL Server's default listening port, pre-filled on a new connection. Declared here
    /// rather than in the SPA, which had a table of these that a third driver would have made stale.</summary>
    public int? DefaultPort => 1433;

    /// <summary>Phase 109j: the <c>KnownLibraries</c> id this driver's own <c>SqlBulkCopy</c>/
    /// <c>SqlDbType</c>/<c>SqlConnectionStringBuilder</c> usage depends on — the same id
    /// <see cref="DbDataKit.State.MsSqlStateDialect.LibraryId"/> and
    /// <c>DriverConnectionFactory.BuiltInDriverLibraryIds</c> already resolve this exact package
    /// through, restated here as a literal rather than a cross-project constant reference (this driver
    /// project does not otherwise depend on <c>DbDataSync.State</c>).</summary>
    public string? RequiredLibraryId => "microsoft-data-sqlclient";

    // The generic implementations are registered alongside this driver's own, not instead of them.
    // SQL Server's prefixed ones are faster (SqlBulkCopy, MERGE) and stay the default; the portable
    // ones are what proves the generic pipeline against a working engine, and are a real fallback on
    // an instance where bulk insert is not permitted.
    //
    // MsSqlBatchReloadReader (phase 191S: retired) used to sit here too, under its own
    // "MsSqlBatchReload" Kind — it predated the generic pipeline (phase 9 vs. phase 18) and was never
    // migrated onto it: a one-line wrapper around SegmentScope supplying only an ISegmentValueBinder
    // (which BatchReloadReader's own constructor already accepts), plus a live catalog call phase 91's
    // cache-only migration never reached. The generic reader below is registered under both its own
    // Kind and "MsSqlBatchReload", so a mapping already saved against that name keeps resolving to a
    // real (and, unlike before, correctly cache-only) reader rather than losing its Kind outright.
    public IReadOnlyList<IChangeReader> Readers { get; } =
    [
        new MsSqlChangeTrackingReader(),
        new MsSqlCdcReader(),
        new TriggerAuditReader(MsSqlDialect.Instance, MsSqlCatalog.Instance),
        new WatermarkReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance),
        new BatchReloadReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance),
        new BatchReloadReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance, MsSqlDriverKinds.BatchReload),
        new KeyReconcileReader(MsSqlDialect.Instance, MsSqlValueBinding.Instance),
    ];

    public IReadOnlyList<IStagingProvider> StagingProviders { get; } =
    [
        new MsSqlStagingTableProvider(),
        new BatchInsertStagingProvider(MsSqlDialect.Instance, MsSqlCatalog.Instance),
    ];

    public IReadOnlyList<IChangeWriter> Writers { get; } =
    [
        new MsSqlMergeWriter(),
        new MsSqlMergeReconcileWriter(),
        new MsSqlDeleteInsertWriter(),
        new DeleteInsertWriter(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance),
        new KeyReconcileDeleteWriter(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance),
        new KeyReconcileScd2CloseWriter(MsSqlDialect.Instance, MsSqlCatalog.Instance, MsSqlValueBinding.Instance),
            new SnapshotWriter(MsSqlDialect.Instance, MsSqlCatalog.Instance),
        new Scd2Writer(MsSqlDialect.Instance, MsSqlCatalog.Instance),
    ];

    /// <summary>The shared SQL Server failure mapper (error numbers to kinds), for <see cref="DriverFailures"/>.</summary>
    public static DbDataKit.Connections.IFailureMapper FailureMapper { get; } = new SqlServerFailureMapper();

    public DbConnection CreateConnection(ConnectionConfig connection, string? credential)
    {
        // The shared composer builds the string (plan 13, A08): the operator's own string is still the base and the credential goes on
        // top, escaped by the builder. Encryption is Mandatory and the certificate is verified unless the operator's string or
        // `properties` say TrustServerCertificate / Encrypt otherwise; the old default of trusting any certificate is gone.
        string connectionString = SharedConnection.Compose(connection, credential, Composition);

        // Stamped here, at the one moment the config and the connection are in the same hand. Every
        // command raised through CreateTimedCommand() reads it back off the connection.
        return new SqlConnection(connectionString).WithCommandTimeout(connection);
    }

    private static readonly SharedConnectionOptions Composition = new()
    {
        Composer = new SqlServerComposer(),
        DefaultDatabase = "master",
        ConnectTimeoutKeys = ["Connect Timeout", "Connection Timeout", "ConnectTimeout"],
        TlsKeys = ["Encrypt", "TrustServerCertificate", "Trust Server Certificate", "Server Certificate", "ServerCertificate"],
        ReadTls = (written, tls) =>
        {
            if (written.TryGetValue("trustservercertificate", out var trust)) tls.TrustServerCertificate = IsTrue(trust);
            if (written.TryGetValue("encrypt", out var encrypt)) tls.Encrypt = !(IsFalse(encrypt) || encrypt.Equals("optional", StringComparison.OrdinalIgnoreCase));
            if (written.TryGetValue("servercertificate", out var pin)) tls.CaFile = pin;
        },
        // Required: a source read (an open, streaming SqlDataReader from CHANGETABLE/a batch query) and a staging
        // SqlBulkCopy/MERGE against the target can both be in flight on one connection at once when source and target
        // share a server. Without MARS that combination deadlocks rather than throwing — see
        // architecture/implementation/done/phase-003-mssql-driver.md. An operator who turned MARS off meant it.
        DefaultProperties = new Dictionary<string, string> { ["MultipleActiveResultSets"] = "True" },
        DefaultPropertySynonyms = new Dictionary<string, string[]> { ["MultipleActiveResultSets"] = ["MARS"] },
    };

    private static bool IsTrue(string value) => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase) || value == "1";
    private static bool IsFalse(string value) => value.Equals("false", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase) || value == "0";

    private static readonly HashSet<string> SystemDatabases = new(["master", "tempdb", "model", "msdb"], StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(DbConnection connection, CancellationToken cancellationToken) =>
        await SharedCatalog.DatabasesAsync(new DbDataKit.Metadata.SqlServerCatalogReader(connection, options: SharedCatalog.OptionsFor(connection)), SystemDatabases, cancellationToken);

    public async Task<IReadOnlyList<TableMetadata>> ListTablesAsync(
        DbConnection connection, string database, CancellationToken cancellationToken)
    {
        connection.ChangeDatabase(database);
        return await SharedCatalog.TablesAsync(new DbDataKit.Metadata.SqlServerCatalogReader(connection, options: SharedCatalog.OptionsFor(connection)), cancellationToken);
    }

    public async Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        DbConnection connection, string database, string schema, string table, CancellationToken cancellationToken)
    {
        connection.ChangeDatabase(database);
        return await MsSqlSchemaQueries.GetColumnsAsync(connection, schema, table, cancellationToken);
    }

    /// <summary>
    /// Sums the row counts the engine already maintains per heap/clustered-index partition
    /// (<c>index_id IN (0, 1)</c>) — no scan, and no permission beyond seeing the table itself, since
    /// <c>sys.partitions</c> is visibility-scoped to objects the login can already see. Null when the
    /// table resolves to nothing.
    /// </summary>
    public async Task<long?> EstimateRowCountAsync(DbConnection connection, TableRef table, CancellationToken cancellationToken)
    {
        connection.ChangeDatabase(table.Database);

        using var cmd = connection.CreateTimedCommand();
        cmd.CommandText = """
            SELECT SUM(p.rows)
            FROM sys.partitions p
            JOIN sys.tables t   ON t.object_id = p.object_id
            JOIN sys.schemas s  ON s.schema_id = t.schema_id
            WHERE s.name = @schema AND t.name = @table AND p.index_id IN (0, 1);
            """;
        cmd.AddParameter("@schema", table.Schema);
        cmd.AddParameter("@table", table.Table);

        var value = await cmd.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value);
    }

    /// <summary>
    /// Round-trips <c>SELECT @@VERSION</c>. Chosen because it touches no user object and needs no
    /// permission beyond connecting, so a successful test means "this login reaches this instance" and
    /// nothing more — which is exactly the question being asked.
    /// </summary>
    public async Task<ConnectionTestResult> TestAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            using var cmd = connection.CreateTimedCommand();
            cmd.CommandText = "SELECT @@VERSION;";
            var version = await cmd.ExecuteScalarAsync(cancellationToken) as string;

            return new ConnectionTestResult(
                Succeeded: true,
                Stopwatch.GetElapsedTime(started),
                // One line: @@VERSION spans four, and the rest is build and OS detail that turns a
                // status card into a wall of text.
                version?.Split('\n')[0].Trim(),
                Error: null);
        }
        catch (DbException ex)
        {
            // An unhealthy instance is an answer to the question, not a fault to propagate.
            return new ConnectionTestResult(false, Stopwatch.GetElapsedTime(started), ServerVersion: null, DriverFailures.Describe(ex, DriverIds.MsSql));
        }
    }

    public string? DefaultTestQuery => "SELECT @@VERSION;";

    public IReadOnlyList<string> SupportedActions => MsSqlProvisioner.SupportedActions;

    public Task<ProvisioningPlan> PlanAsync(DbConnection connection, ProvisioningRequest request, CancellationToken cancellationToken) =>
        MsSqlProvisioner.PlanAsync(connection, request, cancellationToken);
}