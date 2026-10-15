using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Apache.Arrow;

namespace DbDataProxy.Data;

/// <summary>Opens connections to one data source; one implementation per ADO.NET provider.</summary>
public interface IDbConnectionFactory
{
    DbConnection CreateConnection(string connectionString);
}

public sealed class DelegateConnectionFactory(Func<string, DbConnection> create) : IDbConnectionFactory
{
    public DbConnection CreateConnection(string connectionString) => create(connectionString);
}

/// <summary>Generic ADO.NET executor: any <see cref="DbConnection"/> becomes an Arrow query source.</summary>
public sealed class DbQueryExecutor(
    IDbConnectionFactory factory, string connectionString, ParameterStyle parameterStyle = ParameterStyle.Positional)
    : IQueryExecutor, IMetadataProvider
{
    public async Task<IQueryResult> ExecuteQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        var connection = factory.CreateConnection(connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = CreateCommand(connection, request);
            try
            {
                var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess, cancellationToken).ConfigureAwait(false);
                return new DbQueryResult(connection, command, reader, request.BatchSize);
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

    public async Task<long> ExecuteNonQueryAsync(QueryRequest request, CancellationToken cancellationToken)
    {
        await using var connection = factory.CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = CreateCommand(connection, request);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // The catalog functions go through the shared ODBC metadata package, which makes its own connection from the
    // connection string; the factory is only asked what kind of connection this source uses.
    private bool IsOdbc
    {
        get
        {
            using var connection = factory.CreateConnection(connectionString);
            return connection is System.Data.Odbc.OdbcConnection;
        }
    }

    private void RequireOdbc()
    {
        if (!IsOdbc) throw new NotSupportedException("Catalog-API metadata is only available for ODBC data sources.");
    }

    public Task<IReadOnlyList<TableEntry>> ListTablesAsync(int? commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        RequireOdbc();
        return OdbcMetadata.ListTablesAsync(connectionString, commandTimeoutSeconds, cancellationToken);
    }

    public Task<IReadOnlyList<ColumnEntry>> ListColumnsAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken)
    {
        RequireOdbc();
        return OdbcMetadata.ListColumnsAsync(connectionString, catalog, schema, table, cancellationToken);
    }

    public Task<IReadOnlyList<PrimaryKeyEntry>> ListPrimaryKeysAsync(string? catalog, string? schema, string table, CancellationToken cancellationToken)
    {
        RequireOdbc();
        return OdbcMetadata.ListPrimaryKeysAsync(connectionString, catalog, schema, table, cancellationToken);
    }

    public async Task<DialectInfo> GetDialectAsync(CancellationToken cancellationToken)
    {
        if (IsOdbc) return await OdbcMetadata.GetDialectAsync(connectionString, cancellationToken).ConfigureAwait(false);

        await using var connection = factory.CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return new DialectInfo(null, connection.ServerVersion, null);
    }

    private DbCommand CreateCommand(DbConnection connection, QueryRequest request)
    {
        var command = connection.CreateCommand();
        ParameterBinder.Bind(command, request.Sql, request.Parameters, parameterStyle);
        if (request.CommandTimeoutSeconds is { } timeout) command.CommandTimeout = timeout;
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

    public DbQueryResult(DbConnection connection, DbCommand command, DbDataReader reader, int batchSize)
    {
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
            int rows = 0;
            while (rows < _batchSize && await _reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                for (int c = 0; c < _columns.Length; c++) _columns[c].Append(_reader, c);
                rows++;
            }
            if (rows == 0) yield break;

            var arrays = new IArrowArray[_columns.Length];
            for (int c = 0; c < arrays.Length; c++) arrays[c] = _columns[c].Build();
            yield return new RecordBatch(Schema, arrays, rows);

            if (rows < _batchSize) yield break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _reader.DisposeAsync().ConfigureAwait(false);
        await _command.DisposeAsync().ConfigureAwait(false);
        await _connection.DisposeAsync().ConfigureAwait(false);
    }
}
