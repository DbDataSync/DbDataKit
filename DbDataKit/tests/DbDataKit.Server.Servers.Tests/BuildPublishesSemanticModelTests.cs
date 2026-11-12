using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Apache.Arrow;
using DbDataKit.Server;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataGateway.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Servers.Tests;

/// <summary>
/// Plan 19, J12's end-to-end test: a project's semantic model, with the connection it reads, published by a real Build server to a real Gateway (each started from its built output), with a console in this process speaking for a signed-in person.
/// The model is then served: the Gateway lists it at version 1, a client with an account on the Gateway asks it for the members of an attribute and gets the rows of the database behind it, and the Gateway's audit names the person and the Build server.
/// It needs a SQL Server (<c>DBDATAKIT_TEST_SQLSERVER</c>, the shared test engine): publishing a model checks that every table it names can be read, and a project's models build on SQL Server or PostgreSQL only. Without one it is skipped.
/// </summary>
public sealed class BuildPublishesSemanticModelTests : IAsyncLifetime
{
    private static readonly string[] MartsTables = ["dim_customer", "dim_product", "dim_store", "dim_date", "fct_sales_lines", "fct_returns"];

    private readonly string root = Path.Combine(Path.GetTempPath(), "ddk-e2e-model-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<IAsyncDisposable> started = [];
    private string? server;
    private string database = "";
    private WebApplication console = null!;
    private HttpClient http = null!;
    private string gatewayId = null!;
    private string buildId = null!;
    private string personId = null!;
    private string gatewayDirectory = null!;
    private int flightPort;
    private string aliceToken = "";

    public async ValueTask InitializeAsync()
    {
        server = Environment.GetEnvironmentVariable("DBDATAKIT_TEST_SQLSERVER");
        if (server is null) Assert.Skip("set DBDATAKIT_TEST_SQLSERVER (testenv/testenv.sh up mssql; eval \"$(testenv/testenv.sh env dbdatakit)\") to publish a model against a real database");

        // the database behind the model: the tables the project's marts models stand for, with the customers the test asks for (a third has no sales)
        database = "e2e_semantic_" + Guid.NewGuid().ToString("N")[..10];
        await Execute(server, $"CREATE DATABASE [{database}]");
        var readLogin = new SqlConnectionStringBuilder(server) { InitialCatalog = database }.ConnectionString;
        await Execute(readLogin, "CREATE SCHEMA marts");
        // every table and column the model names must be readable (a publish reads one empty SELECT per table)
        var columns = new Dictionary<string, string>
        {
            ["dim_customer"] = "customer_id INT NULL",
            ["dim_product"] = "product_id INT NULL, department NVARCHAR(50) NULL, category_name NVARCHAR(50) NULL, product_name NVARCHAR(100) NULL",
            ["dim_store"] = "store_id INT NULL, region NVARCHAR(50) NULL, channel_group NVARCHAR(50) NULL, store_name NVARCHAR(100) NULL",
            ["dim_date"] = "date_key INT NULL, [date] DATE NULL, [year] INT NULL, year_month INT NULL",
            ["fct_sales_lines"] = "customer_id INT NULL",
            ["fct_returns"] = "product_id INT NULL, date_key INT NULL, refund_amount DECIMAL(18,2) NULL",
        };
        foreach (var table in MartsTables) await Execute(readLogin, $"CREATE TABLE marts.{table} ({columns[table]})");
        await Execute(readLogin, "DROP TABLE marts.dim_customer; CREATE TABLE marts.dim_customer (customer_id INT NOT NULL, region NVARCHAR(50) NULL, country_code NVARCHAR(10) NULL)");
        await Execute(readLogin, "INSERT INTO marts.dim_customer VALUES (1, N'West', N'US'), (2, N'East', N'US'), (3, N'North', N'CA')");
        // the values an attribute takes are those in the fact rows that reach it: two customers have sales, the third does not
        await Execute(readLogin, "DROP TABLE marts.fct_sales_lines; CREATE TABLE marts.fct_sales_lines (customer_id INT NULL, product_id INT NULL, store_id INT NULL, date_key INT NULL, order_id INT NULL, quantity INT NULL, net_amount DECIMAL(18,2) NULL, gross_amount DECIMAL(18,2) NULL, margin_amount DECIMAL(18,2) NULL)");
        await Execute(readLogin, "INSERT INTO marts.fct_sales_lines (customer_id, product_id, store_id, date_key) VALUES (1, 1, 1, 20260101), (1, 1, 1, 20260102), (2, 1, 1, 20260101)");

        var build = Programs.Build;
        var gateway = Programs.Gateway;
        gatewayDirectory = Path.Combine(root, "gateway");
        var (buildDirectory, consoleDirectory) = (Path.Combine(root, "build"), Path.Combine(root, "console"));
        var project = Path.Combine(root, "retail");
        Directory.CreateDirectory(Path.Combine(gatewayDirectory, "config", "connections"));
        Directory.CreateDirectory(Path.Combine(gatewayDirectory, "config", "gateway"));
        Directory.CreateDirectory(buildDirectory);

        // the Gateway: a management listener, a certificate of its own, and one person who may read the data source the publish will make
        File.WriteAllText(Path.Combine(gatewayDirectory, "config", "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        File.WriteAllText(Path.Combine(gatewayDirectory, "config", "gateway", "auth.yaml"), "jwt:\n  signingKey: servers-e2e-signing-key-0123456789abcdef\n  audience: api://servers-e2e\n");
        // the person is a service account of the Gateway's identity home (P13: Gateway has no users of its own) with a personal access token, made as an operator does
        await RealProcess.RunAsync(gateway, "identity", "service-account-add", "--name", "alice", "--repo", gatewayDirectory);
        aliceToken = System.Text.RegularExpressions.Regex.Match(await RealProcess.RunAsync(gateway, "identity", "token-create", "--account", "alice", "--name", "e2e", "--repo", gatewayDirectory), @"ddk_[A-Za-z0-9_-]+").Value;
        Assert.NotEmpty(aliceToken);
        (flightPort, var management) = (Programs.FreePort(), Programs.FreePort());
        var gatewayCode = Programs.CodeIn(await RealProcess.RunAsync(gateway, "server", "pair", "--repo", gatewayDirectory, "--max-role", "admin"));
        var gatewayProcess = RealProcess.Start(gateway, ["server", "run", $"--Gateway:DataDirectory={gatewayDirectory}", "--Gateway:Listen:Address=127.0.0.1", $"--Gateway:Listen:Port={flightPort}",
            "--Gateway:Management:Enabled=true", "--Gateway:Management:Address=127.0.0.1", $"--Gateway:Management:Port={management}", "--Gateway:Management:LocalChannel=false"]);
        started.Add(gatewayProcess);
        var gatewayUrl = new Uri($"https://127.0.0.1:{management}");

        // the Build server: the retail project (its semantic model reads the project's one connection, sqlserver), whose read login is in the server's own store
        await RealProcess.RunAsync(build, "project", "create", "retail", project);
        // who may read the data source is part of what the project publishes (plan 22a GP03): the project's connection file says everyone, and the Gateway adds nothing to it
        Directory.CreateDirectory(Path.Combine(project, "connections"));
        File.WriteAllText(Path.Combine(project, "connections", "sqlserver.yaml"), "schema: connection/2\nname: sqlserver\nengine: sqlserver\ngrants:\n  everyone: true\n");
        await RealProcess.RunAsync(build, "server", "project", "add", "retail", "--path", project, "--repo", buildDirectory);
        await RealProcess.RunWithInputAsync(build, readLogin + "\n", "secret", "set", "dbdatakit:build:connection:sqlserver:read", "--stdin", "--repo", buildDirectory);
        var buildCode = Programs.CodeIn(await RealProcess.RunAsync(build, "server", "pair", "--repo", buildDirectory, "--max-role", "viewer,author,operator,approver,admin"));
        var buildUrl = new Uri($"https://127.0.0.1:{Programs.FreePort()}");
        var buildProcess = RealProcess.Start(build, ["server", "run", "--repo", buildDirectory, "--listen", buildUrl.AbsoluteUri.TrimEnd('/')]);
        started.Add(buildProcess);
        await Task.WhenAll(gatewayProcess.WaitUntilAsync(new Uri(gatewayUrl, "/health/live"), TimeSpan.FromSeconds(90)), buildProcess.WaitUntilAsync(new Uri(buildUrl, "/health/live"), TimeSpan.FromSeconds(90)));

        console = ConsoleHost.Build(new ConsoleSettings(consoleDirectory, new Uri("https://localhost:0")), ConsoleHost.Certificate(consoleDirectory), _ => { }, webRoot: Path.Combine(consoleDirectory, "none"));
        await console.StartAsync();
        http = new HttpClient(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true, UseCookies = false }) { BaseAddress = new Uri(console.Urls.First()) };
        var instances = console.Services.GetRequiredService<ConsoleInstances>();
        gatewayId = (await instances.PairAsync(gatewayUrl, gatewayCode, "Edge gateway")).InstanceId;
        buildId = (await instances.PairAsync(buildUrl, buildCode, "Retail build")).InstanceId;
        personId = console.Services.GetRequiredService<IUserAccounts>().CreateUser("Ada Admin", null, UserRole.Admin).Id;
    }

    public async ValueTask DisposeAsync()
    {
        http?.Dispose();
        if (console is not null) { await console.StopAsync(); await console.DisposeAsync(); }
        foreach (var program in started) await program.DisposeAsync();
        if (server is not null && database.Length > 0)
        {
            SqlConnection.ClearAllPools();
            try { await Execute(server, $"ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]"); }
            catch (SqlException) { /* a scratch database left behind is untidy; its name carries a GUID */ }
        }
        try { Directory.Delete(root, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static async Task Execute(string connectionString, string sql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private async Task<(HttpStatusCode Status, JsonNode? Body)> AsPerson(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add("Cookie", $"{ConsoleHost.Auth.SessionCookie}={console.Services.GetRequiredService<ISessionStore>().Create(personId).Id}");
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length > 0 ? JsonNode.Parse(text) : null);
    }

    private async Task<List<string>> MembersAsync(string attribute)
    {
        // the Gateway's Flight listener has a certificate of its own, which this client has no reason to distrust here
        var handler = new SocketsHttpHandler { SslOptions = new System.Net.Security.SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true } };
        using var client = new GatewayFlightClient($"https://127.0.0.1:{flightPort}", new Grpc.Net.Client.GrpcChannelOptions { HttpHandler = handler });
        await client.AuthenticateAsync("alice", aliceToken);
        var result = await client.QueryAsync($"{{\"kind\": \"members\", \"attribute\": \"{attribute}\"}}", new QueryOptions { DataSource = "semantic:retail_sales" });
        var members = new List<string>();
        await foreach (var batch in result)
            for (int r = 0; r < batch.Length; r++) members.Add(((StringArray)batch.Column(0)).GetString(r));
        return members;
    }

    /// <summary>The Gateway reads its configuration folder again a moment after a file changes; waits, up to a limit, until the person can use the data source.</summary>
    private async Task ServedAsync()
    {
        for (var attempt = 0; ; attempt++)
        {
            try { await MembersAsync("customers.region"); return; }
            catch (Exception ex) when (attempt < 40 && ex.ToString().Contains("PermissionDenied", StringComparison.Ordinal)) { await Task.Delay(250); }
        }
    }

    [Fact]
    public async Task A_build_server_publishes_a_projects_semantic_model_with_its_connection_to_a_gateway_that_then_serves_it_and_the_audit_names_the_person_and_the_server()
    {
        // 1. the console links the two, as in the data source story
        var linked = await AsPerson(HttpMethod.Post, "/api/instances/link", new { from = buildId, to = gatewayId, name = "edge" });
        Assert.True(linked.Status == HttpStatusCode.OK, $"link: {linked.Status} {linked.Body}");

        // 2. publishes the model: the connection it reads first (a read-only data source, the login a secret), then the catalog, as version 1
        var published = await AsPerson(HttpMethod.Post, $"/relay/{buildId}/api/v1/projects/retail/proxies/edge/publish-model", new { model = "retail_sales" });
        Assert.True(published.Status == HttpStatusCode.OK, $"publish-model: {published.Status} {published.Body}");
        Assert.Equal("retail_sales", (string)published.Body!["model"]!);
        Assert.Equal(1, (int)published.Body["version"]!);
        Assert.True((bool)published.Body["created"]!);

        // 3. the Gateway holds it: current version 1, and the data source it reads is the one Build made
        var held = await AsPerson(HttpMethod.Get, $"/relay/{gatewayId}/api/v1/semantic-models/retail_sales");
        Assert.True(held.Status == HttpStatusCode.OK, $"read: {held.Status} {held.Body}");
        Assert.Equal(1, (int)held.Body!["current_version"]!);
        Assert.Contains("retail_sales", (string)held.Body["catalog"]!);
        var connectionFile = Path.Combine(gatewayDirectory, "config", "connections", "sqlserver.yaml");
        Assert.Contains("engine: sqlserver", File.ReadAllText(connectionFile));

        // the grants are the project's: the published file says who may read it, and no one had to open it on the Gateway
        Assert.Contains("everyone: true", File.ReadAllText(connectionFile));
        Assert.DoesNotContain("extensions:", File.ReadAllText(connectionFile));
        await ServedAsync();

        // 4. and serves it: a person with an account on the Gateway asks for the members of an attribute and reads the database behind the model
        Assert.Equal(["East", "West"], (await MembersAsync("customers.region")).Order().ToArray());

        // 5. publishing the same model again is no new version (plan 20 O03: the group's definitions keep one version of the same content), and the model keeps serving
        var again = await AsPerson(HttpMethod.Post, $"/relay/{buildId}/api/v1/projects/retail/proxies/edge/publish-model", new { model = "retail_sales" });
        Assert.True(again.Status == HttpStatusCode.OK, $"publish again: {again.Status} {again.Body}");
        Assert.Equal(1, (int)again.Body!["version"]!);
        Assert.False((bool)again.Body["created"]!);
        // a publish writes the whole connection file again, grants included (a republish replaces them, the Gateway has none of its own): the data source is as open as the project says
        Assert.Contains("everyone: true", File.ReadAllText(connectionFile));
        Assert.Equal(2, (await MembersAsync("customers.region")).Count);

        // 6. the Gateway's audit says who: the person, and the Build server that acted for them
        var audit = GatewayAudit.Read(gatewayDirectory);
        Assert.Contains(audit, a => (string?)a["command"] == "semantic-model publish" && (string?)a["target"] == "retail_sales" && (string?)a["outcome"] == "created" && (string?)a["userId"] == personId);
        Assert.Contains(audit, a => (string?)a["command"] == "semantic-model publish" && (string?)a["target"] == "retail_sales" && (string?)a["outcome"] == "unchanged" && (string?)a["userId"] == personId);
    }
}
