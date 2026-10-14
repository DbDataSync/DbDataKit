using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Server;
using Arrow.Flight.Protocol.Sql;
using Grpc.Core;

namespace DbDataProxy.Server.Flight;

// Flight SQL features not offered by the proxy yet. Plain statement queries and updates are supported.
public sealed partial class ProxyFlightServer
{
    private static RpcException Unsupported(string feature) =>
        new(new Status(StatusCode.Unimplemented, $"{feature} is not supported by this proxy."));

    protected override Task DoGetCatalog(CommandGetCatalogs command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetCatalog));

    protected override Task DoGetCrossReference(CommandGetCrossReference command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetCrossReference));

    protected override Task DoGetDbSchema(CommandGetDbSchemas command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetDbSchema));

    protected override Task DoGetExportedKeys(CommandGetExportedKeys command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetExportedKeys));

    protected override Task DoGetImportedKeys(CommandGetImportedKeys command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetImportedKeys));

    protected override Task DoGetPreparedStatementQuery(CommandPreparedStatementQuery command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetPreparedStatementQuery));

    protected override Task DoGetPrimaryKeys(CommandGetPrimaryKeys command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetPrimaryKeys));

    protected override Task DoGetSqlInfo(CommandGetSqlInfo command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetSqlInfo));

    protected override Task DoGetTableType(CommandGetTableTypes command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetTableType));

    protected override Task DoGetTables(CommandGetTables command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetTables));

    protected override Task DoGetXbdcTypeInfo(CommandGetXdbcTypeInfo command, FlightServerRecordBatchStreamWriter writer, ServerCallContext context) =>
        throw Unsupported(nameof(DoGetXbdcTypeInfo));

    protected override Task<FlightInfo> GetCatalogFlightInfo(CommandGetCatalogs command, FlightDescriptor descriptor, ServerCallContext context) =>
        throw Unsupported(nameof(GetCatalogFlightInfo));

    protected override Task<FlightInfo> GetCrossReferenceFlightInfo(CommandGetCrossReference command, FlightDescriptor descriptor, ServerCallContext context) =>
        throw Unsupported(nameof(GetCrossReferenceFlightInfo));

    protected override Task<FlightInfo> GetDbSchemaFlightInfo(CommandGetDbSchemas command, FlightDescriptor descriptor, ServerCallContext context) =>
        throw Unsupported(nameof(GetDbSchemaFlightInfo));

    protected override Task<FlightInfo> GetExportedKeysFlightInfo(CommandGetExportedKeys command, FlightDescriptor descriptor, ServerCallContext context) =>
        throw Unsupported(nameof(GetExportedKeysFlightInfo));

    protected override Task<FlightInfo> GetImportedKeysFlightInfo(CommandGetImportedKeys command, FlightDescriptor descriptor, ServerCallContext context) =>
        throw Unsupported(nameof(GetImportedKeysFlightInfo));

    protected override Task<FlightInfo> GetPrimaryKeysFlightInfo(CommandGetPrimaryKeys command, FlightDescriptor descriptor, ServerCallContext context) =>
        throw Unsupported(nameof(GetPrimaryKeysFlightInfo));

    protected override Task<FlightInfo> GetSqlFlightInfo(CommandGetSqlInfo command, FlightDescriptor descriptor, ServerCallContext context) =>
        throw Unsupported(nameof(GetSqlFlightInfo));

    protected override Task<FlightInfo> GetTableTypesFlightInfo(CommandGetTableTypes command, FlightDescriptor descriptor, ServerCallContext context) =>
        throw Unsupported(nameof(GetTableTypesFlightInfo));

    protected override Task<FlightInfo> GetTablesFlightInfo(CommandGetTables command, FlightDescriptor descriptor, ServerCallContext context) =>
        throw Unsupported(nameof(GetTablesFlightInfo));

    protected override Task<FlightInfo> GetXdbcTypeFlightInfo(CommandGetXdbcTypeInfo command, FlightDescriptor descriptor, ServerCallContext context) =>
        throw Unsupported(nameof(GetXdbcTypeFlightInfo));

    protected override Task PutPreparedStatementQuery(CommandPreparedStatementQuery command, FlightServerRecordBatchStreamReader reader, IAsyncStreamWriter<FlightPutResult> writer, ServerCallContext context) =>
        throw Unsupported(nameof(PutPreparedStatementQuery));

}
