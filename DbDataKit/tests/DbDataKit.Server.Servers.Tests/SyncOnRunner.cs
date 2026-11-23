using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using DbDataKit.Runner.Client;
using DbDataKit.Server;
using DbDataKit.Secrets;
using DbDataKit.State;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// The end-to-end harness of plan 28c E12e, without a Sync server (plan 33 SC23d2): replications that really are run by a runner, SQL Server to PostgreSQL and SQL Server to SQL Server, with every piece the program it is.
/// <list type="bullet">
/// <item>the configuration repository is <b>files</b> (<c>config/connections</c>, <c>config/replications/&lt;name&gt;/task.yaml</c> and its mappings with their column cache), as a person or the Workspace writes them; no Sync server reads them;</item>
/// <item>a <b>runner</b> (<c>dbdatarunner server run</c>, its own directory) with the engine as the runtime package <c>dbdatasync-runtime</c>, installed and active in the channel (the TaskRunner's build output with the manifest
/// <c>scripts/runtime-manifest.sh</c> prints);</item>
/// <item>the replication set written as a package by the real <c>dbdatasync package publish --to runner --out</c> and sent with the runner client the console's deploy uses (the command line sends nothing), its slots
/// bound to the connections the runner knows: <c>connection/2</c> files in the runner's <c>connections</c> directory and the
/// logins as <c>DBDATAKIT_SECRET_*</c> in the runner's environment (plan 28c E02);</item>
/// <item>the tasks (a replication's drain, <c>provision</c>) run on the runner with their inputs (<c>run_all</c>, <c>requests</c>: a bulk load, a verification, a resync; <c>provision</c>'s replication, mapping and action): run now, pause
/// and resume through a <b>console</b> in this process paired with the runner, as a person on the Runner's screens does; what only a paired client may do (a work item for a drain that is up, a checkpoint, a step and its payload) with the
/// client the publish paired, as the Sync server's API did.</item>
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

    /// <summary>The configuration repository: the files <c>package publish</c> reads (nothing else is in it).</summary>
    public string SyncDirectory => Path.Combine(Root, "sync");

    public string RunnerDirectory => Path.Combine(Root, "runner");

    public string ClientDirectory => Path.Combine(Root, "sync-client");

    public string SourceDatabase { get; } = "e12e_src_" + Guid.NewGuid().ToString("N")[..10];

    public string TargetDatabase { get; } = "e12e_tgt_" + Guid.NewGuid().ToString("N")[..10];

    /// <summary>The second SQL Server database, the target of <c>inc</c>: PostgreSQL has no upsert writer for incremental passes, so the replication that proves positions and a merge writer is SQL Server to SQL Server.</summary>
    public string DwDatabase { get; } = "e12e_dw_" + Guid.NewGuid().ToString("N")[..10];

    public Uri RunnerUrl { get; private set; } = null!;

    public string RunnerId { get; private set; } = null!;

    public RealProcess RunnerProcess { get; private set; } = null!;

    /// <summary>The client <c>package publish</c> paired with the runner (its store is <see cref="ClientDirectory"/>).</summary>
    private RunnerPublishClient client = null!;

    public static string? EnginesMissing =>
        Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER") is null || Environment.GetEnvironmentVariable("DBDATAKIT_TEST_POSTGRES") is null
            ? "set DBDATAKIT_TEST_SQLSERVER and DBDATAKIT_TEST_POSTGRES (testenv/testenv.sh up mssql postgres; eval \"$(testenv/testenv.sh env dbdatakit)\") to run a replication on a real runner"
            : null;

    /// <summary>The replications of the loads story (<c>SyncOnRunnerLoadsTests</c>) beside the four of the main story: <c>bulk</c> (SQL Server to SQL Server, Change Tracking and merge, for a reload in segments), <c>ver</c> (the same pair, periodic, its mapping has a verification check).</summary>
    public bool Extras { get; init; }

    private string[] Tables => Extras ? ["cont", "per", "inc", "prov", "bulk", "ver"] : ["cont", "per", "inc", "prov"];

    /// <summary>The replications whose target is the SQL Server database <see cref="DwDatabase"/> (the others write to PostgreSQL).</summary>
    private static bool IsDw(string table) => table is "inc" or "prov" or "bulk" or "ver";

    /// <summary>The replication whose target table is not made by the harness: the set's <c>provision</c> task creates it (plan 28c E21), and its mapping has no target column cache, as a table that does not exist yet has none. Its
    /// first drain (the activation's) fails naming the missing table.</summary>
    public const string Provisioned = "prov";

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
            if (table == Provisioned) continue;
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

        // the configuration repository, as files: the connections (a login is a reference, never a value), the replications and their mappings with the column cache the engine reads
        WriteConnections();
        WriteReplication("cont", "  frequencySeconds: 2\n  idleTimeoutSeconds: 5\n", "tgt");
        WriteReplication("per", "  mode: Periodic\n  cronExpression: 0 3 * * *\n", "tgt");   // never due while the test runs: it runs when it is asked to
        WriteReplication("inc", "  frequencySeconds: 2\n  idleTimeoutSeconds: 5\n", "dw");
        WriteReplication(Provisioned, "  mode: Periodic\n  cronExpression: 0 3 * * *\n", "dw");
        if (Extras)
        {
            WriteReplication("bulk", "  frequencySeconds: 2\n  idleTimeoutSeconds: 5\n", "dw");
            WriteReplication("ver", "  mode: Periodic\n  cronExpression: 0 3 * * *\n", "dw", verify: true);
        }

        Step("connections and replications written");

        // the console, here: what a person reaches the runner's screens through
        var consoleDirectory = Path.Combine(Root, "console");
        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()), Timeout = TimeSpan.FromMinutes(8) };
        var instances = console.Services.GetRequiredService<ConsoleInstances>();
        personId = console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Admin", null, UserRole.Admin).Id;

        // the runner: the connections it knows are the repository's files, and the logins reach it as environment secrets (plan 28c E02)
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

        // the replication set: the verb writes the package, and the runner client sends it (pairing with the code), with the slots bound to the runner's connections and the version served at once
        var packageFolder = Path.Combine(Root, "sync-package");
        var written = await RealProcess.RunAsync(sync, "package", "publish", "--to", "runner", "--repo", SyncDirectory, "--package", Package, "--runtime-range", "*", "--out", packageFolder);
        Assert.Contains("Wrote the package", written);
        client = new RunnerPublishClient(new RunnerClientStore(ClientDirectory), Environment.GetEnvironmentVariable);
        var server = RunnerUrl.AbsoluteUri.TrimEnd('/');
        await client.BindAsync(server, syncClientCode, Package, Channel, new Dictionary<string, string> { ["src"] = "src", ["tgt"] = "tgt", ["dw"] = "dw" });
        var published = await client.PublishAsync(server, null, Path.Combine(packageFolder, "package.yaml"), Path.Combine(packageFolder, "files"), Channel, stage: false, expectedVersion: null);
        Assert.True(published.Version >= 1, $"{published.Mode} {published.Version}");
        Step("replication set published");
    }

    /// <summary>A runtime version in the line of dbdatasync as built (<c>2026.11.0</c> for a build numbered <c>2026.11.23.1232-alpha.17</c>), so a set a Workspace server builds with dbdatasync's own range (<c>2026.11.x</c>) runs on it.</summary>
    private static string RuntimeVersion()
    {
        var parts = (System.Diagnostics.FileVersionInfo.GetVersionInfo(Programs.Sync).ProductVersion ?? "").Split('-', '+')[0].Split('.');
        return parts.Length >= 2 && parts.Take(2).All(p => p.Length > 0 && p.All(char.IsAsciiDigit)) ? $"{parts[0]}.{parts[1]}.0" : "1.0.0";
    }

    private string RuntimeManifest() =>
        "schema: package/1\nname: " + RuntimePackage + "\ndescription: \"The dbdatasync engine for the test\"\nruntime: true\nversion: \"" + RuntimeVersion() + "\"\nplatform: " + (OperatingSystem.IsWindows() ? "win-x64" : "linux-x64") + "\n" +
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

    // ---- the configuration repository, as files (what the Sync server's screens wrote before plan 33 SC23)

    private void Write(string relative, string text)
    {
        var path = Path.Combine(SyncDirectory, "config", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private void WriteConnections()
    {
        var mssql = SqlServerLogin;
        var (host, port) = (mssql.DataSource.Split(',')[0], mssql.DataSource.Split(',')[1]);
        var pg = PostgresLogin;
        foreach (var (name, database) in new[] { ("src", SourceDatabase), ("dw", DwDatabase) })
            Write($"connections/{name}.yaml", $"""
                schema: connection/2
                name: {name}
                engine: sqlserver
                address:
                  host: {host}
                  port: {port}
                  database: {database}
                auth:
                  mode: sql
                  user: {mssql.UserID}
                  password_secret: secret:dbdatakit:sync:connection:{name}
                extensions:
                  sync:
                    properties:
                      TrustServerCertificate: true
                      Encrypt: false

                """);
        Write("connections/tgt.yaml", $"""
            schema: connection/2
            name: tgt
            engine: postgres
            address:
              host: {pg.Host}
              port: {pg.Port}
              database: {TargetDatabase}
            auth:
              mode: sql
              user: {pg.Username}
              password_secret: secret:dbdatakit:sync:connection:tgt
            extensions:
              sync:
                properties:
                  SslMode: Disable

            """);
    }

    /// <summary>A replication from <c>dbo.&lt;name&gt;_src</c> of the source to <c>&lt;name&gt;_tgt</c> of <paramref name="target"/>, with one mapping <c>main</c> and its column cache (what <c>refresh-metadata</c> recorded on the deleted server).</summary>
    private void WriteReplication(string name, string scheduling, string target, bool verify = false)
    {
        // PostgreSQL has no upsert writer, so a replication to it reloads the table each pass (BatchReload, DeleteInsert); SQL Server to SQL Server reads Change Tracking and merges
        var (reader, cache, writer) = target == "tgt" ? ("BatchReload", "StagingTable", "DeleteInsert") : ("MsSqlChangeTracking", "MsSqlStagingTable", "MsSqlMerge");
        Write($"replications/{name}/task.yaml", $"""
            schemaVersion: 2
            name: {name}
            scheduling:
            {scheduling}changeProcessing:
              reader:
                kind: {reader}
              cache:
                kind: {cache}
              writer:
                kind: {writer}
            endpoints:
              source:
                connectionName: src
                database: {SourceDatabase}
              target:
                connectionName: {target}
                database: {(target == "tgt" ? TargetDatabase : DwDatabase)}

            """);
        var targetColumns = name == Provisioned
            ? ""   // the table does not exist yet: the set's provision task creates it
            : target == "tgt"
                ? "targetColumns:\n- name: id\n  nativeType: integer\n  isPrimaryKey: true\n  keyOrdinal: 1\n- name: name\n  nativeType: text\n"
                : "targetColumns:\n- name: id\n  nativeType: int\n  isPrimaryKey: true\n  keyOrdinal: 1\n- name: name\n  nativeType: nvarchar(50)\n";
        Write($"replications/{name}/table-mappings/main.yaml", $"""
            schemaVersion: 1
            name: main
            sources:
            - schema: dbo
              table: {name}_src
            targets:
            - schema: {(target == "tgt" ? "public" : "dbo")}
              table: {name}_tgt
            columnMappings:
            - sourceColumn: id
              targetColumn: id
            - sourceColumn: name
              targetColumn: name
            {(verify ? "verification:\n- name: counts\n  groupBy:\n  - id\n" : "")}sourceColumns:
            - name: id
              nativeType: int
              isPrimaryKey: true
              keyOrdinal: 1
            - name: name
              nativeType: nvarchar(50)
            {targetColumns}
            """);
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

    /// <summary>The console in this process, paired with the runner as <see cref="RunnerId"/>.</summary>
    public WebApplication Console => console;

    /// <summary>The administrator every call is made as.</summary>
    public string PersonId => personId;

    /// <summary>A call to the console's own API as the signed-in administrator.</summary>
    public Task<(HttpStatusCode Status, JsonNode? Body)> ConsoleCall(HttpMethod method, string path, object? body = null) => As(method, path, body);

    /// <summary>Runs a statement against the source database (SQL Server).</summary>
    public async Task ExecuteSourceAsync(string sql)
    {
        await using var source = new SqlConnection(SourceConnectionString);
        await source.OpenAsync();
        await Exec(source, sql);
    }

    /// <summary>A call to the runner's management API as a signed-in administrator, through the console's relay (what the Runner's screens call).</summary>
    public Task<(HttpStatusCode Status, JsonNode? Body)> Runner(HttpMethod method, string path, object? body = null) => As(method, $"/relay/{RunnerId}/api/v1{path}", body);

    private static string TaskPath(string task) => $"/tasks/{Package}/{Channel}/{task}";

    /// <summary>Run now of a task of the set with its inputs, through the relay: 202 with the run's id, or 409 when a run of the task is queued or running (the runner has one drain of a replication at a time).</summary>
    public Task<(HttpStatusCode Status, JsonNode? Body)> RunNow(string task, IReadOnlyDictionary<string, string>? inputs = null) =>
        Runner(HttpMethod.Post, TaskPath(task) + "/run", new { inputs = inputs ?? new Dictionary<string, string>() });

    public Task<(HttpStatusCode Status, JsonNode? Body)> Pause(string task) => Runner(HttpMethod.Post, TaskPath(task) + "/pause");

    public Task<(HttpStatusCode Status, JsonNode? Body)> Resume(string task) => Runner(HttpMethod.Post, TaskPath(task) + "/resume");

    /// <summary>The runner's row of a task of the set in its status (running, queued, paused and the last run), or null when it has none.</summary>
    public async Task<JsonNode?> TaskStatus(string task)
    {
        var (status, body) = await Runner(HttpMethod.Get, "/status");
        Assert.True(status == HttpStatusCode.OK, $"status: {status} {body}");
        return body!["tasks"]!.AsArray().FirstOrDefault(t => (string?)t!["package"] == Package && (string?)t["channel"] == Channel && (string?)t["task"] == task);
    }

    /// <summary>
    /// Requests of a replication's drain (a bulk load's segments, a verification, a resync), as the Sync API sent them (<c>DrainRequest</c> of <c>DbDataSync.State</c>, the <c>requests</c> input): one run now with the requests as
    /// its input, or, when the drain is queued or running already, each request as an item of the task's own queue, which that drain takes in its next sweep. Every request gets an id, which the drain applies once.
    /// </summary>
    public async Task SendAsync(string replication, params JsonObject[] requests)
    {
        foreach (var request in requests) request["id"] ??= Guid.NewGuid().ToString("N");
        var text = new JsonArray([.. requests.Select(r => (JsonNode)r.DeepClone())]).ToJsonString();
        var (status, body) = await RunNow(replication, new Dictionary<string, string> { ["requests"] = text });
        if (status == HttpStatusCode.Accepted) return;
        Assert.True(status == HttpStatusCode.Conflict, $"run now of {replication} with requests: {status} {body}");
        foreach (var request in requests)
            await client.EnqueueWorkAsync(RunnerUrl.AbsoluteUri.TrimEnd('/'), null, Package, Channel, replication, "Request", (string)request["mapping"]!, (string)request["id"]!, request.ToJsonString());
    }

    /// <summary>The items of a checkpoint of a task, read with the paired client (the relay serves a person the list of checkpoints, not one).</summary>
    public Task<IReadOnlyList<ClientCheckpointItem>> CheckpointAsync(string task, string name) => client.CheckpointAsync(RunnerUrl.AbsoluteUri.TrimEnd('/'), null, Package, Channel, task, name);

    /// <summary>The recorded steps of one name of a task, newest first, read with the paired client.</summary>
    public Task<IReadOnlyList<ClientStep>> StepsAsync(string task, string step) => client.StepsAsync(RunnerUrl.AbsoluteUri.TrimEnd('/'), null, Package, Channel, task, step);

    /// <summary>The streamed result of a step (a verification's parquet file), or null when the runner no longer has it.</summary>
    public async Task<byte[]?> StepPayloadAsync(string task, string step, Guid payload)
    {
        using var buffer = new MemoryStream();
        return await client.StepPayloadAsync(RunnerUrl.AbsoluteUri.TrimEnd('/'), null, Package, Channel, task, step, payload, buffer) ? buffer.ToArray() : null;
    }

    /// <summary>Waits until <paramref name="condition"/> holds, asking every 500 ms; fails with <paramref name="what"/> and the end of both servers' output.</summary>
    public async Task WaitAsync(Func<Task<bool>> condition, string what, int seconds = 120, string task = "inc")
    {
        var until = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < until)
        {
            if (await condition()) return;
            await Task.Delay(500);
        }

        Assert.Fail($"Waited {seconds} s for {what}.\n{string.Join('\n', Steps)}\n--- the runner's runs:\n{await Diagnose(task)}\n--- runner:\n{Tail(RunnerProcess.Output)}");
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
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "e12e-keep.txt"), $"root {Root}\nrunner {RunnerUrl}\nsource {SourceDatabase}\ndw {DwDatabase}\ntarget {TargetDatabase}\n");
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
