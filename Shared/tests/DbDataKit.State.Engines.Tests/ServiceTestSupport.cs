using System.Data.Common;
using System.Text;
using DbDataKit.Monitoring;
using DbDataKit.Secrets;
using DbDataKit.State.Engines.Snapshots;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DbDataKit.State.Tests;

/// <summary>
/// Scratch databases for the snapshot-service tests: SQLite files always, and a scratch database on SQL Server and PostgreSQL when the shared engines are up (<c>DBDATAKIT_TEST_SQLSERVER</c>,
/// <c>DBDATAKIT_TEST_POSTGRES</c>). A server endpoint's connection string holds a <b>secret reference</b> (<c>Password=secret:...</c>) and the resolver gives the real password, so every test also shows that the value
/// does not leak.
/// </summary>
public sealed class ServiceScratch : IDisposable
{
    private readonly List<Action> _cleanups = [];
    private readonly Dictionary<string, string> _secretValues = [];
    private int _counter;

    public ServiceScratch()
    {
        Directory = System.IO.Directory.CreateTempSubdirectory("state-service-").FullName;
        Resolver = new SecretResolverChain([new EnvSecretProvider(name => _secretValues.GetValueOrDefault(name), secretPrefix: "SCRATCH_")]);
    }

    public string Directory { get; }

    public ISecretResolver Resolver { get; }

    /// <summary>Every password the scratch endpoints use, as plain text, for the tests that search for them.</summary>
    public IReadOnlyCollection<string> Passwords => _secretValues.Values;

    public static TheoryData<string, string> Pairs
    {
        get
        {
            var data = new TheoryData<string, string>();
            foreach (var source in new[] { "sqlite", "postgres", "mssql" })
            {
                foreach (var target in new[] { "sqlite", "postgres", "mssql" }) data.Add(source, target);
            }

            return data;
        }
    }

    public static TheoryData<string> Engines => ["sqlite", "postgres", "mssql"];

    public static string Id(string engine) => engine switch { "mssql" => StateEngineIds.MsSql, "postgres" => StateEngineIds.Postgres, _ => StateEngineIds.Sqlite };

    public static string? Server(string engine) => engine switch
    {
        "mssql" => Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER"),
        "postgres" => Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES"),
        _ => null,
    };

    /// <summary>A new empty database on the engine, as an endpoint a product would be configured with.</summary>
    public StateEndpoint Provision(string engine, string? passwordOverride = null)
    {
        var n = ++_counter;
        if (engine == "sqlite") return new StateEndpoint(new StateSettings("sqlite"), Path.Combine(Directory, $"state-{n}.db"));

        var server = Server(engine);
        if (server is null)
        {
            Assert.False(string.Equals(Environment.GetEnvironmentVariable(engine == "mssql" ? "DBDATAKIT_TEST_REQUIRE_SQLSERVER" : "DBDATAKIT_TEST_REQUIRE_POSTGRES"), "true", StringComparison.OrdinalIgnoreCase), $"{engine} is required but its DBDATAKIT_TEST_* variable is not set");
            Assert.Skip($"set DBDATAKIT_TEST_{(engine == "mssql" ? "SQLSERVER" : "POSTGRES")} to run on {engine}");
        }

        var name = $"dbdatakit_svc_{Guid.NewGuid():N}";
        // opening any endpoint registers the provider factories with the dialect registry
        StateOpener.OpenWithoutMigrating(new StateSettings(engine, server), null, []).Dialect.ToString();
        Execute(Id(engine), server!, engine == "mssql" ? $"CREATE DATABASE [{name}];" : $"CREATE DATABASE {name};");
        _cleanups.Add(() =>
        {
            if (engine == "mssql")
            {
                SqlConnection.ClearAllPools();
                Execute(Id(engine), server!, $"ALTER DATABASE [{name}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{name}];");
            }
            else
            {
                NpgsqlConnection.ClearAllPools();
                Execute(Id(engine), server!, $"DROP DATABASE IF EXISTS {name} WITH (FORCE);");
            }
        });

        DbConnectionStringBuilder builder = engine == "mssql" ? new SqlConnectionStringBuilder(server) { InitialCatalog = name } : new NpgsqlConnectionStringBuilder(server) { Database = name };
        var password = builder["Password"]!.ToString()!;
        var secretName = $"state-{engine}-{n}";
        _secretValues[$"SCRATCH_{secretName.Replace('-', '_').ToUpperInvariant()}"] = passwordOverride ?? password;
        builder["Password"] = $"secret:{secretName}";
        return new StateEndpoint(new StateSettings(engine, builder.ConnectionString));
    }

    /// <summary>The engine's real connection string for an endpoint (the reference resolved), to look at or damage a database outside the state layer.</summary>
    public string Resolved(StateEndpoint endpoint) => endpoint.Settings.IsSqlite ? "" : StateOpenerResolve(endpoint);

    private string StateOpenerResolve(StateEndpoint endpoint)
    {
        var text = endpoint.Settings.ConnectionString!;
        foreach (var (key, value) in _secretValues)
        {
            var secret = key["SCRATCH_".Length..].ToLowerInvariant().Replace('_', '-');
            text = text.Replace($"secret:{secret}", value, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>Runs a statement on the scratch database outside the state layer.</summary>
    public object? Scalar(StateEndpoint endpoint, string sql)
    {
        var engine = endpoint.Engine;
        using var connection = StateDialect.For(engine).CreateConnection(engine == StateEngineIds.Sqlite ? $"Data Source={endpoint.SqliteFile};Pooling=False" : Resolved(endpoint));
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static void Execute(string engine, string connectionString, string sql)
    {
        using var connection = StateDialect.For(engine).CreateConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>Every text a test could be shown: all files (table files uncompressed, others as Latin-1) under the directories, for a search for something that must not be there.</summary>
    public static string EverythingUnder(params string[] directories)
    {
        var text = new StringBuilder();
        foreach (var directory in directories.Where(System.IO.Directory.Exists))
        {
            foreach (var path in System.IO.Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                var bytes = File.ReadAllBytes(path);
                if (path.EndsWith(".gz", StringComparison.Ordinal))
                {
                    using var gzip = new System.IO.Compression.GZipStream(new MemoryStream(bytes), System.IO.Compression.CompressionMode.Decompress);
                    using var reader = new StreamReader(gzip, Encoding.UTF8);
                    text.Append(reader.ReadToEnd());
                }
                else
                {
                    text.Append(Encoding.Latin1.GetString(bytes));
                }
            }
        }

        return text.ToString();
    }

    public void Dispose()
    {
        foreach (var cleanup in _cleanups)
        {
            try { cleanup(); }
            catch (Exception) { /* a scratch database left behind is untidy; the name carries a GUID */ }
        }

        SqliteCleanup();
        try { System.IO.Directory.Delete(Directory, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a temp folder left behind is untidy and nothing else */ }
    }

    private static void SqliteCleanup() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
}

/// <summary>The modules the service tests move, and a fill that uses the real stores.</summary>
internal static class ServiceData
{
    /// <summary>One table of every logical type, a composite key and an older identity key (the same shape the State suite uses).</summary>
    public static StateModule Types { get; } = new(
        "svc_types",
        StateScopeKind.Install,
        [
            """
            CREATE TABLE {table:sample} (
                id         {{uuid}} PRIMARY KEY,
                label      {{key}} NOT NULL,
                note       {{text}} NULL,
                created_at {{time}} NOT NULL,
                enabled    {{bool}} NOT NULL,
                amount     {{decimal}} NULL,
                doc        {{json}} NULL,
                data       {{blob}} NULL,
                size       {{int}} NOT NULL
            );
            CREATE TABLE {table:counter} (
                {{identity:id}},
                name {{key}} NOT NULL
            );
            """,
        ]);

    /// <summary>The Proxy's instance rows, with a lease of their own: a row inside its lease is a live instance.</summary>
    public static StateModule Runner { get; } = new(
        "runner",
        StateScopeKind.Group,
        [
            """
            CREATE TABLE {table:instance} (
                id                {{key}} PRIMARY KEY,
                host              {{key}} NOT NULL,
                lease_expires_at  {{time}} NOT NULL
            );
            """,
        ]);

    public static readonly StateModule[] Modules = [IdentityModule.Module, RunsModule.Module, PayloadStore.Module, Types];

    public const string Marker = "sample-note-é中";

    /// <summary>Fills a database through the real stores; returns the id of a payload of several chunks and its bytes.</summary>
    public static (Guid Payload, byte[] Bytes) Fill(StateDatabase database)
    {
        var users = new IdentityUserStore(database);
        var ada = users.CreateUser("Ada Lovelace", "ada@example.invalid", UserRole.Admin);
        users.AddCredential(ada.Id, CredentialMethods.Passkey, "cred-1", secret: "public-key");
        users.CreateUser("Grace Hopper", null, UserRole.Viewer);

        var runs = new RunStore(database, "dbdatasync");
        var run = runs.Start("orders-sync");
        for (var i = 1; i <= 30; i++) runs.Log(run, i % 10 == 0 ? "Error" : "Info", $"line {i}", item: i % 10 == 0 ? "dbo.Orders" : null);
        runs.Finish(run, RunOutcome.Failed, rows: 1200, failedItem: "dbo.Orders", failure: new RunFailure("Timeout", "the source did not answer"));

        var payloads = new PayloadStore(database, chunkSize: 1024);
        var data = new byte[5000];
        new Random(5).NextBytes(data);
        Guid id;
        using (var writer = payloads.OpenWrite(new PayloadOptions(MediaType: "application/octet-stream")))
        {
            id = writer.Id;
            writer.Write(data);
            writer.Complete();
        }

        using (var unfinished = payloads.OpenWrite()) unfinished.Write(data.AsSpan(0, 1500));

        using var connection = database.OpenConnection();
        for (var i = 1; i <= 12; i++)
        {
            using var insert = database.Command("svc_types", connection, "INSERT INTO {table:sample} (id, label, note, created_at, enabled, amount, doc, data, size) VALUES ($id, $label, $note, $at, $enabled, $amount, CAST($doc AS {{json}}), $data, $size);");
            insert.Bind(database, "id", Guid.NewGuid())
                .Bind(database, "label", $"label-{i}")
                .Bind(database, "note", i % 3 == 0 ? null : Marker, System.Data.DbType.String)
                .Bind(database, "at", DateTimeOffset.Parse("2026-11-04T09:15:00.1234567Z").AddMinutes(i))
                .Bind(database, "enabled", i % 2 == 0)
                .Bind(database, "amount", 12.5m + i, System.Data.DbType.Decimal)
                .Bind(database, "doc", "{\"b\":1,\"a\":[1,2]}", System.Data.DbType.String)
                .Bind(database, "data", new byte[] { 1, 2, (byte)i }, System.Data.DbType.Binary)
                .Bind(database, "size", (long)i);
            insert.ExecuteNonQuery();
        }

        using var counter = database.Command("svc_types", connection, "INSERT INTO {table:counter} (name) VALUES ($name);");
        counter.Bind(database, "name", "first");
        counter.ExecuteNonQuery();
        return (id, data);
    }
}
