using System.Reflection;
using System.Text;
using Apache.Arrow;
using Apache.Arrow.Compression;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Ipc;
using Arrow.Flight.Protocol.Sql;
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
/// unlike the stock .NET Flight client, can read Arrow IPC-compressed (zstd/lz4) result streams.
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
        await call.RequestStream.WriteAsync(new FlightHandshakeRequest(ByteString.Empty, 0), cancellationToken).ConfigureAwait(false);
        await call.RequestStream.CompleteAsync().ConfigureAwait(false);

        var headers = await call.ResponseHeadersAsync.ConfigureAwait(false);
        while (await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false)) { /* payload carries the token too */ }
        _authorization = headers.GetValue("authorization")
            ?? throw new InvalidOperationException("The server did not return an authorization token.");
    }

    /// <summary>Runs a query and returns its schema and a lazily-read stream of batches.</summary>
    public async Task<QueryResultStream> QueryAsync(string sql, QueryOptions? options = null, CancellationToken cancellationToken = default)
    {
        var headers = Headers(options);
        var command = new CommandStatementQuery { Query = sql };
        var descriptor = FlightDescriptor.CreateCommandDescriptor(Any.Pack(command).ToByteArray());

        FlightInfo info;
        using (var infoCall = _flight.GetInfo(descriptor, headers, null, cancellationToken))
            info = await infoCall.ResponseAsync.ConfigureAwait(false);

        return new QueryResultStream(info.Schema, ReadEndpointsAsync(info, headers));
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
/// Apache.Arrow.Flight 23.0.0 reads result streams without a decompression codec factory, so IPC-compressed batches
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
            throw new NotSupportedException("This Apache.Arrow.Flight version does not allow enabling IPC decompression.");
        object implementation = ImplementationField!.GetValue(reader)
            ?? throw new InvalidOperationException("The Flight reader has no implementation to configure.");
        FactoryField!.SetValue(implementation, Factory);
    }
}
