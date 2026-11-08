using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Flight.Sql.Client;
using DbDataGateway.Client;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Hosting;
using DuckDB.NET.Data;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataGateway.Tests.Support;

/// <summary>Runs the real gateway host in-process on a random loopback port, backed by a temporary DuckDB database.</summary>
public sealed class GatewayFixture : IAsyncLifetime
{
    /// <summary>The personal access token of alice, a service account of the identity home in the home group the data sources name (made when the host starts); her user name is <c>alice</c>.</summary>
    public string AliceToken { get; private set; } = "";

    private WebApplication? _app;
    private string _dir = "";

    public string Address { get; private set; } = "";
    public string DuckDbPath { get; private set; } = "";

    /// <summary>A directory the <c>duck_lake</c> source is allowed to read files from (it holds lake.csv), and a file next to it that is not allowed.</summary>
    /// <summary>The gateway's data directory (where it keeps its state database).</summary>
    public string DataDirectory => _dir;
    public string LakeDirectory { get; private set; } = "";
    public string PrivateCsv { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dbgateway-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        DuckDbPath = Path.Combine(_dir, "test.duckdb");
        SeedDuckDb(DuckDbPath);
        LakeDirectory = Path.Combine(_dir, "lake");
        Directory.CreateDirectory(LakeDirectory);
        File.WriteAllText(Path.Combine(LakeDirectory, "lake.csv"), "a,b\n1,2\n3,4\n");
        PrivateCsv = Path.Combine(_dir, "private.csv");
        File.WriteAllText(PrivateCsv, "secret\nhunter2\n");

        var settings = new Dictionary<string, string?>
        {
            ["Gateway:Listen:Address"] = "127.0.0.1",
            ["Gateway:Listen:Port"] = "0",
            ["Gateway:Tls:Mode"] = "None",
            ["Gateway:DataDirectory"] = _dir,
            ["Gateway:Compression:Mode"] = "None",
            // Explicit schema 1: these tests exercise its defaults (Classifier warn).
            ["Gateway:SchemaVersion"] = "1",
            ["Gateway:DataSources:duck:Provider"] = "duckdb",
            ["Gateway:DataSources:duck:ConnectionString"] = $"Data Source={DuckDbPath}",
            ["Gateway:DataSources:duck:AllowWrites"] = "true",
            ["Gateway:DataSources:duck_ro:Provider"] = "duckdb",
            ["Gateway:DataSources:duck_ro:ConnectionString"] = $"Data Source={DuckDbPath}",
            ["Gateway:DataSources:duck_strict:Provider"] = "duckdb",
            ["Gateway:DataSources:duck_strict:ConnectionString"] = $"Data Source={DuckDbPath}",
            ["Gateway:DataSources:duck_strict:Classifier"] = "strict",
            ["Gateway:DataSources:duck_off:Provider"] = "duckdb",
            ["Gateway:DataSources:duck_off:ConnectionString"] = $"Data Source={DuckDbPath}",
            ["Gateway:DataSources:duck_off:Classifier"] = "off",
            ["Gateway:DataSources:duck_lake:Provider"] = "duckdb",
            ["Gateway:DataSources:duck_lake:ConnectionString"] = $"Data Source={DuckDbPath}",
            ["Gateway:DataSources:duck_lake:Classifier"] = "off",
            ["Gateway:DataSources:duck_lake:AllowedDirectories:0"] = LakeDirectory,
            ["Gateway:DataSources:other:Provider"] = "duckdb",
            ["Gateway:DataSources:other:ConnectionString"] = "Data Source=:memory:",
        };
        HomeSeed.Open(settings, ["duck", "duck_ro", "duck_strict", "duck_off", "duck_lake"], defaultSource: "duck");

        _app = GatewayHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        AliceToken = HomeSeed.ServiceAccount(_app.Services.GetRequiredService<DbDataKit.State.StateDatabase>(), "alice");
        var feature = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!;
        Address = feature.Addresses.First().Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    public IServiceProvider Services => _app!.Services;

    private static void SeedDuckDb(string path)
    {
        using var c = new DuckDBConnection($"Data Source={path}");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE numbers AS
              SELECT i::INTEGER AS id, 'name-' || i::VARCHAR AS name, (i * 1.5)::DOUBLE AS score,
                     DATE '2024-01-01' + CAST(i % 365 AS INTEGER) AS day
              FROM range(0, 10000) t(i);
            CREATE TABLE prices(id INTEGER PRIMARY KEY, amount DECIMAL(12,3), note VARCHAR);
            INSERT INTO prices VALUES (1, 1.5, 'a');
            CREATE TABLE scaled AS SELECT i * 1.5 AS amount FROM range(0, 10) t(i);
            """;
        cmd.ExecuteNonQuery();
    }

    public GatewayClient Connect() => new(Address);
}

/// <summary>Test client: the gateway client library plus the stock .NET Flight SQL client for compatibility checks.</summary>
public sealed class GatewayClient : IDisposable
{
    private readonly GatewayFlightClient _gateway;
    private readonly GrpcChannel _stockChannel;
    private readonly FlightSqlClient _stock;

    public GatewayClient(string address)
    {
        Counter = new ByteCountingHandler(new SocketsHttpHandler());
        _gateway = new GatewayFlightClient(address, new GrpcChannelOptions { HttpHandler = Counter });
        _stockChannel = GrpcChannel.ForAddress(address);
        _stock = new FlightSqlClient(new FlightClient(_stockChannel));
    }

    /// <summary>Counts response bytes as they cross the wire (after gRPC compression, if any).</summary>
    public ByteCountingHandler Counter { get; }

    public string? Bearer => _gateway.Authorization;

    public GatewayFlightClient Raw => _gateway;

    public Task AuthenticateAsync(string user, string password) => _gateway.AuthenticateAsync(user, password);

    public void UseHeader(string header) => _gateway.Authorization = header;

    public async Task<(Schema Schema, List<RecordBatch> Batches)> QueryAsync(string sql, string? dataSource = null, string? compression = null)
    {
        var result = await _gateway.QueryAsync(sql, new QueryOptions { DataSource = dataSource, Compression = compression });
        var batches = new List<RecordBatch>();
        await foreach (var b in result) batches.Add(b);
        return (result.Schema, batches);
    }

    /// <summary>Update as non-.NET Flight SQL clients send it (CommandStatementUpdate, no prepared statement).</summary>
    public Task<long> RawUpdateAsync(string sql, string? dataSource = null) =>
        _gateway.ExecuteUpdateAsync(sql, new QueryOptions { DataSource = dataSource });

    /// <summary>Update through the stock .NET Flight SQL client, which prepares every statement.</summary>
    public Task<long> UpdateAsync(string sql, string? dataSource = null)
    {
        var md = new Metadata();
        if (_gateway.Authorization is not null) md.Add("authorization", _gateway.Authorization);
        if (dataSource is not null) md.Add("x-datasource", dataSource);
        return _stock.ExecuteUpdateAsync(sql, default, new Apache.Arrow.Flight.Sql.FlightCallOptions { Headers = md });
    }

    public void Dispose()
    {
        _gateway.Dispose();
        _stockChannel.Dispose();
    }
}

public static class Rpc
{
    /// <summary>Runs the action and returns the gRPC failure it raised, looking through client wrapper exceptions.</summary>
    public static async Task<RpcException> ExpectAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            for (var e = ex; e is not null; e = e.InnerException)
                if (e is RpcException rpc) return rpc;
            throw;
        }
        throw new Xunit.Sdk.XunitException("Expected an RpcException but the call succeeded.");
    }
}

public sealed class ByteCountingHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    private long _bytes;
    public long ResponseBytes => Interlocked.Read(ref _bytes);
    public void Reset() => Interlocked.Exchange(ref _bytes, 0);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken);
        var original = response.Content;
        var counting = new CountingContent(original, n => Interlocked.Add(ref _bytes, n));
        foreach (var header in original.Headers) counting.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = counting;
        return response;
    }

    private sealed class CountingContent(HttpContent inner, Action<long> onBytes) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await using var source = await inner.ReadAsStreamAsync();
            await source.CopyToAsync(new CountingStream(stream, onBytes));
        }

        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new CountingStream(await inner.ReadAsStreamAsync(), onBytes);

        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }

    private sealed class CountingStream(Stream inner, Action<long> onBytes) : Stream
    {
        public override int Read(byte[] buffer, int offset, int count) { int n = inner.Read(buffer, offset, count); onBytes(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { int n = await inner.ReadAsync(buffer, ct); onBytes(n); return n; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) => ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) { onBytes(buffer.Length); await inner.WriteAsync(buffer, ct); }
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}

/// <summary>Reads a GetSqlInfo result (a dense union per row) into id -> value.</summary>
public static class SqlInfoReader
{
    public static async Task<Dictionary<uint, object>> ReadAsync(DbDataGateway.Client.QueryResultStream stream)
    {
        var values = new Dictionary<uint, object>();
        await foreach (var batch in stream)
        {
            var names = (UInt32Array)batch.Column("info_name");
            var union = (DenseUnionArray)batch.Column("value");
            for (int i = 0; i < batch.Length; i++)
            {
                var child = union.Fields[union.TypeIds[i]];
                int offset = union.ValueOffsets[i];
                values[names.GetValue(i)!.Value] = child switch
                {
                    StringArray a => a.GetString(offset),
                    BooleanArray a => a.GetValue(offset)!.Value,
                    Int64Array a => a.GetValue(offset)!.Value,
                    _ => throw new NotSupportedException(child.GetType().Name),
                };
            }
        }
        return values;
    }
}
