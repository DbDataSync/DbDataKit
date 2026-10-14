using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Flight.Sql.Client;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Hosting;
using DuckDB.NET.Data;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
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

/// <summary>A minimal Flight SQL client that performs the Basic-auth handshake the way standard Flight clients do.</summary>
public sealed class ProxyClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly FlightClient _flight;
    private readonly FlightSqlClient _sql;
    private string? _bearer;

    public ProxyClient(string address)
    {
        _channel = GrpcChannel.ForAddress(address);
        _flight = new FlightClient(_channel);
        _sql = new FlightSqlClient(_flight);
    }

    public string? Bearer => _bearer;

    public async Task AuthenticateAsync(string user, string password)
    {
        string basic = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes($"{user}:{password}"));
        using var call = _flight.Handshake(new Metadata { { "authorization", "Basic " + basic } });
        await call.RequestStream.WriteAsync(new FlightHandshakeRequest(ByteString.Empty, 0));
        await call.RequestStream.CompleteAsync();
        var headers = await call.ResponseHeadersAsync;
        _bearer = headers.GetValue("authorization");
        while (await call.ResponseStream.MoveNext(default)) { }
    }

    public void UseHeader(string header) => _bearer = header;

    private Apache.Arrow.Flight.Sql.FlightCallOptions Options(string? dataSource, string? compression)
    {
        var md = new Metadata();
        if (_bearer is not null) md.Add("authorization", _bearer);
        if (dataSource is not null) md.Add("x-datasource", dataSource);
        if (compression is not null) md.Add("x-compression", compression);
        return new Apache.Arrow.Flight.Sql.FlightCallOptions { Headers = md };
    }

    public async Task<(Schema Schema, List<RecordBatch> Batches)> QueryAsync(string sql, string? dataSource = null, string? compression = null)
    {
        var options = Options(dataSource, compression);
        var info = await _sql.ExecuteAsync(sql, default, options);
        var batches = new List<RecordBatch>();
        foreach (var endpoint in info.Endpoints)
            await foreach (var b in _sql.DoGetAsync(endpoint.Ticket, options)) batches.Add(b);
        return (info.Schema, batches);
    }

    public Task<long> UpdateAsync(string sql, string? dataSource = null) =>
        _sql.ExecuteUpdateAsync(sql, default, Options(dataSource, null));

    /// <summary>Sends a CommandStatementUpdate the way non-.NET Flight SQL clients do (no prepared statement).</summary>
    public async Task<long> RawUpdateAsync(string sql, string? dataSource = null)
    {
        var md = new Metadata();
        if (_bearer is not null) md.Add("authorization", _bearer);
        if (dataSource is not null) md.Add("x-datasource", dataSource);
        var command = new Arrow.Flight.Protocol.Sql.CommandStatementUpdate { Query = sql };
        var descriptor = FlightDescriptor.CreateCommandDescriptor(Google.Protobuf.WellKnownTypes.Any.Pack(command).ToByteArray());
        using var call = await _flight.StartPut(descriptor, new Schema([], null), md);
        await call.RequestStream.CompleteAsync();
        long count = -1;
        while (await call.ResponseStream.MoveNext(default))
            count = Arrow.Flight.Protocol.Sql.DoPutUpdateResult.Parser.ParseFrom(call.ResponseStream.Current.ApplicationMetadata).RecordCount;
        return count;
    }

    public void Dispose() => _channel.Dispose();
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
