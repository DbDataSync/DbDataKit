using Apache.Arrow;
using Apache.Arrow.Flight.Sql;
using Arrow.Flight.Protocol.Sql;
using Apache.Arrow.Flight.Sql.Client;
using DbDataKit.Serving.Tests;
using Grpc.Core;

namespace DbDataKit.Serving.FlightSql.Tests;

/// <summary>The catalog calls, prepared statements, updates and server info, with a client that does only what a driver does.</summary>
public class MetadataTests
{
    private static async Task<(ModuleHost Host, RawClient Client)> StartAsync(string user = "admin", IServingServices? services = null, FlightSqlOptions? options = null)
    {
        var host = await ModuleHost.StartAsync(services, options);
        return (host, await RawClient.LoginAsync(host.Address, user, $"{user}-pw"));
    }

    private static IEnumerable<string?> Column(IEnumerable<RecordBatch> batches, int index) =>
        batches.SelectMany(b => Enumerable.Range(0, b.Length).Select(i => ((StringArray)b.Column(index)).GetString(i)));

    [Fact]
    public async Task Catalogs_schemas_tables_and_table_types_are_what_the_caller_may_see()
    {
        var (host, client) = await StartAsync("admin");
        await using var _ = host; using var __ = client;
        Assert.Empty(Column(await client.Catalogs(), 0));      // the fake host has no catalogs
        Assert.Equal(["public", "secret"], Column(await client.Schemas(), 1));
        Assert.Equal(["public"], Column(await client.Schemas("pub%"), 1));
        Assert.Equal(["TABLE", "VIEW"], Column(await client.TableTypes(), 0));

        var tables = await client.Tables();
        Assert.Equal(["items", "salaries"], Column(tables, 2));
        Assert.Equal(["TABLE", "TABLE"], Column(tables, 3));
        Assert.Equal(["items"], Column(await client.Tables(tablePattern: "it_ms"), 2));
        Assert.Empty(Column(await client.Tables(types: "VIEW"), 2));
    }

    [Fact]
    public async Task A_reader_is_not_told_about_tables_it_may_not_query()
    {
        var (host, client) = await StartAsync("reader");
        await using var _ = host; using var __ = client;
        var options = client.Options();

        var tables = await client.Tables();

        Assert.Equal(["items"], Column(tables, 2));
        Assert.Equal(["public"], Column(await client.Schemas(), 1));
    }

    [Fact]
    public async Task Tables_can_come_with_their_arrow_schemas()
    {
        var (host, client) = await StartAsync();
        await using var _ = host; using var __ = client;
        var options = client.Options();

        var batches = await client.Tables(tablePattern: "items", includeSchema: true);

        var batch = Assert.Single(batches);
        Assert.Equal("table_schema", batch.Schema.FieldsList[^1].Name);
        var schema = new Apache.Arrow.Ipc.ArrowStreamReader(new MemoryStream(((BinaryArray)batch.Column(4)).GetBytes(0).ToArray())).Schema;
        Assert.Equal(["id", "name"], schema.FieldsList.Select(f => f.Name));
    }

    [Fact]
    public async Task Primary_keys_are_the_ones_the_catalog_reports_none_for_a_host_that_reports_none()
    {
        var (host, client) = await StartAsync();
        await using var _ = host; using var __ = client;
        var options = client.Options();

        var keys = await client.PrimaryKeys("items", "public");

        Assert.Empty(Column(keys, 3));
    }

    [Fact]
    public async Task Server_info_names_the_server_and_says_whether_it_takes_writes()
    {
        var (host, client) = await StartAsync();
        await using var _ = host; using var __ = client;
        var options = client.Options();

        var info = await client.SqlInfo((int)SqlInfo.FlightSqlServerName, (int)SqlInfo.FlightSqlServerReadOnly);

        var batch = Assert.Single(info);
        Assert.Equal(2, batch.Length);
        var union = (DenseUnionArray)batch.Column(1);
        Assert.Equal("host", ((StringArray)union.Fields[0]).GetString(0));         // the fake host names itself "host"
        Assert.True(((BooleanArray)union.Fields[1]).GetValue(0));                  // and takes no writes
    }

    [Fact]
    public async Task A_statement_can_be_prepared_and_run_and_closed()
    {
        var (host, client) = await StartAsync();
        await using var _ = host; using var __ = client;
        var options = client.Options();

        var prepared = await client.Client.PrepareAsync("public.items", RawClient.NoTransaction, options);
        var info = await prepared.ExecuteAsync(options);
        var rows = (await client.ReadAsync(info, options)).Sum(b => b.Length);
        await prepared.CloseAsync(options);

        Assert.Equal(5, rows);
        await Assert.ThrowsAnyAsync<Exception>(() => prepared.ExecuteAsync(options));   // the client refuses a closed statement itself; the server's NotFound is covered by the handle tests
    }

    private static StatusCode Status(Exception e) => e switch
    {
        RpcException rpc => rpc.StatusCode,
        { InnerException: { } inner } => Status(inner),
        _ => StatusCode.Unknown,
    };

    [Fact]
    public async Task Updates_are_run_by_a_host_that_takes_them_and_refused_by_one_that_does_not()
    {
        var (writable, writing) = await StartAsync(services: new HostServices(new FakeServingHost(withWrites: true)));
        await using var _ = writable; using var __ = writing;
        Assert.Equal(1, await writing.UpdateAsync("update items"));

        var (readOnly, reading) = await StartAsync();
        await using var ___ = readOnly; using var ____ = reading;
        Assert.Equal(StatusCode.PermissionDenied, Status(await Assert.ThrowsAnyAsync<Exception>(() => reading.UpdateAsync("update items"))));
    }

    [Fact]
    public async Task A_token_belongs_to_the_session_that_issued_it_and_a_prepared_statement_to_the_caller_that_prepared_it()
    {
        await using var host = await ModuleHost.StartAsync();
        using var admin = await RawClient.LoginAsync(host.Address, "admin", "admin-pw");
        using var reader = await RawClient.LoginAsync(host.Address, "reader", "reader-pw");
        var prepared = await admin.Client.PrepareAsync("public.items", RawClient.NoTransaction, admin.Options());

        var stolen = new Apache.Arrow.Flight.Sql.PreparedStatement(reader.Client, prepared.Handle, prepared.DatasetSchema, prepared.ParameterSchema);
        var error = await Assert.ThrowsAnyAsync<Exception>(() => stolen.ExecuteAsync(reader.Options()));

        Assert.Equal(StatusCode.NotFound, Status(error));
    }
}
