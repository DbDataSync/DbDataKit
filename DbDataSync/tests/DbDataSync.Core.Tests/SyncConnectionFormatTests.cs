using DbDataKit.Config.Git;
using DbDataKit.Connections;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using LibGit2Sharp;
using AuthMode = DbDataSync.Core.Config.AuthMode;

namespace DbDataSync.Core.Tests;

/// <summary>Plan 13, E01: a Sync connection is a <c>connection/1</c> file, with what only Sync has under <c>extensions.sync</c>; the form of earlier releases is refused with a message naming the file.</summary>
public sealed class SyncConnectionFormatTests : IDisposable
{
    private static readonly GitAuthor Author = new("T", "t@example.com");
    private readonly string _repoRoot = Directory.CreateTempSubdirectory("dbdatasync-connfmt-").FullName;
    private readonly string _configRoot;
    private readonly ConfigRepository _repository;
    private readonly SecretStore _secrets = SecretStore.ForProviders([new InMemorySecretProvider()]);

    public SyncConnectionFormatTests()
    {
        Repository.Init(_repoRoot);
        _configRoot = Path.Combine(_repoRoot, "config");
        _repository = new ConfigRepository(_configRoot, new GitCommitService(_repoRoot), _secrets);
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_repoRoot);

    private string FileOf(string name) => Path.Combine(_configRoot, "connections", name + ".yaml");

    private const string OldForm = """
        schemaVersion: 1
        name: orders
        driverType: MsSql
        host: sql01
        port: 1433
        authMode: SqlAuth
        userId: sync
        credentialSecretRef: dbdata:sync:connection:orders
        """;

    // ---- the new format ----

    [Fact]
    public void A_saved_connection_is_a_connection_1_file()
    {
        _repository.SaveConnection(new ConnectionInput
        {
            Name = "orders", DriverType = DriverIds.MsSql, Host = "sql01", Port = 1433, Database = "Orders",
            AuthMode = AuthMode.SqlAuth, UserId = "sync", Password = "p4ss-sentinel", ConnectTimeoutSeconds = 20, CommandTimeoutSeconds = 600,
        }, Author);

        var text = File.ReadAllText(FileOf("orders"));
        Assert.StartsWith("schema: connection/1", text, StringComparison.Ordinal);
        Assert.Contains("engine: sqlserver", text);
        Assert.Contains("host: sql01", text);
        Assert.Contains("password_secret: secret:dbdata:sync:connection:orders", text);
        Assert.Contains("connect_s: 20", text);
        Assert.Contains("command_s: 600", text);
        Assert.DoesNotContain("p4ss-sentinel", text);
        Assert.DoesNotContain("extensions", text);   // nothing Sync-only to say
        Assert.DoesNotContain("schemaVersion", text);

        // the shared loader, which every product uses, reads what Sync wrote (the console's view across servers, E05, needs that)
        var shared = ConnectionDefinitionLoader.Load(text, "orders.yaml").Require();
        Assert.Equal("sqlserver", shared.Engine);
        Assert.Equal("sql01", shared.Address!.Host);
        Assert.Equal("sync", shared.Auth!.User);
        Assert.Equal("secret:dbdata:sync:connection:orders", shared.Auth.PasswordSecret);
        Assert.Equal(20, shared.Timeouts.ConnectS);
    }

    [Fact]
    public void Every_field_comes_back_as_it_was_saved()
    {
        var input = new ConnectionInput
        {
            Name = "lake.v2", DriverType = DriverIds.ArrowFlightSqlPostgres, ConnectionString = "grpc://proxy:32010", Database = "dw",
            AuthMode = AuthMode.None, ConnectTimeoutSeconds = 0, CommandTimeoutSeconds = 100000, SegmentOrder = SegmentOrder.Together,
            Properties = { ["applicationName"] = "sync", ["TrustServerCertificate"] = "true" },
            TestQuery = "select 1",
            Scripts = { ["transform"] = new ScriptBinding { ScriptName = "s1", Parameters = { ["a"] = "b" } }, ["after"] = null },
            Hooks = { ["beforeLoad"] = [new HookConfig { Sql = "update statistics t", OnError = HookErrorMode.Warn }] },
        };
        _repository.SaveConnection(input, Author);

        var text = File.ReadAllText(FileOf("lake.v2"));
        Assert.Contains("engine: flightsql", text);
        Assert.Contains("dialect: postgres", text);
        Assert.Contains("connection_string: grpc://proxy:32010", text);
        Assert.Contains("mode: none", text);
        Assert.DoesNotContain("timeouts:", text);   // 0 and a day or more are Sync's own, in extensions.sync

        var loaded = _repository.LoadConnection("lake.v2");
        Assert.Equal(DriverIds.ArrowFlightSqlPostgres, loaded.DriverType);
        Assert.Equal("grpc://proxy:32010", loaded.ConnectionString);
        Assert.Null(loaded.Host);
        Assert.Equal("dw", loaded.Database);
        Assert.Equal(AuthMode.None, loaded.AuthMode);
        Assert.Equal(0, loaded.ConnectTimeoutSeconds);
        Assert.Equal(100000, loaded.CommandTimeoutSeconds);
        Assert.Equal(SegmentOrder.Together, loaded.SegmentOrder);
        Assert.Equal("sync", loaded.Properties["applicationName"]);
        Assert.Equal("true", loaded.Properties["TrustServerCertificate"]);
        Assert.Equal("select 1", loaded.TestQuery);
        Assert.Equal("s1", loaded.Scripts["transform"]!.ScriptName);
        Assert.Equal("b", loaded.Scripts["transform"]!.Parameters["a"]);
        Assert.True(loaded.Scripts.ContainsKey("after") && loaded.Scripts["after"] is null);
        var hook = Assert.Single(loaded.Hooks["beforeLoad"]!);
        Assert.Equal("update statistics t", hook.Sql);
        Assert.Equal(HookErrorMode.Warn, hook.OnError);

        // and the shared loader accepts the whole file, Sync's settings kept as an extension it does not read
        var shared = ConnectionDefinitionLoader.Load(text, "lake.v2.yaml").Require();
        Assert.True(shared.Extensions.Items.ContainsKey("sync"));
    }

    [Theory]
    [InlineData(DriverIds.MsSql, "sqlserver", null)]
    [InlineData(DriverIds.Postgres, "postgres", null)]
    [InlineData(DriverIds.MySql, "mysql", null)]
    [InlineData(DriverIds.Oracle, "oracle", null)]
    [InlineData(DriverIds.DuckDb, "duckdb", null)]
    [InlineData(DriverIds.ArrowFlightSql, "flightsql", null)]
    [InlineData(DriverIds.ArrowFlightSqlMsSql, "flightsql", "sqlserver")]
    [InlineData(DriverIds.ArrowFlightSqlMySql, "flightsql", "mysql")]
    [InlineData(DriverIds.ArrowFlightSqlOracle, "flightsql", "oracle")]
    [InlineData(DriverIds.ArrowFlightSqlDuckDb, "flightsql", "duckdb")]
    [InlineData("mysql.generic", "mysql.generic", null)]
    public void Each_driver_id_has_an_engine_and_comes_back(string driver, string engine, string? dialect)
    {
        _repository.SaveConnection(new ConnectionInput { Name = "c1", DriverType = driver, ConnectionString = "x", AuthMode = AuthMode.None }, Author);
        var text = File.ReadAllText(FileOf("c1"));
        Assert.Contains($"engine: {engine}", text);
        Assert.Equal(dialect is not null, text.Contains("dialect:", StringComparison.Ordinal));
        Assert.Equal(driver, _repository.LoadConnection("c1").DriverType);
    }

    [Fact]
    public void A_driver_id_that_would_be_read_back_as_a_built_in_is_refused_at_save()
    {
        var ex = Assert.Throws<ConfigValidationException>(() =>
            _repository.SaveConnection(new ConnectionInput { Name = "c1", DriverType = "postgres", ConnectionString = "x", AuthMode = AuthMode.None }, Author));
        Assert.Contains("postgres", ex.Message);
        Assert.False(File.Exists(FileOf("c1")));
    }

    [Theory]
    [InlineData("1db")]
    [InlineData("_db")]
    [InlineData("a-name-that-is-far-too-long-for-the-shared-pattern-of-sixty-four-chars-x")]
    public void A_name_the_shared_format_cannot_hold_is_refused_at_save(string name)
    {
        var ex = Assert.Throws<ConfigValidationException>(() =>
            _repository.SaveConnection(new ConnectionInput { Name = name, DriverType = DriverIds.MsSql, Host = "h", AuthMode = AuthMode.IntegratedAuth }, Author));
        Assert.Contains("connection/1", ex.Message);
    }

    [Fact]
    public void The_file_name_is_the_name()
    {
        _repository.SaveConnection(new ConnectionInput { Name = "orders", DriverType = DriverIds.MsSql, Host = "h", AuthMode = AuthMode.IntegratedAuth }, Author);
        File.WriteAllText(FileOf("orders"), File.ReadAllText(FileOf("orders")).Replace("name: orders", "name: other"));
        var ex = Assert.Throws<ConfigFileException>(() => _repository.LoadConnection("orders"));
        Assert.Contains("orders.yaml", ex.Message);
        Assert.Contains("`other`", ex.Message);
    }

    [Theory]
    [InlineData("tls: { verify: false }", "tls")]
    [InlineData("access: readonly", "access")]
    [InlineData("managed_by: client-1", "managed_by")]
    [InlineData("read_login: { mode: integrated }", "read_login")]
    [InlineData("connection_string_secret: 'env:CS'", "connection_string_secret")]
    public void A_setting_Sync_does_not_use_is_refused_by_key_never_ignored(string line, string key)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf("orders"))!);
        File.WriteAllText(FileOf("orders"), $"schema: connection/1\nname: orders\nengine: sqlserver\n{(key == "connection_string_secret" ? "" : "address: { host: sql01 }\n")}auth: {{ mode: integrated }}\n{line}\n");
        var ex = Assert.Throws<ConfigFileException>(() => _repository.LoadConnection("orders"));
        Assert.Contains(ex.Diagnostics, d => d.KeyPath == key && d.Message.Contains($"`{key}`", StringComparison.Ordinal));
        Assert.Contains("orders.yaml", ex.Message);
    }

    [Fact]
    public void A_password_that_is_not_in_Syncs_secret_store_is_refused()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf("orders"))!);
        File.WriteAllText(FileOf("orders"), "schema: connection/1\nname: orders\nengine: sqlserver\naddress: { host: sql01 }\nauth: { mode: sql, user: u, password_secret: 'env:ORDERS_PASSWORD' }\n");
        var ex = Assert.Throws<ConfigFileException>(() => _repository.LoadConnection("orders"));
        Assert.Contains(ex.Diagnostics, d => d.KeyPath == "auth.password_secret");
    }

    [Fact]
    public void A_file_written_by_another_product_with_its_own_extension_loads_and_the_extension_is_not_Syncs()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf("erp"))!);
        File.WriteAllText(FileOf("erp"), """
            schema: connection/1
            name: erp
            engine: sqlserver
            address: { host: SQL01, database: Erp }
            auth: { mode: sql, user: reader, password_secret: 'secret:dbdata:sync:connection:erp' }
            timeouts: { command_s: 300 }
            extensions:
              proxy:
                classifier: strict
            """);
        var c = _repository.LoadConnection("erp");
        Assert.Equal(DriverIds.MsSql, c.DriverType);
        Assert.Equal("Erp", c.Database);
        Assert.Equal(300, c.CommandTimeoutSeconds);
        Assert.Null(c.ConnectTimeoutSeconds);   // not set: Sync's own default, not the shared type's
    }

    // ---- the form of earlier releases is refused ----

    [Fact]
    public void A_connection_in_the_old_form_is_refused_with_a_message_naming_the_file_and_the_new_form()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FileOf("orders"))!);
        File.WriteAllText(FileOf("orders"), OldForm);

        var ex = Assert.Throws<ConfigFileException>(() => _repository.LoadConnection("orders"));
        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal("DBD-111", d.Code);
        Assert.Contains("orders.yaml", ex.Message);
        Assert.Contains("no longer reads", ex.Message);
        Assert.Contains("schema: connection/1", ex.Message);
        Assert.Contains("extensions.sync", ex.Message);
        // nothing is converted: the file is as it was
        Assert.Equal(OldForm, File.ReadAllText(FileOf("orders")));
    }

    [Fact]
    public void A_repository_with_connections_in_the_old_form_fails_naming_each_file_and_not_the_good_one()
    {
        _repository.SaveConnection(new ConnectionInput { Name = "good", DriverType = DriverIds.MsSql, Host = "h", AuthMode = AuthMode.IntegratedAuth }, Author);
        File.WriteAllText(FileOf("orders"), OldForm);
        File.WriteAllText(FileOf("crm"), OldForm.Replace("name: orders", "name: crm"));

        var ex = Assert.Throws<ConfigFileException>(() => _repository.LoadAllConnections());
        Assert.Contains("orders.yaml", ex.Message);
        Assert.Contains("crm.yaml", ex.Message);
        Assert.DoesNotContain("good.yaml", ex.Message);
        Assert.Equal(2, ex.Diagnostics.Count);
        Assert.All(ex.Diagnostics, d => Assert.Contains("connection/1", d.Fix ?? ""));
    }

    [Fact]
    public void An_old_form_connection_can_still_be_deleted_and_takes_its_password_with_it_and_a_save_rewrites_it()
    {
        _secrets.Store("dbdata:sync:connection:orders", "pw");
        File.WriteAllText(FileOf("orders"), OldForm);
        File.WriteAllText(FileOf("crm"), OldForm.Replace("name: orders", "name: crm"));
        _repository.SaveConnection(new ConnectionInput { Name = "crm", DriverType = DriverIds.MsSql, Host = "h", AuthMode = AuthMode.IntegratedAuth }, Author);
        Assert.Equal("h", _repository.LoadConnection("crm").Host);   // saved over: new form

        _repository.DeleteConnection("orders", Author);
        Assert.False(File.Exists(FileOf("orders")));
        Assert.False(_secrets.TryResolve("dbdata:sync:connection:orders", out _));
    }
}
