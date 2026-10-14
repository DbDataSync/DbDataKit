using System.Reflection;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Ipc;
using Arrow.Flight.Protocol.Sql;
using DbDataProxy.Data;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;

namespace DbDataProxy.Client;

/// <summary>Per-call choices a client can make against the proxy.</summary>
public sealed record QueryOptions
{
    /// <summary>Name of the configured data source (sent as the <c>x-datasource</c> header).</summary>
    public string? DataSource { get; init; }

    /// <summary>Overrides the server's compression for this call: none, gzip, zstd or lz4 (sent as <c>x-compression</c>).</summary>
    public string? Compression { get; init; }
}

/// <summary>An executing query: its schema plus the stream of record batches.</summary>
public sealed class QueryResultStream(Schema schema, IAsyncEnumerable<RecordBatch> batches) : IAsyncEnumerable<RecordBatch>
{
    public Schema Schema { get; } = schema;
    public IAsyncEnumerator<RecordBatch> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        batches.GetAsyncEnumerator(cancellationToken);
}

/// <summary>
/// Flight SQL client for the proxy: performs the Basic-auth handshake, attaches the bearer token to every call and,
/// unlike the stock .NET Flight client, can read result streams that use Arrow data compression (zstd/lz4).
/// </summary>
public sealed class ProxyFlightClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly FlightClient _flight;
    private string? _authorization;

    public ProxyFlightClient(string address, GrpcChannelOptions? channelOptions = null)
    {
        _channel = GrpcChannel.ForAddress(address, channelOptions ?? new GrpcChannelOptions());
        _flight = new FlightClient(_channel);
    }

    /// <summary>The <c>authorization</c> header value in use (<c>Bearer ...</c> after a successful handshake).</summary>
    public string? Authorization
    {
        get => _authorization;
        set => _authorization = value;
    }

    /// <summary>Exchanges a username and password for a bearer token.</summary>
    public async Task AuthenticateAsync(string user, string password, CancellationToken cancellationToken = default)
    {
        string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        using var call = _flight.Handshake(new Metadata { { "authorization", "Basic " + basic } }, null, cancellationToken);
        try
        {
            await call.RequestStream.WriteAsync(new FlightHandshakeRequest(ByteString.Empty, 0)).ConfigureAwait(false);
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.Cancelled)
        {
            // The server answers from the Basic header without waiting for this (empty) message, so it can finish the
            // call before the write returns and .NET reports the write as cancelled. What matters is the response: a
            // real failure (rejected credentials, a dead connection) is thrown by the response read below.
        }

        var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)) { /* payload carries the token too */ }
        _authorization = headers.GetValue("authorization")
            ?? throw new InvalidOperationException("The server did not return an authorization token.");
    }

    /// <summary>Runs a query and returns its schema and a lazily-read stream of batches.</summary>
    public Task<QueryResultStream> QueryAsync(string sql, QueryOptions? options = null, CancellationToken cancellationToken = default) =>
        RunCommandAsync(new CommandStatementQuery { Query = sql }, options, cancellationToken);

    /// <summary>Catalog names (Flight SQL GetCatalogs).</summary>
    public Task<QueryResultStream> GetCatalogsAsync(QueryOptions? options = null, CancellationToken cancellationToken = default) =>
        RunCommandAsync(new CommandGetCatalogs(), options, cancellationToken);

    /// <summary>Schemas, optionally filtered by catalog and a SQL LIKE pattern (Flight SQL GetDbSchemas).</summary>
    public Task<QueryResultStream> GetSchemasAsync(string? catalog = null, string? schemaPattern = null, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var command = new CommandGetDbSchemas();
        if (catalog is not null) command.Catalog = catalog;
        if (schemaPattern is not null) command.DbSchemaFilterPattern = schemaPattern;
        return RunCommandAsync(command, options, cancellationToken);
    }

    /// <summary>Tables and views (Flight SQL GetTables); <paramref name="includeSchema"/> adds each table's Arrow schema.</summary>
    public Task<QueryResultStream> GetTablesAsync(
        string? catalog = null, string? schemaPattern = null, string? tablePattern = null, IEnumerable<string>? tableTypes = null,
        bool includeSchema = false, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var command = new CommandGetTables { IncludeSchema = includeSchema };
        if (catalog is not null) command.Catalog = catalog;
        if (schemaPattern is not null) command.DbSchemaFilterPattern = schemaPattern;
        if (tablePattern is not null) command.TableNameFilterPattern = tablePattern;
        if (tableTypes is not null) command.TableTypes.AddRange(tableTypes);
        return RunCommandAsync(command, options, cancellationToken);
    }

    public Task<QueryResultStream> GetTableTypesAsync(QueryOptions? options = null, CancellationToken cancellationToken = default) =>
        RunCommandAsync(new CommandGetTableTypes(), options, cancellationToken);

    public Task<QueryResultStream> GetPrimaryKeysAsync(string table, string? catalog = null, string? schema = null, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var command = new CommandGetPrimaryKeys { Table = table };
        if (catalog is not null) command.Catalog = catalog;
        if (schema is not null) command.DbSchema = schema;
        return RunCommandAsync(command, options, cancellationToken);
    }

    public Task<QueryResultStream> GetSqlInfoAsync(QueryOptions? options = null, CancellationToken cancellationToken = default) =>
        RunCommandAsync(new CommandGetSqlInfo(), options, cancellationToken);

    private async Task<QueryResultStream> RunCommandAsync(IMessage command, QueryOptions? options, CancellationToken cancellationToken)
    {
        var headers = Headers(options);
        var descriptor = FlightDescriptor.CreateCommandDescriptor(Any.Pack(command).ToByteArray());

        FlightInfo info;
        using (var infoCall = _flight.GetInfo(descriptor, headers, null, cancellationToken))
            info = await infoCall.ResponseAsync.ConfigureAwait(false);

        return new QueryResultStream(info.Schema, ReadEndpointsAsync(info, headers));
    }

    /// <summary>
    /// Runs a query with parameters. The statement is prepared on the server, the values are bound with a DoPut and the
    /// statement is closed once the result has been read. Use <c>?</c> markers (or <c>@name</c> for SQL Server).
    /// </summary>
    public Task<QueryResultStream> QueryAsync(
        string sql, IReadOnlyList<object?> parameters, QueryOptions? options = null, CancellationToken cancellationToken = default) =>
        QueryAsync(sql, ArrowParameters.FromValues(parameters), options, cancellationToken);

    /// <summary>Runs a query with named parameters (<c>@name</c> markers on SQL Server); the keys are the parameter names.</summary>
    public Task<QueryResultStream> QueryAsync(
        string sql, IReadOnlyDictionary<string, object?> namedParameters, QueryOptions? options = null, CancellationToken cancellationToken = default) =>
        QueryAsync(sql, ArrowParameters.FromValues([.. namedParameters.Values], [.. namedParameters.Keys]), options, cancellationToken);

    /// <summary>Runs a query with an Arrow batch of parameters (one row).</summary>
    public async Task<QueryResultStream> QueryAsync(
        string sql, RecordBatch parameters, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var headers = Headers(options);
        var handle = await PrepareAsync(sql, headers, cancellationToken).ConfigureAwait(false);
        try
        {
            await BindAsync(new CommandPreparedStatementQuery { PreparedStatementHandle = handle }, parameters, headers, cancellationToken).ConfigureAwait(false);
            var descriptor = FlightDescriptor.CreateCommandDescriptor(
                Any.Pack(new CommandPreparedStatementQuery { PreparedStatementHandle = handle }).ToByteArray());
            FlightInfo info;
            using (var infoCall = _flight.GetInfo(descriptor, headers, null, cancellationToken))
                info = await infoCall.ResponseAsync.ConfigureAwait(false);
            return new QueryResultStream(info.Schema, ReadThenCloseAsync(info, headers, handle));
        }
        catch
        {
            await CloseAsync(handle, headers).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Runs an INSERT/UPDATE/DDL statement with parameters; one execution per call, affected rows returned.</summary>
    public Task<long> ExecuteUpdateAsync(
        string sql, IReadOnlyList<object?> parameters, QueryOptions? options = null, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(sql, [parameters], options, cancellationToken);

    /// <summary>Runs an update with named parameters (<c>@name</c> markers on SQL Server).</summary>
    public Task<long> ExecuteUpdateAsync(
        string sql, IReadOnlyDictionary<string, object?> namedParameters, QueryOptions? options = null, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(sql, [ArrowParameters.FromValues([.. namedParameters.Values], [.. namedParameters.Keys])], options, cancellationToken);

    /// <summary>Runs one statement once per parameter row (a batch insert); the affected counts are summed.</summary>
    public Task<long> ExecuteUpdateAsync(
        string sql, IReadOnlyList<IReadOnlyList<object?>> parameterRows, QueryOptions? options = null, CancellationToken cancellationToken = default) =>
        ExecuteUpdateAsync(sql, parameterRows.Select(row => ArrowParameters.FromValues(row)).ToList(), options, cancellationToken);

    /// <summary>Runs one statement once per parameter-batch row; all batches must share a schema.</summary>
    public async Task<long> ExecuteUpdateAsync(
        string sql, IReadOnlyList<RecordBatch> batches, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var headers = Headers(options);
        var handle = await PrepareAsync(sql, headers, cancellationToken).ConfigureAwait(false);
        try
        {
            var command = new CommandPreparedStatementUpdate { PreparedStatementHandle = handle };
            var descriptor = FlightDescriptor.CreateCommandDescriptor(Any.Pack(command).ToByteArray());

            using var call = await _flight.StartPut(descriptor, batches[0].Schema, headers, null, cancellationToken).ConfigureAwait(false);
            foreach (var batch in batches) await call.RequestStream.WriteAsync(batch).ConfigureAwait(false);
            await call.RequestStream.CompleteAsync().ConfigureAwait(false);

            long affected = -1;
            while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                affected = DoPutUpdateResult.Parser.ParseFrom(call.ResponseStream.Current.ApplicationMetadata).RecordCount;
            return affected;
        }
        finally
        {
            await CloseAsync(handle, headers).ConfigureAwait(false);
        }
    }

    /// <summary>Prepares the statement on the server, reads the parameter schema it reports (one field per <c>?</c> marker) and closes it.</summary>
    public async Task<Schema> DescribeParametersAsync(string sql, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var headers = Headers(options);
        var prepared = await PrepareCoreAsync(sql, headers, cancellationToken).ConfigureAwait(false);
        try { return Apache.Arrow.Flight.Sql.SchemaExtensions.DeserializeSchema(prepared.ParameterSchema.ToByteArray()); }
        finally { await CloseAsync(prepared.PreparedStatementHandle, headers).ConfigureAwait(false); }
    }

    private async Task<ByteString> PrepareAsync(string sql, Metadata headers, CancellationToken cancellationToken) =>
        (await PrepareCoreAsync(sql, headers, cancellationToken).ConfigureAwait(false)).PreparedStatementHandle;

    private async Task<ActionCreatePreparedStatementResult> PrepareCoreAsync(string sql, Metadata headers, CancellationToken cancellationToken)
    {
        var request = Any.Pack(new ActionCreatePreparedStatementRequest { Query = sql }).ToByteArray();
        using var call = _flight.DoAction(new FlightAction("CreatePreparedStatement", request), headers, null, cancellationToken);
        ActionCreatePreparedStatementResult? prepared = null;
        await foreach (var result in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            prepared = Any.Parser.ParseFrom(result.Body).Unpack<ActionCreatePreparedStatementResult>();
        return prepared ?? throw new InvalidOperationException("The server did not return a prepared statement handle.");
    }

    private async Task BindAsync(IMessage command, RecordBatch batch, Metadata headers, CancellationToken cancellationToken)
    {
        var descriptor = FlightDescriptor.CreateCommandDescriptor(Any.Pack(command).ToByteArray());
        using var call = await _flight.StartPut(descriptor, batch.Schema, headers, null, cancellationToken).ConfigureAwait(false);
        await call.RequestStream.WriteAsync(batch).ConfigureAwait(false);
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)) { /* the handle comes back unchanged */ }
    }

    private async Task CloseAsync(ByteString handle, Metadata headers)
    {
        try
        {
            var request = Any.Pack(new ActionClosePreparedStatementRequest { PreparedStatementHandle = handle }).ToByteArray();
            using var call = _flight.DoAction(new FlightAction("ClosePreparedStatement", request), headers);
            await foreach (var _ in call.ResponseStream.ReadAllAsync().ConfigureAwait(false)) { }
        }
        catch (RpcException)
        {
            // Closing is best effort; the server also expires idle statements.
        }
    }

    private async IAsyncEnumerable<RecordBatch> ReadThenCloseAsync(
        FlightInfo info, Metadata headers, ByteString handle, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        try
        {
            await foreach (var batch in ReadEndpointsAsync(info, headers, cancellationToken).ConfigureAwait(false))
                yield return batch;
        }
        finally
        {
            await CloseAsync(handle, headers).ConfigureAwait(false);
        }
    }

    /// <summary>Runs an INSERT/UPDATE/DDL statement and returns the affected row count.</summary>
    public async Task<long> ExecuteUpdateAsync(string sql, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var headers = Headers(options);
        var command = new CommandStatementUpdate { Query = sql };
        var descriptor = FlightDescriptor.CreateCommandDescriptor(Any.Pack(command).ToByteArray());

        using var call = await _flight.StartPut(descriptor, new Schema([], null), headers, null, cancellationToken).ConfigureAwait(false);
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);

        long affected = -1;
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
            affected = DoPutUpdateResult.Parser.ParseFrom(call.ResponseStream.Current.ApplicationMetadata).RecordCount;
        return affected;
    }

    private async IAsyncEnumerable<RecordBatch> ReadEndpointsAsync(
        FlightInfo info, Metadata headers, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var endpoint in info.Endpoints)
        {
            using var call = _flight.GetStream(endpoint.Ticket, headers, null, cancellationToken);
            FlightIpcDecompression.Enable(call.ResponseStream);
            while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                yield return call.ResponseStream.Current;
        }
    }

    private Metadata Headers(QueryOptions? options)
    {
        var md = new Metadata();
        if (_authorization is not null) md.Add("authorization", _authorization);
        if (options?.DataSource is { Length: > 0 } ds) md.Add("x-datasource", ds);
        if (options?.Compression is { Length: > 0 } c) md.Add("x-compression", c);
        return md;
    }

    public void Dispose() => _channel.Dispose();
}

/// <summary>
/// Apache.Arrow.Flight 23.0.0 reads result streams without a decompression codec factory, so batches using Arrow data compression
/// fail to decode. This installs the factory on the reader after the call is created. It relies on private members and
/// degrades to a clear error (not silent corruption) if a package update moves them.
/// </summary>
internal static class FlightIpcDecompression
{
    private static readonly ICompressionCodecFactory Factory = new CompressionCodecFactory();

    private static readonly FieldInfo? ImplementationField =
        typeof(FlightRecordBatchStreamReader).GetField("_arrowReaderImplementation", BindingFlags.NonPublic | BindingFlags.Instance);

    private static readonly FieldInfo? FactoryField =
        typeof(ArrowStreamReader).Assembly.GetType("Apache.Arrow.Ipc.ArrowReaderImplementation")
            ?.GetField("_compressionCodecFactory", BindingFlags.NonPublic | BindingFlags.Instance);

    public static bool IsSupported => ImplementationField is not null && FactoryField is not null;

    public static void Enable(FlightRecordBatchStreamReader reader)
    {
        if (!IsSupported)
            throw new NotSupportedException("This Apache.Arrow.Flight version does not allow enabling Arrow data decompression.");
        object implementation = ImplementationField!.GetValue(reader)
            ?? throw new InvalidOperationException("The Flight reader has no implementation to configure.");
        FactoryField!.SetValue(implementation, Factory);
    }
}
