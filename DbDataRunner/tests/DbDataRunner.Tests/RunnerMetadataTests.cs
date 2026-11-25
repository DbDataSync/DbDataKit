using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Runner;
using DbDataKit.Runner.Api;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataRunner.Tests;

/// <summary>
/// The built-in package <c>dbdatakit-metadata</c> (<c>POST /api/v1/metadata/{channel}/{task}</c>): <c>discover</c> and <c>describe</c> read a database through the binding of a project package's slot, in a task process of their own, for the
/// person a console names, and answer shape only. The refusals run on every machine; the reads run on SQL Server and PostgreSQL when the shared engines are up (<c>DBDATAKIT_TEST_SQLSERVER</c>, <c>DBDATAKIT_TEST_POSTGRES</c>).
/// </summary>
public sealed class RunnerMetadataTests
{
    private const string Variable = "DBDATAKIT_TEST_METADATA_PASSWORD";

    /// <summary>A value of a row of the test table: no answer may hold it.</summary>
    private const string RowValue = "row-value-7731";

    /// <summary>The project package <c>sample</c> declares the slot <c>source</c> in each channel given and binds it there to <c>erp_&lt;channel&gt;</c>.</summary>
    private static async Task Bind(RunnerHarness h, params string[] channels)
    {
        h.Install(SamplePackage.Task("fails"));
        var service = new RunnerPublishing(h.State);
        var files = service.Publisher.Artifacts(RunnerPublishing.Kind, "sample", 1).Select(a => new PublishFile(a.Path, a.Sha256, a.Size, a.MediaType)).ToList();
        var manifest = PackageManifestWriter.Write(SamplePackage.Manifest(SamplePackage.Task("ok") with { Connections = [new ConnectionSlot("source", "database")] }));
        foreach (var channel in channels)
        {
            service.Bind("sample", channel, new Dictionary<string, string> { ["source"] = "erp_" + channel }, null, RunnerActor.Local());
            service.Publish(new PublishInput("sample", manifest, files, channel, PublishMode.Stage), RunnerActor.Local());
            var active = service.Publisher.HeadsOf(RunnerPublishing.Kind, "sample").FirstOrDefault(x => x.Channel == channel)?.Active;
            await service.ActivateAsync("sample", channel, active, RunnerActor.Local(), new ActivateOptions(Anyway: true));
        }
    }

    private static object Read(string slot = "source", object? inputs = null, string? person = "Ada via Main console") =>
        person is null ? new { package = "sample", slot, inputs } : new { package = "sample", slot, inputs, on_behalf_of = person };

    [Fact]
    public async Task A_read_is_refused_for_a_slot_the_package_does_not_declare_a_call_that_names_no_person_and_the_generic_run_route()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await Bind(h, "dev");
        await h.StartAsync();

        var (undeclared, said) = await h.PostAsync("/api/v1/metadata/dev/discover", ServerRole.Operator, Read(slot: "elsewhere"));
        Assert.Equal(HttpStatusCode.NotFound, undeclared);
        Assert.Contains("does not declare the slot 'elsewhere'", (string)said!["error"]!);

        var (nobody, why) = await h.PostAsync("/api/v1/metadata/dev/discover", ServerRole.Operator, Read(person: null));
        Assert.Equal(HttpStatusCode.BadRequest, nobody);
        Assert.Contains("on_behalf_of", (string)why!["error"]!);

        Assert.Equal(HttpStatusCode.NotFound, (await h.PostAsync("/api/v1/metadata/dev/facts", ServerRole.Operator, Read())).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await h.PostAsync("/api/v1/metadata/prod/discover", ServerRole.Operator, Read())).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync("/api/v1/metadata/dev/describe", ServerRole.Operator, Read())).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await h.PostAsync("/api/v1/metadata/dev/discover", ServerRole.Operator, Read(inputs: new { package = "other" }))).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await h.PostAsync("/api/v1/metadata/dev/discover", ServerRole.Viewer, Read())).Status);
        // the built-in package is not started through the generic route, where nothing checks the slot it would read through
        Assert.Equal(HttpStatusCode.Conflict, (await h.PostAsync($"/api/v1/tasks/{MetadataPackage.Name}/dev/discover/run", ServerRole.Operator, new { inputs = new { package = "sample", slot = "source" } })).Status);

        var refused = h.Audit.Read(new AuditQuery(Verb: RunnerMetadataApi.Verb, Limit: 50));
        Assert.True(refused.Count >= 7);
        Assert.All(refused, e => Assert.Equal(AuditEvent.Refused, e.Entry.Outcome));
        Assert.Empty(h.Audit.Read(new AuditQuery(Verb: RunnerControl.RunVerb, Limit: 50)));
    }

    [Fact]
    public async Task The_built_in_package_is_installed_once_per_channel_and_again_only_when_its_files_change()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite);
        await Bind(h, "dev");
        await h.StartAsync();

        var (first, body) = await h.PostAsync("/api/v1/metadata/dev/discover", ServerRole.Operator, Read());
        Assert.Equal(HttpStatusCode.Accepted, first);
        Assert.Equal((string?)body!["run_id"], (string?)body["key"]);
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync("/api/v1/metadata/dev/discover", ServerRole.Operator, Read())).Status);
        var versions = new RunnerPublishing(h.State).Publisher.HeadsOf(RunnerPublishing.Kind, MetadataPackage.Name);
        Assert.Equal(1, Assert.Single(versions).Active);
        Assert.Single(h.Audit.Read(new AuditQuery(Verb: RunnerPublishing.InstallVerb, Limit: 50)), e => e.Entry.TargetId == $"{MetadataPackage.Name}/dev");
    }

    /// <summary>
    /// A package this runner holds no version of (a draft that was never published): the channels it serves are listed with the bindings it holds for the package; a read of a slot nothing binds is refused with 409, and once the slot is
    /// bound in <c>dev</c> the read starts, with no version of the package anywhere.
    /// </summary>
    [Fact]
    public async Task A_package_with_no_version_is_read_through_the_slot_its_channel_binds_and_its_channels_are_listed()
    {
        await using var h = new RunnerHarness(StateEngineIds.Sqlite, channels: ["dev", "prod"]);
        await h.StartAsync();
        var draft = new { package = "sales-dw", slot = "warehouse", on_behalf_of = "Ada via Main console" };

        var (listed, channels) = await h.GetAsync("/api/v1/metadata/channels?package=sales-dw");
        Assert.Equal(HttpStatusCode.OK, listed);
        Assert.False((bool)channels!["holds_version"]!);
        Assert.Equal(["dev", "prod"], channels["channels"]!.AsArray().Select(c => (string)c!).ToArray());
        Assert.Empty(channels["bindings"]!.AsArray());
        Assert.Equal(HttpStatusCode.BadRequest, (await h.GetAsync("/api/v1/metadata/channels?package=Not%20a%20name")).Status);

        var (unbound, why) = await h.PostAsync("/api/v1/metadata/dev/discover", ServerRole.Operator, draft);
        Assert.Equal(HttpStatusCode.Conflict, unbound);
        Assert.Contains("has no binding in channel 'dev'", (string)why!["error"]!);

        new RunnerPublishing(h.State).Bind("sales-dw", "dev", new Dictionary<string, string> { ["warehouse"] = "warehouse_dev" }, null, RunnerActor.Local());
        var (_, bound) = await h.GetAsync("/api/v1/metadata/channels?package=sales-dw");
        var binding = Assert.Single(bound!["bindings"]!.AsArray())!;
        Assert.Equal(("dev", "warehouse", "warehouse_dev"), ((string)binding["channel"]!, (string)binding["slot"]!, (string)binding["bound_to"]!));

        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync("/api/v1/metadata/dev/discover", ServerRole.Operator, draft)).Status);
        Assert.Equal(HttpStatusCode.Accepted, (await h.PostAsync("/api/v1/metadata/dev/describe", ServerRole.Operator, new { package = "sales-dw", slot = "warehouse", inputs = new { sql = "SELECT 1 AS one" }, on_behalf_of = "Ada" })).Status);
        Assert.Equal(HttpStatusCode.Conflict, (await h.PostAsync("/api/v1/metadata/prod/discover", ServerRole.Operator, draft)).Status);
        Assert.Empty(new RunnerPublishing(h.State).Publisher.HeadsOf(RunnerPublishing.Kind, "sales-dw"));
    }

    public static TheoryData<string> Engines => ["sqlserver", "postgres"];

    /// <summary>
    /// On a real database: <c>discover</c> in <c>dev</c> answers a table's columns, its primary key and its foreign key and the empty schema, and no value of a row; <c>describe</c> of a <c>SELECT</c> answers its columns and refuses an
    /// <c>UPDATE</c> before anything reaches the database; and a <c>discover</c> in <c>prod</c> asked for Bob runs, its run recorded with Bob's name.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task Discover_and_describe_read_shape_only_through_the_projects_binding_and_a_read_in_production_runs_for_the_person_named(string engine)
    {
        await using var database = TestDatabase.Create(engine);
        if (database is null) return;
        Environment.SetEnvironmentVariable(Variable, database.Password);
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite, channels: ["dev", "prod"]);
            await Bind(h, "dev", "prod");
            Directory.CreateDirectory(Path.Combine(h.Directory, "connections"));
            foreach (var channel in new[] { "dev", "prod" })
                File.WriteAllText(Path.Combine(h.Directory, "connections", $"erp_{channel}.yaml"), database.ConnectionFile($"erp_{channel}", Variable));
            await h.StartAsync();

            // discover in dev: the shape, the empty schema, the keys, and no value
            var discovered = await RunAsync(h, "dev", "discover", Read(), "ada");
            var answer = JsonNode.Parse(await PayloadAsync(h, "dev", "discover", discovered))!.AsObject();
            Assert.DoesNotContain(RowValue, answer.ToJsonString());
            Assert.Contains(answer["databases"]!.AsArray(), d => d!["schemas"]!.AsArray().Any(s => (string?)s == "staging"));
            var tables = answer["tables"]!.AsArray().Select(t => t!.AsObject()).ToList();
            Assert.DoesNotContain(tables, t => (string?)t["schema"] == "staging");
            var orders = Assert.Single(tables, t => (string?)t["schema"] == "crm" && (string?)t["name"] == "orders");
            var columns = orders["columns"]!.AsArray().Select(c => ((string)c!["name"]!, (bool)c["key"]!, (bool)c["nullable"]!)).ToList();
            Assert.Equal([("id", true, false), ("customer_id", false, false), ("note", false, true)], columns);
            var foreign = Assert.Single(orders["foreign_keys"]!.AsArray())!;
            Assert.Equal(("customers", "customer_id", "id"), ((string)foreign["references"]!["table"]!, (string)foreign["columns"]![0]!, (string)foreign["references"]!["columns"]![0]!));
            Assert.Contains(orders["unique_keys"]!.AsArray(), k => (string?)k!["columns"]![0] == "note");
            // the answer is a connection of catalog-cache/2, as the Workspace's cache reads it
            var cached = DbDataKit.Catalog.CatalogCache.FromAnswer(answer, new DbDataKit.Catalog.CacheRead("runners", "runner", "dev", "2026-11-22T10:00:00Z"));
            Assert.Contains(cached.Tables, t => t.Name == "customers");

            // one table of one schema
            var one = await RunAsync(h, "dev", "discover", Read(inputs: new { schemas = new[] { "crm" }, table = "customers" }), "ada");
            var narrowed = JsonNode.Parse(await PayloadAsync(h, "dev", "discover", one))!["tables"]!.AsArray();
            Assert.Equal("customers", (string?)Assert.Single(narrowed)!["name"]);

            // describe: a SELECT's columns, and an UPDATE refused by the read-only policy
            var described = await RunAsync(h, "dev", "describe", Read(inputs: new { sql = "SELECT id, note FROM crm.orders" }), "ada");
            var shape = await StepAsync(h, "dev", "describe", described);
            Assert.Equal(["id", "note"], shape["result"]!["columns"]!.AsArray().Select(c => (string)c!["name"]!).ToArray());
            var refused = await RunAsync(h, "dev", "describe", Read(inputs: new { sql = "UPDATE crm.orders SET note = 'changed'" }), "ada", succeed: false);
            Assert.Contains("read-only", (string)refused["failure"]!["message"]!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(RowValue, database.Scalar("SELECT note FROM crm.orders WHERE id = 1"));

            // production: anyone the console lets ask may read, and the run names the person
            var production = await RunAsync(h, "prod", "discover", Read(person: "Bob via Main console"), "bob");
            Assert.Contains("orders", await PayloadAsync(h, "prod", "discover", production));
            var started = h.Audit.Read(new AuditQuery(Verb: RunnerControl.RunVerb, Limit: 50)).Single(e => (e.Entry.Detail ?? "").Contains((string)production["id"]!));
            Assert.Equal($"{MetadataPackage.Name}/prod/discover", started.Entry.TargetId);
            Assert.Equal("bob", started.Entry.ActorId);
            Assert.Contains("\"on_behalf_of\":\"Bob via Main console\"", started.Entry.Detail);
            Assert.DoesNotContain(database.Password, string.Join('\n', h.Log));
        }
        finally { Environment.SetEnvironmentVariable(Variable, null); }
    }

    /// <summary>
    /// On a real database: a draft that was never published (the runner holds no version of <c>sales-dw</c>) with its slot <c>warehouse</c> bound in <c>dev</c>: <c>discover</c> reads its tables and the binding test opens the connection.
    /// </summary>
    [Theory]
    [MemberData(nameof(Engines))]
    public async Task A_never_published_drafts_slot_bound_in_dev_is_discovered_and_its_binding_test_connects(string engine)
    {
        await using var database = TestDatabase.Create(engine);
        if (database is null) return;
        Environment.SetEnvironmentVariable(Variable, database.Password);
        try
        {
            await using var h = new RunnerHarness(StateEngineIds.Sqlite, channels: ["dev", "prod"]);
            Directory.CreateDirectory(Path.Combine(h.Directory, "connections"));
            File.WriteAllText(Path.Combine(h.Directory, "connections", "warehouse_dev.yaml"), database.ConnectionFile("warehouse_dev", Variable));
            new RunnerPublishing(h.State).Bind("sales-dw", "dev", new Dictionary<string, string> { ["warehouse"] = "warehouse_dev" }, null, RunnerActor.Local());
            await h.StartAsync();

            var run = await RunAsync(h, "dev", "discover", new { package = "sales-dw", slot = "warehouse", on_behalf_of = "Ada via Main console" }, "ada");
            var answer = JsonNode.Parse(await PayloadAsync(h, "dev", "discover", run))!.AsObject();
            Assert.Contains(answer["tables"]!.AsArray(), t => (string?)t!["schema"] == "crm" && (string?)t["name"] == "orders");
            Assert.DoesNotContain(RowValue, answer.ToJsonString());

            var (tested, result) = await h.PostAsync("/api/v1/bindings/dev/integration/sales-dw/test", ServerRole.Admin, new { slot = "warehouse" });
            Assert.Equal(HttpStatusCode.OK, tested);
            Assert.True((bool)result!["ok"]!, result.ToJsonString());
            Assert.True((bool)result["connected"]!);
            Assert.Empty(new RunnerPublishing(h.State).Publisher.HeadsOf(RunnerPublishing.Kind, "sales-dw"));
        }
        finally { Environment.SetEnvironmentVariable(Variable, null); }
    }

    /// <summary>Starts a read and waits for its run to end; returns the run as <c>GET /api/v1/runs/{id}</c> answers it.</summary>
    private static async Task<JsonObject> RunAsync(RunnerHarness h, string channel, string task, object body, string subject, bool succeed = true)
    {
        var (status, accepted) = await h.PostAsync($"/api/v1/metadata/{channel}/{task}", ServerRole.Operator, body, subject);
        Assert.True(status == HttpStatusCode.Accepted, accepted?.ToJsonString());
        var id = (string)accepted!["run_id"]!;
        var run = await h.WaitAsync(async () =>
        {
            var (_, found) = await h.GetAsync($"/api/v1/runs/{id}");
            return found is JsonObject o && o["finished"] is not null ? o : null;
        }, $"the run of {task} in {channel} to end", 120);
        if ((string?)run["outcome"] != (succeed ? "succeeded" : "failed"))
        {
            var (_, lines) = await h.GetAsync($"/api/v1/runs/{id}/logs");
            Assert.Fail(run.ToJsonString() + "\n" + lines?.ToJsonString());
        }

        return run;
    }

    /// <summary>The step of the run's answer, found by the run's id among the task's steps.</summary>
    private static async Task<JsonObject> StepAsync(RunnerHarness h, string channel, string task, JsonObject run)
    {
        var (status, steps) = await h.GetAsync($"/api/v1/tasks/{MetadataPackage.Name}/{channel}/{task}/steps/{task}?key={(string)run["id"]!}");
        Assert.Equal(HttpStatusCode.OK, status);
        return Assert.Single(steps!["steps"]!.AsArray())!.AsObject();
    }

    private static async Task<string> PayloadAsync(RunnerHarness h, string channel, string task, JsonObject run)
    {
        var step = await StepAsync(h, channel, task, run);
        var payload = (string)step["payload"]!;
        var (status, body) = await h.GetAsync($"/api/v1/tasks/{MetadataPackage.Name}/{channel}/{task}/steps/{task}/payloads/{payload}");
        Assert.Equal(HttpStatusCode.OK, status);
        return body!.ToJsonString();
    }

    /// <summary>A database of its own on the shared engine, with the schemas <c>crm</c> (two tables, a key, a foreign key, a unique key, one row) and <c>staging</c> (empty); dropped at the end.</summary>
    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _engine;
        private readonly string _server;
        private readonly string _name;

        private TestDatabase(string engine, string server, string name)
        {
            _engine = engine;
            _server = server;
            _name = name;
        }

        public string Password => _engine == "sqlserver" ? new SqlConnectionStringBuilder(_server).Password : new NpgsqlConnectionStringBuilder(_server).Password!;

        public static TestDatabase? Create(string engine)
        {
            var server = Environment.GetEnvironmentVariable(engine == "sqlserver" ? "DBDATAKIT_TEST_SQLSERVER" : "DBDATAKIT_TEST_POSTGRES");
            if (string.IsNullOrWhiteSpace(server))
            {
                Assert.False(string.Equals(Environment.GetEnvironmentVariable(engine == "sqlserver" ? "DBDATAKIT_TEST_REQUIRE_SQLSERVER" : "DBDATAKIT_TEST_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase), $"{engine} is required");
                Assert.Skip($"set DBDATAKIT_TEST_{(engine == "sqlserver" ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
                return null;
            }

            var database = new TestDatabase(engine, server, $"metadata_{Guid.NewGuid():N}");
            database.Execute(null, engine == "sqlserver" ? $"CREATE DATABASE [{database._name}];" : $"CREATE DATABASE {database._name};");
            const string notes = "note {0} NULL UNIQUE";
            database.Execute(database._name,
                "CREATE SCHEMA crm;",
                "CREATE SCHEMA staging;",
                $"CREATE TABLE crm.customers (id int NOT NULL PRIMARY KEY, name {(engine == "sqlserver" ? "nvarchar(50)" : "varchar(50)")} NOT NULL);",
                $"CREATE TABLE crm.orders (id int NOT NULL PRIMARY KEY, customer_id int NOT NULL REFERENCES crm.customers (id), {string.Format(System.Globalization.CultureInfo.InvariantCulture, notes, engine == "sqlserver" ? "nvarchar(100)" : "varchar(100)")});",
                $"INSERT INTO crm.customers (id, name) VALUES (1, 'Contoso');",
                $"INSERT INTO crm.orders (id, customer_id, note) VALUES (1, 1, '{RowValue}');");
            return database;
        }

        /// <summary>The runner's <c>connection/1</c> file for the database, its password a secret reference to <paramref name="variable"/>.</summary>
        public string ConnectionFile(string name, string variable)
        {
            if (_engine == "sqlserver")
            {
                var b = new SqlConnectionStringBuilder(_server);
                return $"schema: connection/1\nname: {name}\nengine: sqlserver\nconnection_string: 'Server={b.DataSource};Database={_name}'\ntls: {{ encrypt: false, trust_server_certificate: true }}\nauth: {{ mode: sql, user: {b.UserID}, password_secret: 'env:{variable}' }}\n";
            }

            var p = new NpgsqlConnectionStringBuilder(_server);
            return $"schema: connection/1\nname: {name}\nengine: postgres\nconnection_string: 'Host={p.Host};Port={p.Port};Database={_name}'\ntls: {{ encrypt: false }}\nauth: {{ mode: sql, user: {p.Username}, password_secret: 'env:{variable}' }}\n";
        }

        public string? Scalar(string sql)
        {
            using var connection = Open(_name);
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar() as string;
        }

        private System.Data.Common.DbConnection Open(string? database)
        {
            System.Data.Common.DbConnection connection = _engine == "sqlserver"
                ? new SqlConnection(new SqlConnectionStringBuilder(_server) { InitialCatalog = database ?? "master" }.ConnectionString)
                : new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server) { Database = database ?? "postgres" }.ConnectionString);
            connection.Open();
            return connection;
        }

        private void Execute(string? database, params string[] statements)
        {
            using var connection = Open(database);
            foreach (var sql in statements)
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                if (_engine == "sqlserver")
                {
                    SqlConnection.ClearAllPools();
                    Execute(null, $"ALTER DATABASE [{_name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_name}];");
                }
                else
                {
                    NpgsqlConnection.ClearAllPools();
                    Execute(null, $"DROP DATABASE IF EXISTS {_name} WITH (FORCE);");
                }
            }
            catch (Exception ex) when (ex is SqlException or NpgsqlException) { /* a test database left behind is dropped with the engine */ }

            return ValueTask.CompletedTask;
        }
    }
}
