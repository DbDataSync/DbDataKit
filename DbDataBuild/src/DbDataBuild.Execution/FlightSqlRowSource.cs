using ServerFacts = DbDataKit.Metadata.ServerFacts;
using Apache.Arrow;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Flight.Sql.Client;
using DbDataBuild.Models;
using DbDataBuild.State;
using Google.Protobuf;
using Grpc.Core;
using DbDataKit.Connections.FlightSql;

namespace DbDataBuild.Execution;

/// <summary>A table (or view) a Flight SQL server's metadata calls described: the schema name it is in, its name, whether it is a view, and its columns from the Arrow schema the server sent.</summary>
internal sealed record MetadataTable(string SchemaName, string Name, bool IsView, IReadOnlyList<ColumnShape> Columns);

/// <summary>A foreign key as a Flight SQL server's imported-keys call describes it: the key's name, its columns and what they reference.</summary>
internal sealed record MetadataForeignKey(string Table, string Name, IReadOnlyList<string> Columns, string ReferencedSchema, string ReferencedTable, IReadOnlyList<string> ReferencedColumns);

/// <summary>An index as a catalog describes it: its table, name, whether it is unique, its key columns in order (`name desc` for a descending one) and its included columns.</summary>
internal sealed record MetadataIndex(string Table, string Name, bool Unique, IReadOnlyList<string> Columns, IReadOnlyList<string> Include);

/// <summary>
/// What a source that has a catalog of its own, apart from SQL, can say about its tables: Flight SQL's metadata calls (`CommandGetDbSchemas`, `CommandGetTables`, `CommandGetPrimaryKeys`, `CommandGetImportedKeys`) and
/// ODBC's catalog functions (`catalog: odbc`). Every answer is null when the source does not offer that call; the caller then falls back to the dialect's `information_schema` text, or says plainly that there is no catalog to read.
/// </summary>
internal interface IMetadataCatalog
{
    /// <summary>The indexes of the tables of a schema, per table; null where the source has no way to say (Flight SQL has no metadata call for indexes).</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataIndex>>?> IndexesAsync(string schema, CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<MetadataIndex>>?>(null);

    Task<IReadOnlyList<string>?> SchemasAsync(CancellationToken ct);
    Task<IReadOnlyList<MetadataTable>?> TablesAsync(string schema, string? table, CancellationToken ct);
    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> PrimaryKeysAsync(string schema, CancellationToken ct);
    Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataForeignKey>>?> ForeignKeysAsync(string schema, CancellationToken ct);
}

/// <summary>What a source can say about the system it reaches without a query of ours; today only a Flight SQL server's `GetSqlInfo`. Null when the source does not offer the call.</summary>
internal interface IServerFacts
{
    Task<ServerFacts?> ServerFactsAsync(CancellationToken ct);
}

/// <summary>
/// An Arrow Flight SQL server as a row source (Apache.Arrow.Flight.Sql over Grpc.Net.Client). The login is a URI with options (<see cref="FlightSqlLogin"/>). A statement without parameters is sent as a plain query; one with
/// parameters is **prepared** and its parameters are bound as a one-row Arrow batch in the order the plan gives (`?`, positional). The server answers with endpoints; the rows are read from each in turn on this same
/// connection as record batches and handed over value by value, by <see cref="ArrowValueReader.Value"/>. A call's time limit is the gRPC deadline. Like ADO.NET's drivers it is only ever reached through <see cref="ReadSession"/>.
/// </summary>
internal sealed class FlightSqlRowSource : IRowSource, IMetadataCatalog, IServerFacts
{
    private readonly FlightSqlLogin login;
    private readonly FlightSqlConnection connection;

    internal FlightSqlClient Client => connection.Client;

    internal FlightSqlConnection Connection => connection;

    private FlightSqlRowSource(FlightSqlLogin login, FlightSqlConnection connection)
    {
        this.login = login; this.connection = connection;
    }

    /// <summary>`Flight SQL server proxy.example.com:31337 (TLS)`: where the source goes, never how it signs in.</summary>
    public string? ServerDescription => $"Flight SQL server {login.HostAndPort}{(login.Tls ? " (TLS)" : " (no TLS)")}";

    /// <summary>The seconds a call may take when the caller names none (the other drivers' default).</summary>
    internal const int DefaultTimeoutSeconds = 30;

    /// <summary>
    /// Opens the shared Flight SQL connection and signs in: a bearer token is sent with each call; a user and password are traded for one by the server's handshake. Nothing is sent for a token (the first call is the first
    /// contact), so "the server cannot be reached" and "the token is refused" show at the first statement, which for a command is its check of the origin. TLS is the machine's trust, the certificate authority of `ca_file`
    /// instead of it, or no check at all (`tls_skip_verify`).
    /// </summary>
    public static async Task<FlightSqlRowSource> ConnectAsync(string loginValue, CancellationToken ct)
    {
        var login = FlightSqlLogin.Parse(loginValue);
        var token = await login.ResolveTokenAsync(ct);
        var timeout = TimeSpan.FromSeconds(DefaultTimeoutSeconds);
        var settings = new FlightSqlSettings(
            login.Address, token != null ? FlightSqlAuthMethod.Bearer : login.User != null ? FlightSqlAuthMethod.Basic : FlightSqlAuthMethod.None,
            login.User, token ?? login.Password, login.TlsSkipVerify, Catalog: null, ConnectTimeout: timeout, CommandTimeout: timeout, FlightSqlParameterMode.Prepared,
            CaFile: login.Tls && !login.TlsSkipVerify ? login.CaFile : null, Headers: login.Headers.Select(h => new KeyValuePair<string, string>(h.Name, h.Value)).ToList());
        var connection = new FlightSqlConnection(settings, value => throw new NotSupportedException("A copy's origin binds its values; it never writes them into the text."));
        try
        {
            try { await connection.OpenAsync(ct); }
            catch (InvalidOperationException ex) when (ex.Message.StartsWith("The CA file", StringComparison.Ordinal)) { throw new FlightSqlLoginException("The ca_file of the Flight SQL login could not be read as a PEM file of certificates."); }
            return new FlightSqlRowSource(login, connection);
        }
        catch { connection.Dispose(); throw; }
    }

    private FlightCallOptions Options(int? timeoutSeconds)
    {
        var seconds = timeoutSeconds ?? DefaultTimeoutSeconds;
        return connection.CallOptions(seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);
    }

    public async Task<IRowReader> ExecuteAsync(string sql, IReadOnlyList<GateParameter> parameters, int? timeoutSeconds, bool streaming, CancellationToken ct)
    {
        var options = Options(timeoutSeconds);
        PreparedHandle? prepared = null;
        try
        {
            FlightInfo info;
            if (parameters.Count == 0) info = await Client.ExecuteAsync(sql, Transaction.NoTransaction, options, ct);
            else
            {
                prepared = await PreparedHandle.CreateAsync(Client, sql, options, ct);
                info = await prepared.BindAndExecuteAsync(ArrowBinding.ParameterBatch(parameters), options, ct);
            }
            return await FlightSqlRowReader.StartAsync(this, info, options, prepared, ct);
        }
        catch
        {
            if (prepared != null) await prepared.CloseQuietlyAsync(options);
            throw;
        }
    }

    public Task<IRowReader> ExecuteCommandAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct) =>
        throw new NotSupportedException("A Flight SQL connection runs queries only: a native command is for SQL Server and PostgreSQL connections.");

    /// <summary>The dialect of the connection, for the one bounded read this source makes (<see cref="DescribeAsync"/>); the login sets it.</summary>
    internal DialectProfile Dialect { get; set; } = DialectProfiles.Ansi;

    public async Task<IReadOnlyList<ColumnShape>?> DescribeAsync(string sql, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
    {
        if (await DescribeByProtocolAsync(sql, ct) is { } described) return described;
        // a server that will not say the schema of a statement without running it: a read bounded to no rows, in the dialect's `limit` style, gives the schema of its (empty) result
        try
        {
            await using var reader = (FlightSqlRowReader)await ExecuteAsync(Dialect.Bounded(sql, 0), parameters, null, false, ct);
            return reader.ResultSchema.FieldsList.Count == 0 ? null : ArrowBinding.Columns(reader.ResultSchema);
        }
        catch (Exception ex) when (FlightSqlFailure.Rpc(ex) != null) { return null; }
    }

    private async Task<IReadOnlyList<ColumnShape>?> DescribeByProtocolAsync(string sql, CancellationToken ct)
    {
        var options = Options(null);
        PreparedHandle? prepared = null;
        try
        {
            Schema? schema = null;
            try
            {
                prepared = await PreparedHandle.CreateAsync(Client, sql, options, ct);
                schema = prepared.DatasetSchema;
            }
            catch (Exception ex) when (FlightSqlFailure.Rpc(ex) != null) { /* a server without prepared statements may still say the schema of a query */ }
            if (schema == null || schema.FieldsList.Count == 0)
            {
                // the library's GetExecuteSchemaAsync prepares a statement it then leaves open when the server gives no schema: the GetSchema call is made here with the statement as a command
                try { schema = await Client.GetSchemaAsync(FlightDescriptor.CreateCommandDescriptor(Google.Protobuf.WellKnownTypes.Any.Pack(new Arrow.Flight.Protocol.Sql.CommandStatementQuery { Query = sql }).ToByteArray()), options, ct); }
                catch (Exception ex) when (FlightSqlFailure.Rpc(ex) != null) { return null; }
            }
            return schema == null || schema.FieldsList.Count == 0 ? null : ArrowBinding.Columns(schema);
        }
        finally { if (prepared != null) await prepared.CloseQuietlyAsync(options); }
    }

    // ---- the metadata calls ----

    // The catalog calls are the shared reader's; a server that rejects a metadata command it does not know answers InvalidArgument, which here, as Unimplemented, means "not offered".
    private FlightSqlMetadataReader Catalog => catalogReader ??= new(connection, invalidArgumentIsNotOffered: true);
    private FlightSqlMetadataReader? catalogReader;

    private static TimeSpan MetadataTimeout => TimeSpan.FromSeconds(DefaultTimeoutSeconds);

    public async Task<IReadOnlyList<string>?> SchemasAsync(CancellationToken ct) =>
        await Catalog.SchemasAsync(null, MetadataTimeout, ct) is { } names ? new SortedSet<string>(names.Where(n => n.Length > 0), StringComparer.Ordinal).ToList() : null;

    public async Task<IReadOnlyList<MetadataTable>?> TablesAsync(string schema, string? table, CancellationToken ct)
    {
        if (await Catalog.TablesAsync(null, schema, table, includeSchema: true, MetadataTimeout, ct) is not { } rows) return null;
        var found = new List<MetadataTable>();
        foreach (var row in rows)
        {
            // the pattern is a LIKE pattern (`_` matches any character): the name is compared exactly here
            if (row.Schema != schema || row.Name is not { } name || (table != null && name != table)) continue;
            if (row.Type.Contains("SYSTEM", StringComparison.OrdinalIgnoreCase) || row.Type.Contains("TEMPORARY", StringComparison.OrdinalIgnoreCase)) continue;
            if (row.SchemaBytes is not { Length: > 0 }) continue;     // no columns told: it cannot be described
            found.Add(new(schema, name, row.Type.Contains("VIEW", StringComparison.OrdinalIgnoreCase), ArrowBinding.Columns(row.ReadSchema()!)));
        }
        return found;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>?> PrimaryKeysAsync(string schema, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        if (await TablesAsync(schema, null, ct) is not { } tables) return null;                                  // no metadata calls at all
        foreach (var table in tables)
        {
            if (table.IsView) continue;
            if (await Catalog.PrimaryKeysAsync(null, schema, table.Name, MetadataTimeout, ct) is not { } keys) return null;
            var columns = keys.Select((k, i) => (Sequence: k.Sequence ?? i + 1, k.Column)).ToList();
            if (columns.Count > 0) result[table.Name] = columns.OrderBy(c => c.Sequence).Select(c => c.Column).ToList();
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<MetadataForeignKey>>?> ForeignKeysAsync(string schema, CancellationToken ct)
    {
        var result = new Dictionary<string, IReadOnlyList<MetadataForeignKey>>(StringComparer.Ordinal);
        if (await TablesAsync(schema, null, ct) is not { } tables) return null;                                  // no metadata calls at all
        foreach (var table in tables)
        {
            if (table.IsView) continue;
            if (await Catalog.ImportedKeysAsync(null, schema, table.Name, MetadataTimeout, ct) is not { } imported) return null;
            var rows = imported.Select((k, i) => (Key: k.KeyName ?? $"fk_{table.Name}_{k.ReferencedTable}", Sequence: k.Sequence ?? i + 1, k.Column,
                RefSchema: k.ReferencedSchema ?? schema, RefTable: k.ReferencedTable, RefColumn: k.ReferencedColumn)).ToList();
            if (rows.Count == 0) continue;
            result[table.Name] = rows.GroupBy(x => x.Key, StringComparer.Ordinal).Select(g =>
            {
                var ordered = g.OrderBy(x => x.Sequence).ToList();
                return new MetadataForeignKey(table.Name, g.Key, ordered.Select(x => x.Column).ToList(), ordered[0].RefSchema, ordered[0].RefTable, ordered.Select(x => x.RefColumn).ToList());
            }).ToList();
        }
        return result;
    }

    // The ids of the standard SqlInfo the tool reads (the Flight SQL protocol): the server's name, version and read-only flag, and how identifiers are cased and quoted.
    private const uint NameId = 0, VersionId = 1, ReadOnlyId = 3, IdentifierCaseId = 503, IdentifierQuoteId = 504, QuotedIdentifierCaseId = 505;

    /// <summary>
    /// `GetSqlInfo` for the ids above: what the server says it is. A proxy answers per data source (the login's `x-datasource` header). Null when the server does not implement the call; a value of a type other than the
    /// protocol's for an id is left out rather than guessed at.
    /// </summary>
    public async Task<ServerFacts?> ServerFactsAsync(CancellationToken ct)
    {
        if (await Catalog.SqlInfoAsync([NameId, VersionId, ReadOnlyId, IdentifierCaseId, IdentifierQuoteId, QuotedIdentifierCaseId], MetadataTimeout, ct) is not { } info) return null;
        string? Text(uint id) => info.TryGetValue(id, out var v) ? v as string : null;
        long? Number(uint id) => info.TryGetValue(id, out var v) ? v switch { long l => l, int i => i, _ => null } : null;
        bool? flag = info.TryGetValue(ReadOnlyId, out var ro) ? ro as bool? : null;
        return FlightServerFacts.Of(Text(NameId), Text(VersionId), Text(IdentifierQuoteId),
            Number(IdentifierCaseId) is { } c1 ? FlightServerFacts.CaseName(c1) : null, Number(QuotedIdentifierCaseId) is { } c2 ? FlightServerFacts.CaseName(c2) : null, flag);
    }

    public ValueTask DisposeAsync()
    {
        connection.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>The rows of one Flight SQL statement: the record batches of each endpoint in turn, a row at a time. The first batch is read when the statement starts, so a failure shows at once and the names are known.</summary>
internal sealed class FlightSqlRowReader : IRowReader
{
    private readonly FlightSqlRowSource source;
    private readonly FlightCallOptions options;
    private readonly PreparedHandle? prepared;
    private readonly CancellationTokenSource cancellation;
    private readonly IReadOnlyList<FlightEndpoint> endpoints;
    private int nextEndpoint;
    private IAsyncEnumerator<RecordBatch>? batches;
    private RecordBatch? current;
    private int row = -1;
    private Schema schema;

    private FlightSqlRowReader(FlightSqlRowSource source, FlightCallOptions options, PreparedHandle? prepared, CancellationTokenSource cancellation, IReadOnlyList<FlightEndpoint> endpoints, Schema schema)
    {
        this.source = source; this.options = options; this.prepared = prepared; this.cancellation = cancellation; this.endpoints = endpoints; this.schema = schema;
    }

    public static async Task<FlightSqlRowReader> StartAsync(FlightSqlRowSource source, FlightInfo info, FlightCallOptions options, PreparedHandle? prepared, CancellationToken ct)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = new FlightSqlRowReader(source, options, prepared, cancellation, info.Endpoints, info.Schema ?? new Schema(new List<Field>(), null));
        try { await reader.AdvanceBatchAsync(); }
        catch { await reader.DisposeAsync(); throw; }
        return reader;
    }

    public IReadOnlyList<string> Names => schema.FieldsList.Select(f => f.Name).ToList();

    /// <summary>The Arrow schema of the result as the server (or the first batch) said it.</summary>
    internal Schema ResultSchema => schema;

    // the next batch with rows, across endpoints; false at the end of the last
    private async Task<bool> AdvanceBatchAsync()
    {
        while (true)
        {
            if (batches == null)
            {
                if (nextEndpoint >= endpoints.Count) return false;
                batches = source.Connection.DoGetAsync(endpoints[nextEndpoint++].Ticket, options, cancellation.Token).GetAsyncEnumerator(cancellation.Token);
            }
            if (!await batches.MoveNextAsync())
            {
                await batches.DisposeAsync();
                batches = null;
                continue;
            }
            current?.Dispose();
            current = batches.Current;
            if (current.Schema.FieldsList.Count > 0) schema = current.Schema;
            row = -1;
            if (current.Length > 0) return true;
        }
    }

    public async Task<bool> ReadAsync(CancellationToken ct)
    {
        if (current == null) return false;
        if (++row < current.Length) return true;
        if (await AdvanceBatchAsync()) { row = 0; return true; }
        current = null;
        return false;
    }

    public ValueTask<object?> ValueAsync(int column, CancellationToken ct)
    {
        try { return ValueTask.FromResult(ArrowValueReader.Value(current!.Column(column), row)); }
        catch (ArrowTypeNotCarriedException ex) { throw new TransferException($"column `{schema.FieldsList[column].Name}`: {ex.Message}."); }
    }

    public void Cancel() { try { cancellation.Cancel(); } catch (Exception) { /* it ended */ } }

    public async ValueTask DisposeAsync()
    {
        // a statement that still has rows is cancelled instead of drained
        if (batches != null) Cancel();
        try { if (batches != null) await batches.DisposeAsync(); }
        catch (Exception ex) when (ex is OperationCanceledException or RpcException) { /* the cancelled rest */ }
        finally
        {
            current?.Dispose();
            cancellation.Dispose();
            if (prepared != null) await prepared.CloseQuietlyAsync(options);
        }
    }
}
