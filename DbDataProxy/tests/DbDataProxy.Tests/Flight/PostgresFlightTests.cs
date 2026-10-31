using Apache.Arrow;
using DbDataProxy.Client;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Hosting;
using DbDataProxy.Tests.Support;
using Grpc.Core;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace DbDataProxy.Tests.Flight;

/// <summary>
/// The whole proxy in front of a real PostgreSQL (plan 13a, P12): queries and types, parameters, updates, catalog metadata, the read-only classifier
/// and the read-only session, the concurrency limits, database-credential pass-through and the failure model. Skipped without a reachable server
/// (see <see cref="PostgresEnv"/>); CI runs it against the shared PostgreSQL container and requires it.
/// </summary>
public sealed class PostgresFlightTests : IAsyncLifetime
{
    private const string Schema = "dbproxy_p12";
    private const string Table = Schema + ".items";
    private const string PassUser = "dbproxy_p12_caller";
    private const string PassPassword = "Caller_Pw_1";

    private WebApplication? _app;
    private string _address = "";
    private string _dir = "";

    public async ValueTask InitializeAsync()
    {
        await PostgresEnv.EnsureReachableAsync();
        await PostgresEnv.ExecuteAsync(
            $"DROP SCHEMA IF EXISTS {Schema} CASCADE",
            $"CREATE SCHEMA {Schema}",
            $"CREATE TABLE {Table}(id int NOT NULL PRIMARY KEY, name text NOT NULL, price numeric(10,2) NULL, added date NULL)",
            $"INSERT INTO {Table} VALUES (1, 'widget', 9.50, '2024-01-02'), (2, 'gadget', 19.99, '2024-02-03'), (3, 'gizmo', NULL, NULL)",
            $"CREATE VIEW {Schema}.items_view AS SELECT id, name FROM {Table}",
            $"DROP ROLE IF EXISTS {PassUser}",
            $"CREATE ROLE {PassUser} LOGIN PASSWORD '{PassPassword}'",
            $"GRANT USAGE ON SCHEMA {Schema} TO {PassUser}",
            $"GRANT SELECT ON {Table} TO {PassUser}");

        _dir = Path.Combine(Path.GetTempPath(), "dbproxy-pg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);

        var settings = new Dictionary<string, string?>
        {
            ["Proxy:Listen:Address"] = "127.0.0.1",
            ["Proxy:Listen:Port"] = "0",
            ["Proxy:Tls:Mode"] = "None",
            ["Proxy:DataDirectory"] = _dir,
            ["Proxy:Compression:Mode"] = "Arrow",
            ["Proxy:Compression:Codec"] = "zstd",
            ["Proxy:Users:0:Name"] = "alice",
            ["Proxy:Users:0:PasswordHash"] = PasswordHasher.Hash("pw", 1000),
        };
        string[] sources = ["pg_rw", "pg_ro", "pg_off", "pg_pass"];
        for (int i = 0; i < sources.Length; i++) settings[$"Proxy:Users:0:DataSources:{i}"] = sources[i];

        settings["Proxy:DataSources:pg_rw:Provider"] = "postgres";
        settings["Proxy:DataSources:pg_rw:ConnectionString"] = PostgresEnv.ConnectionString();
        settings["Proxy:DataSources:pg_rw:AllowWrites"] = "true";

        // read-only, strict classifier: the statement text is checked, and the session is read-only too
        settings["Proxy:DataSources:pg_ro:Provider"] = "postgres";
        settings["Proxy:DataSources:pg_ro:ConnectionString"] = PostgresEnv.ConnectionString();
        settings["Proxy:DataSources:pg_ro:Classifier"] = "strict";

        // read-only with the classifier off: only the session stands between a write and the table
        settings["Proxy:DataSources:pg_off:Provider"] = "postgres";
        settings["Proxy:DataSources:pg_off:ConnectionString"] = PostgresEnv.ConnectionString();
        settings["Proxy:DataSources:pg_off:Classifier"] = "off";

        settings["Proxy:DataSources:pg_pass:Provider"] = "postgres";
        settings["Proxy:DataSources:pg_pass:PassThrough"] = "true";
        settings["Proxy:DataSources:pg_pass:ConnectionString"] = PostgresEnv.WithoutLogin();
        settings["Proxy:DataSources:pg_pass:Classifier"] = "strict";

        _app = ProxyHost.Build([], b => b.Configuration.AddInMemoryCollection(settings));
        await _app.StartAsync();
        _address = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()
            .Replace("0.0.0.0", "127.0.0.1");
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
        try { if (_dir.Length > 0) Directory.Delete(_dir, true); } catch (IOException) { }
        if (Environment.GetEnvironmentVariable("DBPROXY_TEST_POSTGRES") is { Length: > 0 })
        {
            try { await PostgresEnv.ExecuteAsync($"DROP SCHEMA IF EXISTS {Schema} CASCADE", $"DROP ROLE IF EXISTS {PassUser}"); }
            catch (Exception ex) when (ex is Npgsql.NpgsqlException or InvalidOperationException) { }
        }
    }

    private async Task<ProxyClient> LoginAsync(string user = "alice", string password = "pw")
    {
        var client = new ProxyClient(_address);
        await client.AuthenticateAsync(user, password);
        return client;
    }

    private static async Task<List<string?[]>> RowsAsync(QueryResultStream stream)
    {
        var rows = new List<string?[]>();
        await foreach (var batch in stream)
            for (int r = 0; r < batch.Length; r++)
                rows.Add(Enumerable.Range(0, batch.ColumnCount).Select(c => ArrowText.Get(batch.Column(c), r)).ToArray());
        return rows;
    }

    private static QueryOptions On(string dataSource) => new() { DataSource = dataSource };

    [Fact]
    public async Task Queries_return_typed_columns_over_arrow_compression()
    {
        using var client = await LoginAsync();
        var rows = await RowsAsync(await client.Raw.QueryAsync($"SELECT id, name, price, added FROM {Table} ORDER BY id", On("pg_rw")));

        Assert.Equal(3, rows.Count);
        Assert.Equal(["1", "widget", "9.50", "2024-01-02"], rows[0]);
        Assert.Null(rows[2][2]);
        Assert.Null(rows[2][3]);
    }

    [Fact]
    public async Task The_common_postgresql_types_arrive_as_arrow_types()
    {
        using var client = await LoginAsync();
        var batches = new List<RecordBatch>();
        await foreach (var b in await client.Raw.QueryAsync(
            """
            SELECT 7::smallint AS i2, 8::int AS i4, 9::bigint AS i8, 1.5::real AS f4, 2.25::float8 AS f8, 12.50::numeric(10,2) AS dec,
                   true AS b, 'text'::text AS t, 'abc'::varchar(5) AS v, DATE '2024-03-04' AS d,
                   TIMESTAMP '2024-03-04 05:06:07.123456' AS ts, TIMESTAMPTZ '2024-03-04 05:06:07+00' AS tstz,
                   TIME '05:06:07' AS tm, '\xDEADBEEF'::bytea AS bin, '123e4567-e89b-12d3-a456-426614174000'::uuid AS id,
                   ARRAY[1,2,3] AS arr, '{"a": 1}'::jsonb AS doc, NULL::int AS nothing
            """, On("pg_rw"))) batches.Add(b);

        var batch = Assert.Single(batches);
        Assert.Equal(1, batch.Length);
        string Text(string column) => ArrowText.Get(batch.Column(column), 0)!;
        Assert.Equal("7", Text("i2"));
        Assert.Equal("8", Text("i4"));
        Assert.Equal("9", Text("i8"));
        Assert.Equal("true", Text("b").ToLowerInvariant());
        Assert.Equal("abc", Text("v"));
        Assert.Equal("12.50", Text("dec"));
        Assert.Equal("2024-03-04", Text("d"));
        Assert.Equal("123e4567-e89b-12d3-a456-426614174000", Text("id"));
        Assert.Equal("[1,2,3]", Text("arr"));
        Assert.Contains("\"a\"", Text("doc"));
        Assert.Null(ArrowText.Get(batch.Column("nothing"), 0));
        Assert.IsType<Apache.Arrow.Types.Decimal128Type>(batch.Schema.GetFieldByName("dec").DataType);
        Assert.IsType<Apache.Arrow.Types.Date32Type>(batch.Schema.GetFieldByName("d").DataType);
        Assert.IsType<Apache.Arrow.Types.TimestampType>(batch.Schema.GetFieldByName("tstz").DataType);
        Assert.IsType<Apache.Arrow.Types.BinaryType>(batch.Schema.GetFieldByName("bin").DataType);
        Assert.StartsWith("2024-03-04", Text("tstz"));
    }

    [Fact]
    public async Task A_numeric_wider_than_decimal_fails_the_query_and_a_cast_reads_it()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.QueryAsync("SELECT 1e40::numeric AS n", On("pg_rw"))));
        Assert.Equal(StatusCode.Internal, ex.StatusCode);   // a failure with a kind, not a dropped connection
        Assert.Equal("10000000000000000000000000000000000000000", (await RowsAsync(await client.Raw.QueryAsync("SELECT (1e40::numeric)::text AS n", On("pg_rw"))))[0][0]);
    }

    [Fact]
    public async Task Parameters_bind_for_question_marks_and_named_markers()
    {
        using var client = await LoginAsync();

        var positional = await RowsAsync(await client.Raw.QueryAsync(
            $"SELECT name FROM {Table} WHERE id >= ? AND name <> ? ORDER BY id", [2, "gizmo"], On("pg_rw")));
        Assert.Equal(["gadget"], positional.Select(r => r[0]));

        var named = await RowsAsync(await client.Raw.QueryAsync(
            $"SELECT name FROM {Table} WHERE price > @min", new Dictionary<string, object?> { ["min"] = 5m }, On("pg_rw")));
        Assert.Equal(2, named.Count);

        var hostile = await RowsAsync(await client.Raw.QueryAsync(
            $"SELECT count(*) FROM {Table} WHERE name = ?", ["x'; DROP TABLE " + Table + "; --"], On("pg_rw")));
        Assert.Equal("0", hostile[0][0]);
        Assert.Equal("3", (await RowsAsync(await client.Raw.QueryAsync($"SELECT count(*) FROM {Table}", On("pg_rw"))))[0][0]);

        // a marker inside a string, a comment or a quoted identifier is not a parameter
        var inText = await RowsAsync(await client.Raw.QueryAsync("SELECT '?' AS q, ? AS p -- ?", ["x"], On("pg_rw")));
        Assert.Equal(["?", "x"], inText[0]);
    }

    [Fact]
    public async Task Updates_with_parameter_rows_insert_and_report_counts()
    {
        using var client = await LoginAsync();
        long inserted = await client.Raw.ExecuteUpdateAsync($"INSERT INTO {Table}(id, name, price) VALUES (?, ?, ?)",
            [[100, "bulk-a", 1.25m], [101, "bulk-b", null]], On("pg_rw"));
        Assert.Equal(2, inserted);

        long deleted = await client.Raw.ExecuteUpdateAsync($"DELETE FROM {Table} WHERE id >= ?", [100], On("pg_rw"));
        Assert.Equal(2, deleted);
    }

    [Fact]
    public async Task Metadata_comes_from_the_catalog()
    {
        using var client = await LoginAsync();
        var options = On("pg_rw");

        var tables = await RowsAsync(await client.Raw.GetTablesAsync(schemaPattern: Schema, options: options));
        Assert.Contains(tables, r => r[2] == "items" && r[3] == "TABLE" && r[1] == Schema);
        Assert.Contains(tables, r => r[2] == "items_view" && r[3] == "VIEW");

        Assert.Contains(await RowsAsync(await client.Raw.GetSchemasAsync(options: options)), r => r[1] == Schema);

        var keys = await RowsAsync(await client.Raw.GetPrimaryKeysAsync("items", schema: Schema, options: options));
        Assert.Equal("id", Assert.Single(keys)[3]);
        Assert.Empty(await RowsAsync(await client.Raw.GetPrimaryKeysAsync("items_view", schema: Schema, options: options)));
    }

    [Fact]
    public async Task Table_schemas_carry_declared_precision_scale_and_nullability()
    {
        using var client = await LoginAsync();
        Apache.Arrow.Schema? schema = null;
        await foreach (var batch in await client.Raw.GetTablesAsync(schemaPattern: Schema, tablePattern: "items", includeSchema: true, options: On("pg_rw")))
            if (batch.Length > 0)
                schema = Apache.Arrow.Flight.Sql.SchemaExtensions.DeserializeSchema(((BinaryArray)batch.Column("table_schema")).GetBytes(0).ToArray());
        Assert.NotNull(schema);

        var price = Assert.IsType<Apache.Arrow.Types.Decimal128Type>(schema.GetFieldByName("price").DataType);
        Assert.Equal((10, 2), (price.Precision, price.Scale));
        Assert.False(schema.GetFieldByName("id").IsNullable);     // primary key
        Assert.False(schema.GetFieldByName("name").IsNullable);   // declared NOT NULL
        Assert.True(schema.GetFieldByName("price").IsNullable);
    }

    [Fact]
    public async Task Sql_info_describes_the_backend()
    {
        using var client = await LoginAsync();
        var pg = await SqlInfoReader.ReadAsync(await client.Raw.GetSqlInfoAsync(On("pg_rw")));
        Assert.Equal("PostgreSQL", pg[0]);
        Assert.Matches(@"^\d+", (string)pg[1]);
        Assert.Equal("\"", pg[504]);
        Assert.Equal(false, pg[3]);   // AllowWrites is on
        Assert.Equal(true, (await SqlInfoReader.ReadAsync(await client.Raw.GetSqlInfoAsync(On("pg_ro"))))[3]);
    }

    // ---- the read-only classifier and session, and the failure model -----------------------------------------------------

    public static TheoryData<string> Hostile =>
    [
        $"INSERT INTO {Table} VALUES (9, 'x', 1, NULL)",
        $"UPDATE {Table} SET price = 0",
        $"DELETE FROM {Table}",
        $"DROP TABLE {Table}",
        "CREATE TABLE dbproxy_p12_evil(a int)",
        $"SELECT 1; DROP TABLE {Table}",
        $"WITH d AS (DELETE FROM {Table} RETURNING *) SELECT * FROM d",
        $"WITH d AS (SELECT 1) DELETE FROM {Table}",
        $"SELECT * INTO dbproxy_p12_copy FROM {Table}",
        $"SELECT * FROM {Table} FOR UPDATE",
        $"COPY {Table} TO '/tmp/dbproxy_p12.csv'",
        "COPY (SELECT 1) TO PROGRAM 'id'",
        "SET default_transaction_read_only = off",
        "DO $$ BEGIN PERFORM 1; END $$",
        "SELECT pg_read_file('/etc/passwd')",
        "SELECT pg_ls_dir('/')",
        "SELECT lo_import('/etc/passwd')",
        "SELECT * FROM dblink('host=x', 'select 1') AS t(a int)",
        "SELECT set_config('default_transaction_read_only', 'off', false)",
        "SELECT pg_advisory_lock(1)",
        "SELECT pg_sleep(1)",
        "SELECT nextval('dbproxy_p12_seq')",
        "SELECT pg_terminate_backend(1)",
        "",
        "   -- only a comment",
        "SELECT 'unterminated",
    ];

    public static TheoryData<string> Benign =>
    [
        "SELECT 1 AS n",
        $"SELECT count(*) AS n FROM {Table} WHERE id > 1",
        "WITH t AS (SELECT 1 AS a) SELECT * FROM t",
        "SELECT $$; DROP TABLE x; --$$ AS s",
        "SELECT $tag$it's; DELETE FROM x$tag$ AS s",
        "SELECT '; DROP TABLE x' AS s, \"id\" FROM " + Table + " LIMIT 1",
        "SELECT E'a\\'; DROP TABLE x' AS s",
        "SELECT 1 AS n; ",
    ];

    private async Task AssertTableIntactAsync(ProxyClient client)
    {
        var rows = await RowsAsync(await client.Raw.QueryAsync($"SELECT count(*) FROM {Table}", On("pg_rw")));
        Assert.Equal("3", rows[0][0]);
        Assert.Empty(await RowsAsync(await client.Raw.GetTablesAsync(tablePattern: "dbproxy_p12_%", options: On("pg_rw"))));
    }

    [Theory, MemberData(nameof(Hostile))]
    public async Task Strict_refuses_hostile_text_on_the_query_path(string sql)
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.QueryAsync(sql, On("pg_ro"))));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);
        await AssertTableIntactAsync(client);
    }

    [Theory, MemberData(nameof(Benign))]
    public async Task Strict_lets_plain_selects_through(string sql)
    {
        using var client = await LoginAsync();
        Assert.NotEmpty(await RowsAsync(await client.Raw.QueryAsync(sql, On("pg_ro"))));
    }

    [Fact]
    public async Task With_the_classifier_off_the_read_only_session_still_refuses_a_write()
    {
        using var client = await LoginAsync();
        var ex = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.QueryAsync($"DELETE FROM {Table}", On("pg_off"))));
        Assert.Equal(StatusCode.PermissionDenied, ex.StatusCode);   // SQLSTATE 25006, read-only transaction
        Assert.Contains("[postgres 25006]", ex.Status.Detail);
        await AssertTableIntactAsync(client);

        var update = await Rpc.ExpectAsync(async () => await client.Raw.ExecuteUpdateAsync($"UPDATE {Table} SET price = 0 WHERE id > ?", [0], On("pg_off")));
        Assert.Equal(StatusCode.PermissionDenied, update.StatusCode);
        await AssertTableIntactAsync(client);
    }

    [Fact]
    public async Task A_writable_source_does_not_apply_the_classifier()
    {
        using var client = await LoginAsync();
        Assert.Equal(1, await client.Raw.ExecuteUpdateAsync($"UPDATE {Table} SET price = 9.50 WHERE id = ?", [1], On("pg_rw")));
    }

    [Fact]
    public async Task Failures_report_the_kind_and_sqlstate_but_not_the_engines_words()
    {
        using var client = await LoginAsync();

        var missing = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.QueryAsync("SELECT * FROM confidential_salaries_2026", On("pg_rw"))));
        Assert.Equal(StatusCode.InvalidArgument, missing.StatusCode);
        Assert.Contains("[postgres 42P01]", missing.Status.Detail);
        Assert.DoesNotContain("confidential_salaries_2026", missing.Status.Detail);

        var syntax = await Rpc.ExpectAsync(async () => await RowsAsync(await client.Raw.QueryAsync("SELEKT 1", On("pg_rw"))));
        Assert.Equal(StatusCode.InvalidArgument, syntax.StatusCode);
        Assert.Contains("[postgres 42601]", syntax.Status.Detail);
        Assert.DoesNotContain("SELEKT", syntax.Status.Detail);
    }

    // ---- pass-through ----------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Database_credentials_pass_through_to_the_server()
    {
        using (var good = await LoginAsync(PassUser, PassPassword))
        {
            var rows = await RowsAsync(await good.Raw.QueryAsync("SELECT current_user", On("pg_pass")));
            Assert.Equal(PassUser, rows[0][0]);
            Assert.Equal("3", (await RowsAsync(await good.Raw.QueryAsync($"SELECT count(*) FROM {Table}", On("pg_pass"))))[0][0]);
            // the caller is held to what the database lets that login do, and to the classifier
            Assert.Equal(StatusCode.PermissionDenied, (await Rpc.ExpectAsync(async () => await RowsAsync(await good.Raw.QueryAsync($"DELETE FROM {Table}", On("pg_pass"))))).StatusCode);
        }

        using var bad = await LoginAsync(PassUser, "definitely-not-the-password");
        var ex = await Rpc.ExpectAsync(async () => await RowsAsync(await bad.Raw.QueryAsync("SELECT 1", On("pg_pass"))));
        Assert.Equal(StatusCode.Unauthenticated, ex.StatusCode);
    }
}
