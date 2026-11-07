using System.Data.Common;
using DbDataKit.Dialect;
using DbDataKit.Transfer;
using DbDataKit.Transfer.SqlServer;
using DbDataSync.Core.Config;
using DbDataSync.Core.Sql;
using DbDataSync.Drivers.Abstractions;
using DbDataSync.Drivers.Generic;
using Microsoft.Data.SqlClient;

namespace DbDataSync.Drivers.MsSql;

/// <summary>
/// Loads the reader's rows straight into the target with <see cref="SqlBulkCopy"/> — no staging table, no
/// second copy in tempdb, no merge. For a full load or reload of a large table that is the difference
/// between writing the data twice and writing it once.
/// <para>
/// **Paired with the <c>PassThrough</c> cache**, which hands this writer the reader's stream untouched
/// (<see cref="IPassThroughWriter"/>). Nothing is staged, so there is nothing to match against: every row
/// is inserted, and the writer first clears the scope it is about to fill. That is why it reconciles —
/// whatever the target held in scope is replaced by what the source holds.
/// </para>
/// <para>
/// Two settings:
/// <list type="bullet">
/// <item><c>clear</c> — <c>Delete</c> (the default) removes the rows in this pass's scope, the whole table
/// when unsegmented or just the segment when segmented. <c>Truncate</c> empties the table with
/// <c>TRUNCATE TABLE</c>, which is far cheaper but has no scope: it is refused for a segmented pass (each
/// segment would wipe the last one's rows) and for a table a foreign key references, where SQL Server
/// refuses it anyway — both with a message saying to use <c>Delete</c>.</item>
/// <item><c>transaction</c> — on (the default), the clear and the load commit together, so a failed load
/// leaves the old rows and readers never see the table empty, at the cost of holding locks for the whole
/// load. Off, they are separate: a failure leaves the table cleared or partly loaded until the next run.</item>
/// </list>
/// </para>
/// <para>
/// Insert-only: a row that is not an insert (a change feed's update or delete) fails the pass rather than
/// being quietly mis-applied. Behind an incremental reader this writer is refused at save time, like every
/// reconciling writer.
/// </para>
/// </summary>
public sealed class MsSqlBulkInsertWriter : IPassThroughWriter, IStatementPreview
{
    public const string ClearOption = "clear";
    public const string TransactionOption = "transaction";
    public const string ClearDelete = "Delete";
    public const string ClearTruncate = "Truncate";

    public string Kind => MsSqlDriverKinds.BulkInsert;

    public bool SupportsReconciliation => true;

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new ParameterDescriptor
        {
            Name = ClearOption,
            Label = "Clear the target first",
            Description =
                "Delete removes the rows in this pass's scope (the whole table, or just the segment when the " +
                "load is segmented). Truncate empties the whole table and is much cheaper, but is refused for a " +
                "segmented load and for a table a foreign key references.",
            Type = ParameterType.Dropdown,
            DropdownOptions = [ClearDelete, ClearTruncate],
            Default = ClearDelete,
        },
        new ParameterDescriptor
        {
            Name = TransactionOption,
            Label = "Clear and load in one transaction",
            Description =
                "On, a failed load leaves the previous rows in place and readers never see the table empty, " +
                "but locks are held for the whole load. Off, a failure leaves the table cleared or partly " +
                "loaded until the next run.",
            Type = ParameterType.Bool,
            Default = "true",
        },
    ];

    /// <summary>The settings as configured; an unstated or unrecognised one takes its default, a
    /// recognised-but-wrong one is an error naming the setting.</summary>
    internal static (bool Truncate, bool Transaction) ReadOptions(IReadOnlyDictionary<string, string> options)
    {
        var truncate = false;
        if (options.TryGetValue(ClearOption, out var clear) && !string.IsNullOrWhiteSpace(clear))
        {
            if (string.Equals(clear.Trim(), ClearTruncate, StringComparison.OrdinalIgnoreCase)) truncate = true;
            else if (!string.Equals(clear.Trim(), ClearDelete, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Writer option '{ClearOption}' is '{clear}'; it must be '{ClearDelete}' or '{ClearTruncate}'.");
        }

        var transaction = true;
        if (options.TryGetValue(TransactionOption, out var raw) && !string.IsNullOrWhiteSpace(raw))
        {
            if (!bool.TryParse(raw.Trim(), out transaction))
                throw new InvalidOperationException(
                    $"Writer option '{TransactionOption}' is '{raw}'; it must be 'true' or 'false'.");
        }

        return (truncate, transaction);
    }

    public async Task<WriteResult> ApplyAsync(
        DbConnection targetConnection,
        TableRef target,
        StagedChangeSet staged,
        IReadOnlyList<ColumnMapping> columnMappings,
        string mappingName,
        IReadOnlyList<CachedColumn> targetColumns,
        IReadOnlyDictionary<string, string> options,
        CancellationToken cancellationToken)
    {
        if (staged.Stream is null)
            throw new InvalidOperationException(
                $"Writer '{Kind}' loads straight into the target and needs the '{GenericDriverKinds.PassThrough}' " +
                "cache; this pass staged its rows into a table instead. Set the cache to " +
                $"'{GenericDriverKinds.PassThrough}'.");

        var (truncate, useTransaction) = ReadOptions(options);

        targetConnection.ChangeDatabase(target.Database);
        var shape = MsSqlTargetShape.FromCachedColumns(mappingName, targetColumns, target, columnMappings);
        var segment = ReadSegment(options);
        var scope = SegmentScope.Resolve(segment, shape.Columns, columnMappings);

        if (truncate && segment is not null)
            throw new InvalidOperationException(TruncateSegmentedMessage(mappingName));

        var connection = (SqlConnection)targetConnection;
        await using var transaction = useTransaction
            ? (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            if (truncate)
            {
                await EnsureTruncatable(connection, transaction, shape, mappingName, cancellationToken);
                await ExecuteAsync(connection, transaction, $"TRUNCATE TABLE {shape.QuotedTarget};", cancellationToken);
            }
            else
            {
                var delete = MsSqlDeleteInsertWriter.BuildDelete(shape, scope.Filter);
                using var deleteCmd = connection.CreateTimedCommand();
                deleteCmd.Transaction = transaction;
                deleteCmd.CommandText = delete.Text;
                SegmentScope.AddTo(deleteCmd, MsSqlDialect.Instance, MsSqlValueBinding.Instance, delete.Slots, scope.Column);
                await deleteCmd.ExecuteNonQueryAsync(cancellationToken);
            }

            var written = await BulkInsertAsync(
                connection, transaction, shape, staged.Stream.Take(), columnMappings, mappingName, cancellationToken);

            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken);
            return new WriteResult(written);
        }
        catch
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    /// <summary>The pass's segment, or null when it is not segmented. A whole-table Bulk Load still carries
    /// an explicit <see cref="FullSegment"/> in its options, which is the whole table, not a part of it.</summary>
    internal static BatchReloadSegment? ReadSegment(IReadOnlyDictionary<string, string> options) =>
        SegmentSerializer.ReadOptional(options) is FullSegment ? null : SegmentSerializer.ReadOptional(options);

    internal static string TruncateSegmentedMessage(string mappingName) =>
        $"Table mapping '{mappingName}' loads in segments, and '{ClearOption}: {ClearTruncate}' empties the whole " +
        "table — each segment would remove the rows the one before it loaded. Set the writer's " +
        $"'{ClearOption}' option to '{ClearDelete}', which removes only the segment's own rows, or load the " +
        "table without segments.";

    /// <summary>TRUNCATE TABLE is refused by SQL Server for a table a foreign key references, with an error
    /// that does not say what to change. Checked first so the failure names the setting.</summary>
    private static async Task EnsureTruncatable(
        SqlConnection connection, SqlTransaction? transaction, MsSqlTargetShape shape, string mappingName,
        CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            "SELECT TOP (1) OBJECT_NAME(parent_object_id) FROM sys.foreign_keys WHERE referenced_object_id = OBJECT_ID(@target);";
        cmd.AddParameter("@target", shape.QuotedTarget);
        if (await cmd.ExecuteScalarAsync(cancellationToken) is string referencedBy)
            throw new InvalidOperationException(
                $"Table mapping '{mappingName}' cannot truncate {shape.QuotedTarget}: a foreign key on " +
                $"'{referencedBy}' references it, and SQL Server does not allow TRUNCATE TABLE then. Set the " +
                $"writer's '{ClearOption}' option to '{ClearDelete}'.");
    }

    private static async Task ExecuteAsync(
        SqlConnection connection, SqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        using var cmd = connection.CreateTimedCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> BulkInsertAsync(
        SqlConnection connection, SqlTransaction? transaction, MsSqlTargetShape shape, IAsyncEnumerable<ChangeRow> rows,
        IReadOnlyList<ColumnMapping> columnMappings, string mappingName, CancellationToken cancellationToken)
    {
        var mapped = shape.MappedTargetColumns;
        var typeByName = shape.Columns.ToDictionary(c => c.Name, c => c.NativeType, StringComparer.OrdinalIgnoreCase);
        var sourceColumnByTarget = columnMappings
            .GroupBy(m => m.TargetColumn, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().SourceColumn, StringComparer.OrdinalIgnoreCase);

        var schema = StagingBatches.Schema(EngineId.SqlServer, mapped, typeByName, includeChangeOrdering: false, operationColumn: null);
        var gate = StagingValueGate.For(EngineId.SqlServer, $"{shape.Target.Schema}.{shape.Target.Table}", mapped, typeByName, schema);
        var counter = new StagingBatches.Counter();
        var batches = StagingBatches.Batch(
            InsertsOnly(rows, mappingName), schema, mapped, sourceColumnByTarget, includeChangeOrdering: false, counter, gate,
            cancellationToken: cancellationToken, hasOperationColumn: false);

        var bulkOptions = new BulkOptions
        {
            // Each batch is its own commit without a transaction; with one, they all belong to it.
            Transaction = transaction is null ? TransactionMode.None : TransactionMode.Single,
            KeepIdentity = shape.RequiresIdentityInsert,
            Timeout = TimeSpan.FromSeconds(ConnectionTimeouts.CommandTimeoutOf(connection)),
        };
        var writer = new SqlServerBulkWriter(connection, transaction);
        return await writer.WriteAsync(
            new WriteTarget(new TableName(shape.Target.Schema, shape.Target.Table), schema), batches, bulkOptions, cancellationToken);
    }

    private static async IAsyncEnumerable<ChangeRow> InsertsOnly(
        IAsyncEnumerable<ChangeRow> rows, string mappingName,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var row in rows.WithCancellation(cancellationToken))
        {
            if (row.Operation != ChangeOperation.Insert)
                throw new InvalidOperationException(
                    $"Table mapping '{mappingName}' got a {row.Operation} row, but '{MsSqlDriverKinds.BulkInsert}' " +
                    "only inserts. It loads a full read of the source; behind a change feed use an upsert writer.");
            yield return row;
        }
    }

    public async Task<IReadOnlyList<PreviewStatement>> DescribeAsync(
        PreviewRequest request, CancellationToken cancellationToken)
    {
        request.Connection.ChangeDatabase(request.Target.Database);
        var (truncate, useTransaction) = ReadOptions(request.Options);
        var shape = await MsSqlTargetShape.LoadAsync(
            request.Connection, request.Target, request.ColumnMappings, cancellationToken);
        var segment = ReadSegment(request.Options);

        if (truncate && segment is not null)
            return [new PreviewStatement(PreviewStages.Write, "Empty the target", null, PreviewOrigin.BuiltIn,
                TruncateSegmentedMessage("this mapping") + " This pass would fail here.")];

        var transactionNote = useTransaction
            ? "The clear and the load run in one transaction."
            : "No transaction: a failure leaves the target cleared or partly loaded.";
        var clear = truncate
            ? new PreviewStatement(
                PreviewStages.Write, "Empty the target", $"TRUNCATE TABLE {shape.QuotedTarget};", PreviewOrigin.BuiltIn, transactionNote)
            : new PreviewStatement(
                PreviewStages.Write, "Empty the scope",
                MsSqlDeleteInsertWriter.BuildDelete(shape, SegmentScope.Resolve(segment, shape.Columns, request.ColumnMappings).Filter).Text,
                PreviewOrigin.BuiltIn,
                (segment is null ? "Unsegmented, that scope is the whole table. " : $"Scoped to {segment.Describe()}. ") + transactionNote);

        return
        [
            clear,
            new PreviewStatement(
                PreviewStages.Write, "Load the rows directly into the target", null, PreviewOrigin.BuiltIn,
                $"SqlBulkCopy into {shape.QuotedTarget}, with nothing staged — a bulk stream rather than a statement, " +
                "which is why there is none to show."),
        ];
    }
}
