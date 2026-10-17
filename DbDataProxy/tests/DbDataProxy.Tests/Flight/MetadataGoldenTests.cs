using System.Text;
using Apache.Arrow;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Types;
using DbDataProxy.Client;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Tests.Support;
using DuckDB.NET.Data;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataProxy.Tests.Flight;

/// <summary>
/// Golden outputs of every Flight SQL metadata response (GetCatalogs, GetDbSchemas, GetTables with and without schemas, GetTableTypes, GetPrimaryKeys,
/// GetSqlInfo) for a DuckDB source and for the Microsoft Text ODBC driver (through the 32-bit worker). They were recorded from the implementation that built
/// the answers with its own INFORMATION_SCHEMA SQL and the ODBC catalog calls, before the metadata moved onto the shared catalog readers (C5c), and the
/// readers must reproduce them. Set DBPROXY_UPDATE_GOLDEN=1 to rewrite the files after a deliberate change (and justify the diff in the commit message).
/// </summary>
public sealed class MetadataGoldenTests : IAsyncLifetime
{
    private string _dir = "";
    private WebApplication? _app;
    private string _address = "";

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), "dbproxy-golden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        string db = Path.Combine(_dir, "golden.duckdb");
        using (var c = new DuckDBConnection($"Data Source={db}"))
        {
            c.Open();
            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                CREATE SCHEMA sales;
                CREATE SCHEMA empty_schema;
                CREATE TABLE main.customers(id INTEGER PRIMARY KEY, name VARCHAR NOT NULL, balance DECIMAL(12,2), created TIMESTAMP, ratio DOUBLE, flag BOOLEAN, day DATE);
                CREATE TABLE sales.orders(order_id BIGINT, line INTEGER, sku VARCHAR, qty INTEGER NOT NULL, price DECIMAL(10,4), PRIMARY KEY (order_id, line));
                CREATE TABLE main."Odd Name"("weird col" INTEGER, "x""y" VARCHAR);
                CREATE TABLE main.nokey(a INTEGER, b DOUBLE);
                CREATE TABLE main.fk_child(id INTEGER PRIMARY KEY, cust INTEGER REFERENCES customers(id));
                CREATE VIEW main.v_customers AS SELECT id, name FROM customers;
                CREATE VIEW sales.v_totals AS SELECT order_id, sum(qty) AS q FROM sales.orders GROUP BY order_id;
                """;
            cmd.ExecuteNonQuery();
        }

        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Compression:Mode"] = "None",
            ["Proxy:DataSources:gduck:Provider"] = "duckdb",
            ["Proxy:DataSources:gduck:ConnectionString"] = $"Data Source={db}",
            ["Proxy:DataSources:gduck:Classifier"] = "off",
            ["Proxy:Users:0:Name"] = "alice",
            ["Proxy:Users:0:PasswordHash"] = PasswordHasher.Hash("pw", 1000),
            ["Proxy:Users:0:DataSources:0"] = "*",
        };
        if (OperatingSystem.IsWindows() && Environment.Is64BitProcess)
        {
            File.WriteAllText(Path.Combine(_dir, "people.csv"), "name,age,joined\r\nAda,36,2020-01-02\r\nGrace,45,2019-05-06\r\n");
            File.WriteAllText(Path.Combine(_dir, "orders.csv"), "id,total\r\n1,10.5\r\n");
            settings["Proxy:Worker:Path"] = WorkerBuild.EnsurePublished();
            settings["Proxy:DataSources:gtext:Provider"] = "odbc";
            settings["Proxy:DataSources:gtext:Bitness"] = "x86";
            settings["Proxy:DataSources:gtext:ConnectionString"] = $"Driver={{Microsoft Text Driver (*.txt; *.csv)}};Dbq={_dir};Extensions=asc,csv,tab,txt;";
        }
        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First().Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Duckdb_metadata_responses_match_the_golden_file() => await CheckAsync("gduck", "metadata-duckdb.txt", DuckScenarios());

    [Fact]
    public async Task Odbc_text_driver_metadata_responses_match_the_golden_file()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows() && Environment.Is64BitProcess, "Requires 64-bit Windows with the 32-bit text ODBC driver.");
        await CheckAsync("gtext", "metadata-odbc-text.txt", TextScenarios());
    }

    // ---- scenarios -------------------------------------------------------------------------------------------------------

    private delegate Task<QueryResultStream> Call(ProxyFlightClient client, QueryOptions options);

    private static List<(string Name, Call Run)> DuckScenarios() =>
    [
        ("GetCatalogs", (c, o) => c.GetCatalogsAsync(o)),
        ("GetDbSchemas", (c, o) => c.GetSchemasAsync(options: o)),
        ("GetDbSchemas schema=sal%", (c, o) => c.GetSchemasAsync(schemaPattern: "sal%", options: o)),
        ("GetDbSchemas schema=no_such_%", (c, o) => c.GetSchemasAsync(schemaPattern: "no_such_%", options: o)),
        ("GetDbSchemas catalog=golden", (c, o) => c.GetSchemasAsync(catalog: "golden", options: o)),
        ("GetDbSchemas catalog=missing", (c, o) => c.GetSchemasAsync(catalog: "missing", options: o)),
        ("GetTableTypes", (c, o) => c.GetTableTypesAsync(o)),
        ("GetTables", (c, o) => c.GetTablesAsync(options: o)),
        ("GetTables catalog=golden", (c, o) => c.GetTablesAsync(catalog: "golden", options: o)),
        ("GetTables schema=sales", (c, o) => c.GetTablesAsync(schemaPattern: "sales", options: o)),
        ("GetTables table=c%", (c, o) => c.GetTablesAsync(tablePattern: "c%", options: o)),
        ("GetTables table=_ustomers", (c, o) => c.GetTablesAsync(tablePattern: "_ustomers", options: o)),
        ("GetTables types=VIEW", (c, o) => c.GetTablesAsync(tableTypes: ["VIEW"], options: o)),
        ("GetTables types=TABLE", (c, o) => c.GetTablesAsync(tableTypes: ["table"], options: o)),
        ("GetTables types=SYSTEM TABLE", (c, o) => c.GetTablesAsync(tableTypes: ["SYSTEM TABLE"], options: o)),
        ("GetTables include_schema catalog=golden", (c, o) => c.GetTablesAsync(catalog: "golden", includeSchema: true, options: o)),
        ("GetTables include_schema schema=sales", (c, o) => c.GetTablesAsync(schemaPattern: "sales", includeSchema: true, options: o)),
        ("GetTables include_schema table=nokey", (c, o) => c.GetTablesAsync(tablePattern: "nokey", includeSchema: true, options: o)),
        ("GetTables include_schema table=Odd%", (c, o) => c.GetTablesAsync(tablePattern: "Odd%", includeSchema: true, options: o)),
        ("GetPrimaryKeys customers", (c, o) => c.GetPrimaryKeysAsync("customers", options: o)),
        ("GetPrimaryKeys orders schema=sales", (c, o) => c.GetPrimaryKeysAsync("orders", schema: "sales", options: o)),
        ("GetPrimaryKeys orders (no schema)", (c, o) => c.GetPrimaryKeysAsync("orders", options: o)),
        ("GetPrimaryKeys nokey", (c, o) => c.GetPrimaryKeysAsync("nokey", options: o)),
        ("GetPrimaryKeys v_customers", (c, o) => c.GetPrimaryKeysAsync("v_customers", options: o)),
        ("GetPrimaryKeys missing", (c, o) => c.GetPrimaryKeysAsync("missing", options: o)),
        ("GetPrimaryKeys fk_child catalog=golden schema=main", (c, o) => c.GetPrimaryKeysAsync("fk_child", catalog: "golden", schema: "main", options: o)),
        ("GetSqlInfo", (c, o) => c.GetSqlInfoAsync(o)),
    ];

    private static List<(string Name, Call Run)> TextScenarios() =>
    [
        ("GetCatalogs", (c, o) => c.GetCatalogsAsync(o)),
        ("GetDbSchemas", (c, o) => c.GetSchemasAsync(options: o)),
        ("GetTableTypes", (c, o) => c.GetTableTypesAsync(o)),
        ("GetTables", (c, o) => c.GetTablesAsync(options: o)),
        ("GetTables table=p%", (c, o) => c.GetTablesAsync(tablePattern: "p%", options: o)),
        ("GetTables types=VIEW", (c, o) => c.GetTablesAsync(tableTypes: ["VIEW"], options: o)),
        ("GetTables include_schema", (c, o) => c.GetTablesAsync(includeSchema: true, options: o)),
        ("GetPrimaryKeys people#csv", (c, o) => c.GetPrimaryKeysAsync("people#csv", options: o)),
        ("GetPrimaryKeys missing", (c, o) => c.GetPrimaryKeysAsync("missing", options: o)),
        ("GetSqlInfo", (c, o) => c.GetSqlInfoAsync(o)),
    ];

    // ---- recording and comparing -------------------------------------------------------------------------------------------

    private async Task CheckAsync(string dataSource, string fileName, List<(string Name, Call Run)> scenarios)
    {
        using var client = new ProxyFlightClient(_address);
        await client.AuthenticateAsync("alice", "pw");
        var options = new QueryOptions { DataSource = dataSource };

        var text = new StringBuilder();
        foreach (var (name, run) in scenarios)
        {
            text.Append("== ").AppendLine(name);
            try
            {
                var stream = await run(client, options);
                text.Append("schema: ").AppendLine(DescribeSchema(stream.Schema));
                bool sqlInfo = name == "GetSqlInfo";
                int rows = 0;
                await foreach (var batch in stream)
                    for (int r = 0; r < batch.Length; r++, rows++)
                        text.Append("row: ").AppendLine(sqlInfo ? SqlInfoRow(batch, r) : Row(batch, r));
                text.Append("rows: ").Append(rows).AppendLine();
            }
            catch (Exception ex)
            {
                var rpc = ex as Grpc.Core.RpcException ?? ex.InnerException as Grpc.Core.RpcException;
                text.Append("error: ").AppendLine(rpc is null ? ex.GetType().Name : rpc.StatusCode.ToString());
            }
        }

        string actual = text.ToString().Replace(_dir, "<dir>", StringComparison.OrdinalIgnoreCase).Replace(_dir.Replace('\\', '/'), "<dir>", StringComparison.OrdinalIgnoreCase)
            .Replace("\r\n", "\n");
        string path = Path.Combine(WorkerBuild.RepositoryRoot(), "tests", "DbDataProxy.Tests", "Golden", fileName);
        if (Environment.GetEnvironmentVariable("DBPROXY_UPDATE_GOLDEN") == "1" || !File.Exists(path))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, actual);
            if (!File.Exists(path)) Assert.Fail("could not write " + path);
        }
        else
        {
            string expected = File.ReadAllText(path).Replace("\r\n", "\n");
            Assert.True(expected == actual, "Metadata responses differ from " + fileName + ":\n" + FirstDifference(expected, actual));
        }
    }

    private static string FirstDifference(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        for (int i = 0; i < Math.Max(e.Length, a.Length); i++)
            if ((i < e.Length ? e[i] : null) != (i < a.Length ? a[i] : null))
                return $"line {i + 1}\n  golden: {(i < e.Length ? e[i] : "<end>")}\n  actual: {(i < a.Length ? a[i] : "<end>")}\n  (scenario near: {a.Take(i + 1).LastOrDefault(l => l.StartsWith("== "))})";
        return "(none)";
    }

    private static string DescribeSchema(Schema schema) => string.Join(", ", schema.FieldsList.Select(DescribeField));

    private static string DescribeField(Field f) =>
        f.Name + ":" + DescribeType(f.DataType) + (f.IsNullable ? "?" : "") + (f.HasMetadata ? "{" + string.Join(",", f.Metadata.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value)) + "}" : "");

    private static string DescribeType(IArrowType type) => type switch
    {
        Decimal128Type d => $"decimal128({d.Precision},{d.Scale})",
        TimestampType t => $"timestamp[{t.Unit}{(t.Timezone is null ? "" : "," + t.Timezone)}]",
        Time64Type t => $"time64[{t.Unit}]",
        Time32Type t => $"time32[{t.Unit}]",
        UnionType u => "union<" + string.Join("|", u.Fields.Select(DescribeField)) + ">",
        ListType l => "list<" + DescribeField(l.ValueField) + ">",
        _ => type.Name,
    };

    private static string Row(RecordBatch batch, int row)
    {
        var cells = new List<string>();
        for (int c = 0; c < batch.ColumnCount; c++)
        {
            var column = batch.Column(c);
            if (batch.Schema.GetFieldByIndex(c).Name == "table_schema" && column is BinaryArray binary)
            {
                var schema = SchemaExtensions.DeserializeSchema(binary.GetBytes(row).ToArray());
                cells.Add("{" + DescribeSchema(schema) + "}");
            }
            else cells.Add(ArrowText.Get(column, row) ?? "<null>");
        }
        return string.Join(" | ", cells);
    }

    private static string SqlInfoRow(RecordBatch batch, int row)
    {
        uint id = ((UInt32Array)batch.Column("info_name")).GetValue(row)!.Value;
        var union = (DenseUnionArray)batch.Column("value");
        var child = union.Fields[union.TypeIds[row]];
        int offset = union.ValueOffsets[row];
        string value = child switch
        {
            StringArray a => a.GetString(offset),
            BooleanArray a => a.GetValue(offset)!.Value.ToString(),
            Int64Array a => a.GetValue(offset)!.Value.ToString(),
            _ => child.GetType().Name,
        };
        // Versions change with the packages and the drivers; the rest of the dialect facts are the contract.
        if (id is 1 or 2) value = "<version>";
        return id + " = " + value;
    }
}
