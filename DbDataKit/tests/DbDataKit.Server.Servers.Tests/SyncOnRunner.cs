using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Nodes;
using DbDataKit.Server;
using DbDataKit.Secrets;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// The end-to-end harness of plan 28c E12e: a replication that really is owned by a runner, SQL Server to PostgreSQL, with every piece the programs they are.
/// <list type="bullet">
/// <item>a <b>Sync server</b> (<c>DbDataSync.Cli server run</c>, its own directory; since plan 28c E22 it starts no process of its own) and a <b>runner</b> (<c>dbdatarunner server run</c>, its own directory), both processes;</item>
/// <item>the engine as the runtime package <c>dbdatasync-runtime</c>, installed and active in the channel (the TaskRunner's build output with the manifest <c>scripts/runtime-manifest.sh</c> prints);</item>
/// <item>the replication set published with the real <c>dbdatasync package publish --to runner</c>, its slots bound to the connections the runner knows: <c>connection/1</c> files in the runner's <c>connections</c> directory and the
/// logins as <c>DBDATAKIT_SECRET_*</c> in the runner's environment (plan 28c E02);</item>
/// <item>the Sync API called as Sync's screens call it: through a <b>console</b> in this process that is paired with both servers (<c>PUT /api/runner</c>), because the API takes the console's signed token and a
/// real server is what the move has to be proved against; the runner is read the same way.</item>
/// </list>
/// </summary>
internal sealed class SyncOnRunner : IAsyncDisposable
{
    public const string Package = "e12e-set";
    public const string Channel = "prod";
    public const string RuntimePackage = "dbdatasync-runtime";

    private readonly List<IAsyncDisposable> started = [];
    private readonly List<string> sqlServerDatabases = [];
    private readonly List<string> postgresDatabases = [];
    private WebApplication console = null!;
    private HttpClient http = null!;
    private string personId = null!;

    public required string SqlServer { get; init; }

    public required string Postgres { get; init; }

    public string Root { get; } = Path.Combine(Path.GetTempPath(), "ddk-e12e-" + Guid.NewGuid().ToString("N")[..8]);

    public string SyncDirectory => Path.Combine(Root, "sync");

    public string RunnerDirectory => Path.Combine(Root, "runner");

    public string ClientDirectory => Path.Combine(Root, "sync-client");

    public string SourceDatabase { get; } = "e12e_src_" + Guid.NewGuid().ToString("N")[..10];

    public string TargetDatabase { get; } = "e12e_tgt_" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>The second SQL Server database, the target of <c>inc</c>: PostgreSQL has no upsert writer for incremental passes, so the replication that proves positions and a merge writer is SQL Server to SQL Server.</summary>
    public string DwDatabase { get; } = "e12e_dw_" + Guid.NewGuid().ToString("N")[..10];

    public Uri SyncUrl { get; private set; } = null!;

    public Uri RunnerUrl { get; private set; } = null!;

    public string SyncId { get; private set; } = null!;

    public string RunnerId { get; private set; } = null!;

    public RealProcess SyncProcess { get; private set; } = null!;

    public RealProcess RunnerProcess { get; private set; } = null!;

    public static string? EnginesMissing =>
        Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER") is null || Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES") is null
            ? "set DBDATAKIT_TEST_SQLSERVER and DBDATAKIT_TEST_POSTGRES (testenv/testenv.sh up mssql postgres; eval \"$(testenv/testenv.sh env dbdatakit)\") to run a replication on a real runner"
            : null;

    /// <summary>The replications of the loads story (<c>SyncOnRunnerLoadsTests</c>) beside the three of the move: <c>bulk</c> (SQL Server to SQL Server, Change Tracking and merge, for a reload in segments), <c>ver</c> (the same pair, periodic, its mapping has a verification check).</summary>
    public bool Extras { get; init; }

    private string[] Tables => Extras ? ["cont", "per", "inc", "bulk", "ver"] : ["cont", "per", "inc"];

    /// <summary>The replications whose target is the SQL Server database <see cref="DwDatabase"/> (the others write to PostgreSQL).</summary>
    private static bool IsDw(string table) => table is "inc" or "bulk" or "ver";

    public static async Task<SyncOnRunner> StartAsync(bool extras = false)
    {
        var harness = new SyncOnRunner { Extras = extras, SqlServer = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER")!, Postgres = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES")! };
        try { await harness.InitializeAsync(); }
        catch { await harness.DisposeAsync(); throw; }
        return harness;
    }

    // ---- the databases

    private SqlConnectionStringBuilder SqlServerLogin => new(SqlServer);

    private NpgsqlConnectionStringBuilder PostgresLogin => new(Postgres);

    public string SourceConnectionString => new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = SourceDatabase }.ConnectionString;

    public string TargetConnectionString => new NpgsqlConnectionStringBuilder(Postgres) { Database = TargetDatabase }.ConnectionString;

    public string DwConnectionString => new SqlConnectionStringBuilder(SqlServer) { InitialCatalog = DwDatabase }.ConnectionString;

    private async Task CreateDatabasesAsync(params string[] tables)
    {
        await using (var server = new SqlConnection(SqlServer))
        {
            await server.OpenAsync();
            await Exec(server, $"CREATE DATABASE [{SourceDatabase}]; ALTER DATABASE [{SourceDatabase}] SET CHANGE_TRACKING = ON (CHANGE_RETENTION = 2 DAYS, AUTO_CLEANUP = OFF);");
            sqlServerDatabases.Add(SourceDatabase);
            await Exec(server, $"CREATE DATABASE [{DwDatabase}];");
            sqlServerDatabases.Add(DwDatabase);
        }

        await using (var server = new NpgsqlConnection(Postgres))
        {
            await server.OpenAsync();
            await Exec(server, $"CREATE DATABASE {TargetDatabase}");
            postgresDatabases.Add(TargetDatabase);
        }

        await using var source = new SqlConnection(SourceConnectionString);
        await source.OpenAsync();
        await using var target = new NpgsqlConnection(TargetConnectionString);
        await target.OpenAsync();
        await using var dw = new SqlConnection(DwConnectionString);
        await dw.OpenAsync();
        foreach (var table in tables)
        {
            await Exec(source, $"CREATE TABLE dbo.[{table}_src] (id INT NOT NULL PRIMARY KEY, name NVARCHAR(50) NOT NULL); ALTER TABLE dbo.[{table}_src] ENABLE CHANGE_TRACKING;");
            if (IsDw(table)) await Exec(dw, $"CREATE TABLE dbo.{table}_tgt (id INT NOT NULL PRIMARY KEY, name NVARCHAR(50) NOT NULL)");
            else await Exec(target, $"CREATE TABLE public.{table}_tgt (id integer PRIMARY KEY, name text NOT NULL)");
        }
    }

    private static async Task Exec(DbConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.CommandTimeout = 300;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Inserts <paramref name="count"/> rows with the ids from <paramref name="from"/> on into the source table of <paramref name="table"/>.</summary>
    public async Task InsertAsync(string table, int from, int count)
    {
        await using var source = new SqlConnection(SourceConnectionString);
        await source.OpenAsync();
        await Exec(source, $"""
            INSERT INTO dbo.[{table}_src] (id, name)
            SELECT TOP ({count}) {from - 1} + ROW_NUMBER() OVER (ORDER BY (SELECT NULL)), CONCAT(N'row-', {from - 1} + ROW_NUMBER() OVER (ORDER BY (SELECT NULL)))
            FROM sys.all_objects a CROSS JOIN sys.all_objects b CROSS JOIN sys.all_objects c;
            """);
    }

    /// <summary>Runs a statement against the SQL Server database the <c>dw</c> connection points at (the target of <c>inc</c>, <c>bulk</c>, <c>ver</c> and <c>loadfail</c>).</summary>
    public async Task ExecuteDwAsync(string sql)
    {
        await using var dw = new SqlConnection(DwConnectionString);
        await dw.OpenAsync();
        await Exec(dw, sql);
    }

    public async Task<long> SourceCountAsync(string table)
    {
        await using var source = new SqlConnection(SourceConnectionString);
        await source.OpenAsync();
        await using var command = source.CreateCommand();
        command.CommandText = $"SELECT COUNT_BIG(*) FROM dbo.[{table}_src]";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>What the target table of <paramref name="table"/> holds: the number of rows and of distinct ids (a row twice would show as a difference; the key forbids it, so a duplicate fails the writer instead).</summary>
    public async Task<(long Rows, long Distinct)> TargetAsync(string table)
    {
        if (IsDw(table))
        {
            await using var dw = new SqlConnection(DwConnectionString);
            await dw.OpenAsync();
            await using var query = dw.CreateCommand();
            query.CommandText = $"SELECT COUNT_BIG(*), COUNT_BIG(DISTINCT id) FROM dbo.{table}_tgt";
            await using var rows = await query.ExecuteReaderAsync();
            await rows.ReadAsync();
            return (rows.GetInt64(0), rows.GetInt64(1));
        }

        await using var target = new NpgsqlConnection(TargetConnectionString);
        await target.OpenAsync();
        await using var command = target.CreateCommand();
        command.CommandText = $"SELECT COUNT(*), COUNT(DISTINCT id) FROM public.{table}_tgt";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return (reader.GetInt64(0), reader.GetInt64(1));
    }

    // ---- the programs

    private readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

    /// <summary>Timings of the set-up steps, for a failure's message and for whoever tunes the job's time.</summary>
    public List<string> Steps { get; } = [];

    public void Step(string what) => Steps.Add($"{clock.Elapsed.TotalSeconds:0.0} s: {what}");

    private async Task InitializeAsync()
    {
        var (sync, runner) = (Programs.Sync, Programs.Runner);
        Directory.CreateDirectory(SyncDirectory);
        Directory.CreateDirectory(RunnerDirectory);
        await CreateDatabasesAsync(Tables);
        Step("databases");

        // the runtime package, installed and active before the runner starts (the development path, in the runner's own directory)
        var runtime = Path.Combine(Root, "runtime");
        var bin = Path.Combine(runtime, "files", "bin");
        CopyDirectory(Programs.EngineBuildOutput, bin);
        File.WriteAllText(Path.Combine(runtime, "package.yaml"), RuntimeManifest());
        await RealProcess.RunAsync(runner, "package", "install", Path.Combine(runtime, "package.yaml"), Path.Combine(runtime, "files"), "--channel", Channel, "--repo", RunnerDirectory);

        Step("runtime package installed");

        // the Sync server, with a certificate made here (a server must present one to be paired with)
        var certificate = Path.Combine(Root, "sync.pfx");
        using (var key = RSA.Create(2048))
        {
            var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var names = new SubjectAlternativeNameBuilder();
            names.AddDnsName("localhost");
            names.AddIpAddress(IPAddress.Loopback);
            request.CertificateExtensions.Add(names.Build());
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            File.WriteAllBytes(certificate, made.Export(X509ContentType.Pfx, "servers-e2e"));
        }

        var syncCode = Programs.CodeIn(await RealProcess.RunAsync(sync, "server", "pair", "--repo", SyncDirectory, "--max-role", "admin"));
        SyncUrl = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
        SyncProcess = RealProcess.Start(sync, ["server", "run", "--repo", SyncDirectory, "--url", SyncUrl.AbsoluteUri.TrimEnd('/')], environment: new Dictionary<string, string>
        {
            ["Kestrel__Certificates__Default__Path"] = certificate, ["Kestrel__Certificates__Default__Password"] = "servers-e2e",
        });
        started.Add(SyncProcess);
        await SyncProcess.WaitUntilAsync(new Uri(SyncUrl, "/api/health"), TimeSpan.FromSeconds(120));

        Step("Sync is up");

        // the console, here: paired with Sync, so it can speak for a signed-in person to it
        var consoleDirectory = Path.Combine(Root, "console");
        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()), Timeout = TimeSpan.FromMinutes(8) };   // a cutover or a rollback waits for the work in flight
        var instances = console.Services.GetRequiredService<ConsoleInstances>();
        SyncId = (await instances.PairAsync(SyncUrl, syncCode, "Sync")).InstanceId;
        personId = console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Admin", null, UserRole.Admin).Id;

        // the logins and the definitions, made on Sync as its screens make them
        await CreateConnectionsAsync();
        await CreateReplicationAsync("cont", new { mode = "Continuous", frequencySeconds = 2, idleTimeoutSeconds = 5 }, "tgt");
        await CreateReplicationAsync("per", new { mode = "Periodic", cronExpression = "0 3 * * *" }, "tgt");   // never due while the test runs: it runs when it is asked to
        await CreateReplicationAsync("inc", new { mode = "Continuous", frequencySeconds = 2, idleTimeoutSeconds = 5 }, "dw");
        if (Extras)
        {
            await CreateReplicationAsync("bulk", new { mode = "Continuous", frequencySeconds = 2, idleTimeoutSeconds = 5 }, "dw");
            await CreateReplicationAsync("ver", new { mode = "Periodic", cronExpression = "0 3 * * *" }, "dw", verify: true);
        }

        Step("connections and replications made");

        // the runner: the connections it knows are the files Sync has, and the logins reach it as environment secrets (plan 28c E02)
        var connections = Path.Combine(RunnerDirectory, "connections");
        Directory.CreateDirectory(connections);
        var environment = new Dictionary<string, string>();
        foreach (var (name, password) in new[] { ("src", SqlServerLogin.Password), ("tgt", PostgresLogin.Password!), ("dw", SqlServerLogin.Password) })
        {
            File.Copy(Path.Combine(SyncDirectory, "config", "connections", name + ".yaml"), Path.Combine(connections, name + ".yaml"));
            environment[EnvSecretProvider.VariableNameFor("dbdatakit:sync:connection:" + name)] = password;
        }

        var consoleCode = Programs.CodeIn(await RealProcess.RunAsync(runner, "server", "pair", "--repo", RunnerDirectory, "--max-role", "admin"));
        var syncClientCode = Programs.CodeIn(await RealProcess.RunAsync(runner, "server", "pair", "--repo", RunnerDirectory, "--max-role", "admin"));
        RunnerUrl = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
        RunnerProcess = RealProcess.Start(runner, ["server", "run", "--repo", RunnerDirectory, "--listen", RunnerUrl.AbsoluteUri.TrimEnd('/')], environment: environment);
        started.Add(RunnerProcess);
        await RunnerProcess.WaitUntilAsync(new Uri(RunnerUrl, "/health"), TimeSpan.FromSeconds(90));
        RunnerId = (await instances.PairAsync(RunnerUrl, consoleCode, "Runner")).InstanceId;

        Step("runner is up");

        // the replication set, published as the verb publishes it, with the slots bound to the runner's connections (the verb pairs the Sync side's client with the code)
        var published = await RealProcess.RunAsync(sync, "package", "publish", "--to", "runner", "--repo", SyncDirectory, "--server", RunnerUrl.AbsoluteUri.TrimEnd('/'), "--channel", Channel, "--package", Package,
            "--code", syncClientCode, "--client-dir", ClientDirectory, "--bind", "src=src", "--bind", "tgt=tgt", "--bind", "dw=dw", "--runtime-range", "*", "--serve");
        Assert.Contains("Published", published);
        Step("replication set published");

        // the link of Sync to the runner, through the API as the Runner tab does
        var link = await Sync(HttpMethod.Put, "/api/runner", new { server = RunnerUrl.AbsoluteUri.TrimEnd('/'), channel = Channel, package = Package, clientDirectory = ClientDirectory });
        Assert.True(link.Status == HttpStatusCode.OK, $"link: {link.Status} {link.Body}");
    }

    private string RuntimeManifest() =>
        "schema: package/1\nname: " + RuntimePackage + "\ndescription: \"The dbdatasync engine for the test\"\nruntime: true\nversion: \"1.0.0\"\nplatform: " + (OperatingSystem.IsWindows() ? "win-x64" : "linux-x64") + "\n" +
        "launch: { kind: executable, command: dotnet, args: [\"{runtime}/bin/DbDataSync.TaskRunner.dll\", \"--runner\"] }\nbuilt_against: { dotnet: \"10.0\", kit: \"1\", protocol: dbdatakit.runner.v1 }\n";

    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private async Task CreateConnectionsAsync()
    {
        var mssql = SqlServerLogin;
        var made = await Sync(HttpMethod.Put, "/api/connections/src", new
        {
            name = "src", driverType = "MsSql", host = mssql.DataSource.Split(',')[0], port = int.Parse(mssql.DataSource.Split(',')[1]), database = SourceDatabase, authMode = "SqlAuth", userId = mssql.UserID, password = mssql.Password,
            properties = new Dictionary<string, string> { ["TrustServerCertificate"] = "true", ["Encrypt"] = "false" },
        });
        Assert.True(made.Status == HttpStatusCode.OK, $"src: {made.Status} {made.Body}");
        var pg = PostgresLogin;
        made = await Sync(HttpMethod.Put, "/api/connections/tgt", new
        {
            name = "tgt", driverType = "Postgres", host = pg.Host, port = pg.Port, database = TargetDatabase, authMode = "SqlAuth", userId = pg.Username, password = pg.Password,
            properties = new Dictionary<string, string> { ["SslMode"] = "Disable" },
        });
        Assert.True(made.Status == HttpStatusCode.OK, $"tgt: {made.Status} {made.Body}");
        made = await Sync(HttpMethod.Put, "/api/connections/dw", new
        {
            name = "dw", driverType = "MsSql", host = mssql.DataSource.Split(',')[0], port = int.Parse(mssql.DataSource.Split(',')[1]), database = DwDatabase, authMode = "SqlAuth", userId = mssql.UserID, password = mssql.Password,
            properties = new Dictionary<string, string> { ["TrustServerCertificate"] = "true", ["Encrypt"] = "false" },
        });
        Assert.True(made.Status == HttpStatusCode.OK, $"dw: {made.Status} {made.Body}");
    }

    private async Task CreateReplicationAsync(string name, object scheduling, string target, bool verify = false)
    {
        // PostgreSQL has no upsert writer, so a replication to it reloads the table each pass (BatchReload, DeleteInsert); SQL Server to SQL Server reads Change Tracking and merges
        var processing = target == "tgt"
            ? new { reader = new { kind = "BatchReload" }, cache = new { kind = "StagingTable" }, writer = new { kind = "DeleteInsert" } }
            : new { reader = new { kind = "MsSqlChangeTracking" }, cache = new { kind = "MsSqlStagingTable" }, writer = new { kind = "MsSqlMerge" } };
        var task = await Sync(HttpMethod.Put, $"/api/replications/{name}", new
        {
            name,
            enabled = true,
            endpoints = new { source = new { connectionName = "src", database = SourceDatabase }, target = new { connectionName = target, database = target == "tgt" ? TargetDatabase : DwDatabase } },
            scheduling,
            changeProcessing = processing,
        });
        Assert.True(task.Status == HttpStatusCode.OK, $"{name}: {task.Status} {task.Body}");
        var mapping = await Sync(HttpMethod.Put, $"/api/replications/{name}/table-mappings/main", new
        {
            name = "main",
            sources = new[] { new { schema = "dbo", table = name + "_src" } },
            targets = new[] { new { schema = target == "tgt" ? "public" : "dbo", table = name + "_tgt" } },
            columnMappings = new[] { new { sourceColumn = "id", targetColumn = "id" }, new { sourceColumn = "name", targetColumn = "name" } },
            verification = verify ? (object[])[new { name = "counts", kind = "RowCount", groupBy = new[] { "id" } }] : [],
        });
        Assert.True(mapping.Status == HttpStatusCode.OK, $"{name} mapping: {mapping.Status} {mapping.Body}");
        var cached = await Sync(HttpMethod.Post, $"/api/replications/{name}/table-mappings/main/refresh-metadata");
        Assert.True(cached.Status == HttpStatusCode.OK, $"{name} metadata: {cached.Status} {cached.Body}");
    }

    // ---- calls

    private async Task<(HttpStatusCode Status, JsonNode? Body)> As(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={console.Services.GetRequiredService<ISessionStore>().Create(personId).Id}");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    /// <summary>A call to the Sync API as a signed-in administrator, through the console's relay.</summary>
    public Task<(HttpStatusCode Status, JsonNode? Body)> Sync(HttpMethod method, string path, object? body = null) => As(method, $"/relay/{SyncId}{path}", body);

    /// <summary>A call to the runner's management API the same way.</summary>
    public Task<(HttpStatusCode Status, JsonNode? Body)> Runner(HttpMethod method, string path, object? body = null) => As(method, $"/relay/{RunnerId}/api/v1{path}", body);

    /// <summary>Waits until <paramref name="condition"/> holds, asking every 500 ms; fails with <paramref name="what"/> and the end of both servers' output.</summary>
    public async Task WaitAsync(Func<Task<bool>> condition, string what, int seconds = 120)
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(500);
        }

        Assert.Fail($"Waited {seconds} s for {what}.\n{string.Join('\n', Steps)}\n--- the runner's runs:\n{await Diagnose()}\n--- Sync:\n{Tail(SyncProcess.Output)}\n--- runner:\n{Tail(RunnerProcess.Output)}");
    }

    /// <summary>What the runner says of the package's runs and tasks, and the last lines of the log of its latest runs of <paramref name="task"/>: what a failure of the story needs to be read from.</summary>
    public async Task<string> Diagnose(string task = "inc")
    {
        try
        {
            var runs = await Runner(HttpMethod.Get, $"/runs?package={Package}&channel={Channel}&limit=20");
            var status = await Runner(HttpMethod.Get, "/status");
            return $"{runs.Body}\n{status.Body?["tasks"]}\nproblems: {status.Body?["problems"]}; running_here {status.Body?["running_here"]} of {status.Body?["max_concurrent_runs"]}; instances {status.Body?["instances"]}\n{RunnerLogs(task)}";
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException) { return ex.Message; }
    }

    private string RunnerLogs(string task)
    {
        try
        {
            var state = StateDatabase.OpenModules(Path.Combine(RunnerDirectory, "state.db"), DbDataKit.Runner.RunnerNames.EngineModules, new StateScope(Group: "main", Service: DbDataKit.Runner.RunnerNames.ServiceId));
            var runs = new RunStore(state, DbDataKit.Runner.RunnerNames.EngineService);
            var text = new System.Text.StringBuilder();
            foreach (var run in runs.Read(new RunQuery(Service: DbDataKit.Runner.RunnerNames.EngineService, JobId: $"{Package}/{Channel}/{task}", Limit: 3)))
            {
                text.AppendLine($"run {run.Id} {run.Run.Outcome}");
                foreach (var line in runs.Logs(run.Id, limit: 60).TakeLast(30)) text.AppendLine($"  [{line.Level}] {line.Message}");
            }

            return text.ToString();
        }
        catch (Exception ex) { return "(the runner's logs could not be read: " + ex.Message + ")"; }
    }

    private static string Tail(string text) => text.Length <= 6000 ? text : "..." + text[^6000..];

    public async ValueTask DisposeAsync()
    {
        // for working on a failure: the programs and their directories are left as they are (the processes outlive the test run), and where they are is printed
        if (Environment.GetEnvironmentVariable("DBDATAKIT_TEST_E12E_KEEP") == "1")
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "e12e-runner-output.txt"), RunnerProcess.Output);
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "e12e-keep.txt"), $"root {Root}\nsync {SyncUrl}\nrunner {RunnerUrl}\nsource {SourceDatabase}\ndw {DwDatabase}\ntarget {TargetDatabase}\n");
            return;
        }

        http?.Dispose();
        if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
        foreach (var program in Enumerable.Reverse(started)) await program.DisposeAsync();
        try
        {
            SqlConnection.ClearAllPools();
            NpgsqlConnection.ClearAllPools();
            foreach (var database in sqlServerDatabases)
            {
                await using var server = new SqlConnection(SqlServer);
                await server.OpenAsync();
                await Exec(server, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}];");
            }

            foreach (var database in postgresDatabases)
            {
                await using var server = new NpgsqlConnection(Postgres);
                await server.OpenAsync();
                await Exec(server, $"DROP DATABASE IF EXISTS {database} WITH (FORCE)");
            }
        }
        catch (Exception ex) when (ex is DbException or InvalidOperationException) { /* a scratch database left behind is untidy; its name carries a GUID */ }

        try
        {
            // the runner's cache holds read-only directories (0555): make them writable to delete them
            if (!OperatingSystem.IsWindows() && Directory.Exists(Root))
                foreach (var directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories))
                    File.SetUnixFileMode(directory, (UnixFileMode)0b111_111_101);
            Directory.Delete(Root, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
