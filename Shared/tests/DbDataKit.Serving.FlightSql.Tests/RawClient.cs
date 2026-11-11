using System.Text;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Flight.Sql.Client;
using Grpc.Core;
using Grpc.Net.Client;

namespace DbDataKit.Serving.FlightSql.Tests;

/// <summary>A Flight SQL client that does what a driver does and nothing more, for the calls the shared ADO.NET client does not make (updates, catalog metadata, server info): the handshake, then every call with the token it issued.</summary>
public sealed class RawClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly string _token;

    public FlightSqlClient Client { get; }
    public string Token => _token;

    private RawClient(GrpcChannel channel, FlightSqlClient client, string token) => (_channel, Client, _token) = (channel, client, token);

    public static async Task<RawClient> LoginAsync(Uri address, string user, string password)
    {
        var channel = GrpcChannel.ForAddress(address);
        var flight = new FlightClient(channel);
        var basic = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{user}:{password}"));
        using var call = flight.Handshake(new Grpc.Core.Metadata { { "authorization", basic } });
        try
        {
            await call.RequestStream.WriteAsync(new FlightHandshakeRequest(Google.Protobuf.ByteString.Empty, 0));
            await call.RequestStream.CompleteAsync();
        }
        catch (Exception ex) when (ex is Grpc.Core.RpcException { StatusCode: Grpc.Core.StatusCode.Cancelled } or OperationCanceledException)
        {
            // credentials in the header are enough, so the server answers and ends the call without reading the request; a client that has not written yet finds the call finished (what matters is the response below)
        }
        var headers = await call.ResponseHeadersAsync;
        while (await call.ResponseStream.MoveNext(CancellationToken.None)) { }
        var token = headers.First(h => h.Key == "authorization").Value;
        return new RawClient(channel, new FlightSqlClient(flight), token);
    }

    public FlightCallOptions Options(string? dataSource = "main")
    {
        var options = new FlightCallOptions();
        options.Headers.Add("authorization", _token);
        if (dataSource is not null) options.Headers.Add("x-datasource", dataSource);
        options.Headers.Add("x-compression", "none");          // the Apache client cannot read compressed Arrow data
        return options;
    }

    public async Task<List<RecordBatch>> ReadAsync(IEnumerable<FlightInfo> infos, FlightCallOptions options)
    {
        var batches = new List<RecordBatch>();
        foreach (var info in infos)
            foreach (var endpoint in info.Endpoints)
                await foreach (var batch in Client.DoGetAsync(endpoint.Ticket, options).ConfigureAwait(false)) batches.Add(batch);
        return batches;
    }

    public Task<List<RecordBatch>> ReadAsync(FlightInfo info, FlightCallOptions options) => ReadAsync([info], options);

    /// <summary>The Flight SQL transaction argument of every statement call; there is none here.</summary>
    public static Transaction NoTransaction => default;

    // The metadata commands as most drivers send them: a field that is not asked for is absent. (The Apache .NET client sends empty strings for the ones it is not given, and an empty pattern matches nothing.)

    private async Task<List<RecordBatch>> RunAsync(Google.Protobuf.IMessage command, string? dataSource = "main")
    {
        var options = Options(dataSource);
        var descriptor = FlightDescriptor.CreateCommandDescriptor(Google.Protobuf.MessageExtensions.ToByteArray(Google.Protobuf.WellKnownTypes.Any.Pack(command)));
        return await ReadAsync(await Client.GetFlightInfoAsync(descriptor, options), options);
    }

    public Task<List<RecordBatch>> Catalogs() => RunAsync(new Arrow.Flight.Protocol.Sql.CommandGetCatalogs());

    public Task<List<RecordBatch>> Schemas(string? pattern = null, string? catalog = null)
    {
        var command = new Arrow.Flight.Protocol.Sql.CommandGetDbSchemas();
        if (pattern is not null) command.DbSchemaFilterPattern = pattern;
        if (catalog is not null) command.Catalog = catalog;
        return RunAsync(command);
    }

    public Task<List<RecordBatch>> Tables(string? schemaPattern = null, string? tablePattern = null, bool includeSchema = false, params string[] types)
    {
        var command = new Arrow.Flight.Protocol.Sql.CommandGetTables { IncludeSchema = includeSchema };
        if (schemaPattern is not null) command.DbSchemaFilterPattern = schemaPattern;
        if (tablePattern is not null) command.TableNameFilterPattern = tablePattern;
        command.TableTypes.AddRange(types);
        return RunAsync(command);
    }

    public Task<List<RecordBatch>> TableTypes() => RunAsync(new Arrow.Flight.Protocol.Sql.CommandGetTableTypes());

    public Task<List<RecordBatch>> PrimaryKeys(string table, string? schema = null, string? dataSource = "main")
    {
        var command = new Arrow.Flight.Protocol.Sql.CommandGetPrimaryKeys { Table = table };
        if (schema is not null) command.DbSchema = schema;
        return RunAsync(command, dataSource);
    }

    public Task<List<RecordBatch>> SqlInfo(params int[] ids)
    {
        var command = new Arrow.Flight.Protocol.Sql.CommandGetSqlInfo();
        command.Info.AddRange(ids.Select(i => (uint)i));
        return RunAsync(command);
    }

    /// <summary>A <c>CommandStatementUpdate</c> sent the way a driver sends it: a DoPut, answered with the number of rows affected.</summary>
    public async Task<long> UpdateAsync(string sql, string? dataSource = "main")
    {
        var command = new Arrow.Flight.Protocol.Sql.CommandStatementUpdate { Query = sql };
        var descriptor = FlightDescriptor.CreateCommandDescriptor(Google.Protobuf.MessageExtensions.ToByteArray(Google.Protobuf.WellKnownTypes.Any.Pack(command)));
        var empty = new RecordBatch(new Schema.Builder().Field(new Apache.Arrow.Field("n", Apache.Arrow.Types.Int32Type.Default, false)).Build(), [new Int32Array.Builder().Append(0).Build()], 1);
        var result = await Client.DoPutAsync(descriptor, empty, Options(dataSource));
        return Arrow.Flight.Protocol.Sql.DoPutUpdateResult.Parser.ParseFrom(result.ApplicationMetadata).RecordCount;
    }

    public void Dispose() => _channel.Dispose();
}
