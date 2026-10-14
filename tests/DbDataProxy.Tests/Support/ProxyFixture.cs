using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Flight.Sql.Client;
using DbDataProxy.Client;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Hosting;
using DuckDB.NET.Data;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using System.Net;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataProxy.Tests.Support;

/// <summary>Runs the real proxy host in-process on a random loopback port, backed by a temporary DuckDB database.</summary>
public sealed class ProxyFixture : IAsyncLifetime
{
    public const string AlicePassword = "alice-pw";

    private WebApplication? _app;
    private string _dir = "";

    public string Address { get; private set; } = "";
    public string DuckDbPath { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dbproxy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        DuckDbPath = Path.Combine(_dir, "test.duckdb");
        SeedDuckDb(DuckDbPath);

        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Compression:Mode"] = "None",
            ["Proxy:DataSources:duck:Provider"] = "duckdb",
            ["Proxy:DataSources:duck:ConnectionString"] = $"Data Source={DuckDbPath}",
            ["Proxy:DataSources:duck:AllowWrites"] = "true",
            ["Proxy:DataSources:duck_ro:Provider"] = "duckdb",
            ["Proxy:DataSources:duck_ro:ConnectionString"] = $"Data Source={DuckDbPath}",
            ["Proxy:DataSources:other:Provider"] = "duckdb",
            ["Proxy:DataSources:other:ConnectionString"] = "Data Source=:memory:",
            ["Proxy:Users:0:Name"] = "alice",
            ["Proxy:Users:0:PasswordHash"] = PasswordHasher.Hash(AlicePassword, 1000),
            ["Proxy:Users:0:DataSources:0"] = "duck",
            ["Proxy:Users:0:DataSources:1"] = "duck_ro",
            ["Proxy:Users:0:DefaultDataSource"] = "duck",
        };

        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
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
            """;
        cmd.ExecuteNonQuery();
    }

    public ProxyClient Connect() => new(Address);
}

/// <summary>Test client: the proxy client library plus the stock .NET Flight SQL client for compatibility checks.</summary>
public sealed class ProxyClient : IDisposable
{
    private readonly ProxyFlightClient _proxy;
    private readonly GrpcChannel _stockChannel;
    private readonly FlightSqlClient _stock;

    public ProxyClient(string address)
    {
        Counter = new ByteCountingHandler(new SocketsHttpHandler());
        _proxy = new ProxyFlightClient(address, new GrpcChannelOptions { HttpHandler = Counter });
        _stockChannel = GrpcChannel.ForAddress(address);
        _stock = new FlightSqlClient(new FlightClient(_stockChannel));
    }

    /// <summary>Counts response bytes as they cross the wire (after gRPC compression, if any).</summary>
    public ByteCountingHandler Counter { get; }

    public string? Bearer => _proxy.Authorization;

    public ProxyFlightClient Raw => _proxy;

    public Task AuthenticateAsync(string user, string password) => _proxy.AuthenticateAsync(user, password);

    public void UseHeader(string header) => _proxy.Authorization = header;

    public async Task<(Schema Schema, List<RecordBatch> Batches)> QueryAsync(string sql, string? dataSource = null, string? compression = null)
    {
        var result = await _proxy.QueryAsync(sql, new QueryOptions { DataSource = dataSource, Compression = compression });
        var batches = new List<RecordBatch>();
        await foreach (var b in result) batches.Add(b);
        return (result.Schema, batches);
    }

    /// <summary>Update as non-.NET Flight SQL clients send it (CommandStatementUpdate, no prepared statement).</summary>
    public Task<long> RawUpdateAsync(string sql, string? dataSource = null) =>
        _proxy.ExecuteUpdateAsync(sql, new QueryOptions { DataSource = dataSource });

    /// <summary>Update through the stock .NET Flight SQL client, which prepares every statement.</summary>
    public Task<long> UpdateAsync(string sql, string? dataSource = null)
    {
        var md = new Metadata();
        if (_proxy.Authorization is not null) md.Add("authorization", _proxy.Authorization);
        if (dataSource is not null) md.Add("x-datasource", dataSource);
        return _stock.ExecuteUpdateAsync(sql, default, new Apache.Arrow.Flight.Sql.FlightCallOptions { Headers = md });
    }

    public void Dispose()
    {
        _proxy.Dispose();
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
