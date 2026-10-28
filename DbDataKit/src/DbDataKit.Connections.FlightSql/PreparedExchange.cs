using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Flight.Sql.Client;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

namespace DbDataKit.Connections.FlightSql;

/// <summary>
/// A server-side prepared statement: its opaque handle, and the schema of its rows when the server said it.
/// <para>
/// The exchange is made from the protocol's own messages (the <c>CreatePreparedStatement</c> and <c>ClosePreparedStatement</c> actions, a DoPut of the parameters, a FlightInfo for the handle) instead of with the library's
/// <c>PreparedStatement</c>, which refuses a server that leaves the dataset or the parameter schema empty (the protocol allows it, and a proxy may well do it) and holds the handle as UTF-8 text.
/// </para>
/// </summary>
public sealed class PreparedHandle(FlightSqlClient client, ByteString handle, Schema? datasetSchema)
{
    public ByteString Handle { get; } = handle;

    public Schema? DatasetSchema { get; } = datasetSchema;

    public static async Task<PreparedHandle> CreateAsync(FlightSqlClient client, string sql, FlightCallOptions options, CancellationToken ct)
    {
        var action = new FlightAction("CreatePreparedStatement", Any.Pack(new Arrow.Flight.Protocol.Sql.ActionCreatePreparedStatementRequest { Query = sql }).ToByteString());
        Arrow.Flight.Protocol.Sql.ActionCreatePreparedStatementResult? created = null;
        await foreach (var result in client.DoActionAsync(action, options, ct).ConfigureAwait(false))
            created ??= Any.Parser.ParseFrom(result.Body).Unpack<Arrow.Flight.Protocol.Sql.ActionCreatePreparedStatementResult>();
        if (created is null || created.PreparedStatementHandle.IsEmpty) throw new InvalidOperationException("The Flight SQL server returned no prepared statement.");
        var schema = created.DatasetSchema.IsEmpty ? null : SchemaExtensions.DeserializeSchema(new ReadOnlyMemory<byte>(created.DatasetSchema.ToByteArray()));
        return new PreparedHandle(client, created.PreparedStatementHandle, schema);
    }

    /// <summary>Sends the parameters (a one-row batch, a column per marker) and asks for the statement's FlightInfo.</summary>
    public async Task<FlightInfo> BindAndExecuteAsync(RecordBatch parameters, FlightCallOptions options, CancellationToken ct)
    {
        var descriptor = FlightDescriptor.CreateCommandDescriptor(Any.Pack(new Arrow.Flight.Protocol.Sql.CommandPreparedStatementQuery { PreparedStatementHandle = Handle }).ToByteArray());
        await client.DoPutAsync(descriptor, parameters, options, ct).ConfigureAwait(false);
        return await client.GetFlightInfoAsync(descriptor, options, ct).ConfigureAwait(false);
    }

    /// <summary>Releases the statement on the server; one that cannot be released goes with the connection.</summary>
    public async Task CloseQuietlyAsync(FlightCallOptions options)
    {
        try
        {
            var action = new FlightAction("ClosePreparedStatement", Any.Pack(new Arrow.Flight.Protocol.Sql.ActionClosePreparedStatementRequest { PreparedStatementHandle = Handle }).ToByteString());
            await foreach (var _ in client.DoActionAsync(action, options, CancellationToken.None).ConfigureAwait(false)) { /* no result */ }
        }
        catch (Exception) { /* the statement is gone with the connection */ }
    }
}
