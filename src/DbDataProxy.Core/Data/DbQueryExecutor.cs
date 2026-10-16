using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Apache.Arrow;

namespace DbDataProxy.Data;

/// <summary>
/// Generic ADO.NET executor: any <see cref="DbConnection"/> opened through the shared provider becomes an Arrow query source. Whatever the
/// driver throws leaves as a <see cref="DbDataKit.Connections.ConnectionFailureException"/> (kind and code, never the driver's text); see <see cref="FailureTranslator"/>.
/// </summary>
public sealed class DbQueryExecutor(SourceConnection source, FailureTranslator? failures = null) : IQueryExecutor, IMetadataProvider
{
    private readonly FailureTranslator _failures = failures ?? new FailureTranslator(source.Provider.FailureMapper, source.Provider.Engine, source.Definition.Name);

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private void Rethrow(Exception ex)
    {
        var translated = _failures.Translate(ex);
        if (ReferenceEquals(translated, ex)) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();
        throw translated;
    }

    public async Task<IQueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var command = CreateCommand(connection, request);
                try
                {
                    var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
                    return new DbQueryResult(connection, command, reader, request.BatchSize, _failures);
                }
                catch
                {
                    await command.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Rethrow(ex);
            throw;
        }
    }

    public async Task<long> ExecuteNonQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = CreateCommand(connection, request);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Rethrow(ex);
            throw;
        }
    }

    // The catalog functions go through the shared ODBC metadata package, which makes its own connection from the composed connection string.
    private bool IsOdbc => source.Engine == "odbc";

    private void RequireOdbc()
    {
        if (!IsOdbc) throw new UnsupportedValueException("Catalog-API metadata is only available for ODBC data sources.");
    }

    private async Task<T> GuardAsync<T>(Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Rethrow(ex);
            throw;
        }
    }

    public Task<IReadOnlyList<TableEntry>> ListTablesAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        RequireOdbc();
        return GuardAsync(() => OdbcMetadata.ListTablesAsync(source.Compose(), commandTimeoutSeconds, cancellationToken));
    }

    public Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken)
    {
        RequireOdbc();
        return GuardAsync(() => OdbcMetadata.ListColumnsAsync(source.Compose(), catalog, schema, table, cancellationToken));
    }

    public Task<IReadOnlyList<PrimaryKeyEntry>> ListPrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken)
    {
        RequireOdbc();
        return GuardAsync(() => OdbcMetadata.ListPrimaryKeysAsync(source.Compose(), catalog, schema, table, cancellationToken));
    }

    public Task<DialectInfo> GetDialectAsync(CancellationToken cancellationToken) => GuardAsync(async () =>
    {
        if (IsOdbc) return await OdbcMetadata.GetDialectAsync(source.Compose(), cancellationToken).ConfigureAwait(false);

        await using var connection = await source.OpenAsync(cancellationToken).ConfigureAwait(false);
        return new DialectInfo(null, connection.ServerVersion, null);
    });

    private DbCommand CreateCommand(DbConnection connection, QueryRequest request)
    {
        var command = connection.CreateCommand();
        ParameterBinder.Bind(command, request.Sql, request.Parameters, source.ParameterStyle, source.Dialect);
        // Never "forever": a request without its own timeout gets the definition's (the data source's CommandTimeoutSeconds, else the shared default).
        command.CommandTimeout = request.CommandTimeoutSeconds ?? source.Definition.Timeouts.CommandS;
        return command;
    }
}

internal sealed class DbQueryResult : IQueryResult
{
    private readonly DbConnection _connection;
    private readonly DbCommand _command;
    private readonly DbDataReader _reader;
    private readonly ArrowColumnWriter[] _columns;
    private readonly int _batchSize;
    private readonly FailureTranslator _failures;

    public DbQueryResult(DbConnection connection, DbCommand command, DbDataReader reader, int batchSize, FailureTranslator failures)
    {
        _failures = failures;
        _connection = connection;
        _command = command;
        _reader = reader;
        _batchSize = Math.Max(1, batchSize);

        var meta = ColumnMeta.Read(reader);
        _columns = new ArrowColumnWriter[reader.FieldCount];
        var fields = new List<Field>(_columns.Length);
        for (int i = 0; i < _columns.Length; i++)
        {
            _columns[i] = ArrowColumnWriter.Create(reader, i, meta[i]);
            fields.Add(_columns[i].Field);
        }
        Schema = new Schema(fields, null);
    }

    public Schema Schema { get; }

    public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (true)
        {
            int rows = await ReadRowsAsync(cancellationToken).ConfigureAwait(false);
            if (rows == 0) yield break;

            var arrays = new IArrowArray[_columns.Length];
            for (int c = 0; c < arrays.Length; c++) arrays[c] = _columns[c].Build();
            yield return new RecordBatch(Schema, arrays, rows);

            if (rows < _batchSize) yield break;
        }
    }

    // A driver error in the middle of a stream is translated like one at the start: kind and code, never the driver's text.
    private async Task<int> ReadRowsAsync(CancellationToken cancellationToken)
    {
        int rows = 0;
        try
        {
            while (rows < _batchSize && await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                for (int c = 0; c < _columns.Length; c++) _columns[c].Append(_reader, c);
                rows++;
            }
            return rows;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var translated = _failures.Translate(ex);
            if (ReferenceEquals(translated, ex)) throw;
            throw translated;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.DisposeAsync().ConfigureAwait(false);
        await _command.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
