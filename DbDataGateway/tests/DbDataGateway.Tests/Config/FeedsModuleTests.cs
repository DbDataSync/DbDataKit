using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Apache.Arrow;
using Apache.Arrow.Ipc;
using DbDataKit.Hosting;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Hosting;
using DbDataGateway.Server.Serving;
using DbDataGateway.Tests.Support;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// Plan 14a SL31 (was J28): the <c>feeds</c> module through the real host. A saved query of the model, a person's own saved query and a semantic request are read as JSON, CSV and an Arrow IPC stream from the module's own HTTPS
/// listener, as the identity home's person behind the token: the rows are the ones the semantic module gives every protocol (compared here with a statement run through the serving core, which is what Flight SQL runs), a person
/// without the data source's group is refused in words, a token that is not the home's (a console's, nonsense) is refused, <c>limit</c> pages and is bounded, and with the module not listed nothing listens.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class FeedsModuleTests : IAsyncLifetime
{
    private const string Model = "sales_catalog";
    private const string Source = "sales-ro";
    private const string Analysts = "analysts";

    private readonly string dataDirectory = Path.Combine(Path.GetTempPath(), "dbgateway-feeds-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication? app;
    private int feedsPort;
    private int managementPort;

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string ConfigDirectory => GatewayConfigFile.DirectoryIn(dataDirectory);

    private const string Queries = """

        queries:
          revenue_by_region:
            description: Net revenue by region
            sql: SELECT customers.region, MEASURE(orders.net_revenue) AS net FROM sales_catalog GROUP BY ALL ORDER BY customers.region LIMIT 1000
          revenue_in_quarter:
            sql: SELECT customers.region, MEASURE(orders.net_revenue) AS net FROM sales_catalog WHERE date.fiscal_quarter = :quarter GROUP BY ALL ORDER BY customers.region LIMIT 1000
            parameters:
              quarter: {for: date.fiscal_quarter, type: string}
          revenue_in_regions:
            sql: SELECT customers.region, MEASURE(orders.net_revenue) AS net FROM sales_catalog WHERE customers.region IN (:regions) GROUP BY ALL ORDER BY customers.region LIMIT 1000
            parameters:
              regions: {for: customers.region, type: string, many: true}
        """;

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(ConfigDirectory, "connections"));
        File.WriteAllText(Path.Combine(ConfigDirectory, "server.yaml"), "schemaVersion: 2\ntls: { mode: selfSigned }\n");
        var file = Path.Combine(dataDirectory, "sales.duckdb");
        using (var connection = new DuckDB.NET.Data.DuckDBConnection("Data Source=" + file))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "seed.sql"));
            command.ExecuteNonQuery();
        }
        File.WriteAllText(Path.Combine(ConfigDirectory, "connections", Source + ".yaml"), Conn.Yaml(Source, "duckdb", "Data Source=" + file, gateway: [$"groups: [{Analysts}]"]));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (app is not null) { await app.StopAsync(); await app.DisposeAsync(); }
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        TestFolder.Delete(dataDirectory);
    }

    private async Task StartAsync(bool feeds = true, params string[] more)
    {
        for (var attempt = 1; ; attempt++)
        {
            feedsPort = FreePort();
            managementPort = FreePort();
            while (managementPort == feedsPort) managementPort = FreePort();
            List<string> args = [$"--Gateway:DataDirectory={dataDirectory}", "--Gateway:Listen:Address=127.0.0.1", "--Gateway:Listen:Port=0", "--Gateway:Modules:0=flightsql",
                "--Gateway:Management:Enabled=true", "--Gateway:Management:Address=127.0.0.1", $"--Gateway:Management:Port={managementPort}", "--Gateway:DefinitionPollSeconds=1"];
            if (feeds) args.AddRange(["--Gateway:Modules:1=feeds", "--Gateway:Rest:Address=127.0.0.1", $"--Gateway:Rest:Port={feedsPort}"]);
            args.AddRange(more);
            app = GatewayHost.Build([.. args]);
            try
            {
                await app.StartAsync();
                break;
            }
            catch (IOException ex) when (attempt < 5 && ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
            {
                await app.DisposeAsync();
            }
        }
    }

    private T Get<T>() where T : notnull => app!.Services.GetRequiredService<T>();

    private void PublishModel()
    {
        var models = Get<PublishedSemanticModels>();
        models.Publish(Model, File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "semantic", "catalog.yaml")) + Queries, null, "test", new Dictionary<string, string> { ["sqlserver_sales"] = Source }, [], DateTimeOffset.UtcNow);
        models.ServeNow(Model);
    }

    private static HttpClient Trusting() => new(new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, _, _, _) => true });

    private async Task<HttpResponseMessage> CallAsync(HttpMethod method, string path, string? token, JsonNode? body = null, string? accept = null, string? basicUser = null)
    {
        using var http = Trusting();
        using var request = new HttpRequestMessage(method, $"https://127.0.0.1:{feedsPort}{path}");
        if (basicUser is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(basicUser + ":" + token)));
        else if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (accept is not null) request.Headers.Accept.ParseAdd(accept);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return await http.SendAsync(request);
    }

    private static async Task OkAsync(HttpResponseMessage response) => Assert.True(response.StatusCode == HttpStatusCode.OK, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static async Task<JsonNode> JsonOf(HttpResponseMessage response) => JsonNode.Parse(await response.Content.ReadAsStringAsync())!;

    /// <summary>What the semantic module gives every protocol: a statement run through the serving core as the person, in the REST shape.</summary>
    private async Task<JsonObject> CoreAsync(UserRecordLike person, string sql)
    {
        var callers = await Get<HomeCallers>().GetAsync();
        var caller = new GatewayCaller(HomePrincipals.Create(callers, callers.UserOf(person.Id)!, "test", Get<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue));
        await using var answer = await Get<IServingServices>().Queries.RunAsync(new ServingContext(caller), new ServingRequest.SemanticSql(SemanticModels.SourcePrefix + Model, sql, new ServingLimits(MaxRows: 1000)));
        return await SemanticRest.ResultAsync(answer, CancellationToken.None);
    }

    private sealed record UserRecordLike(string Id);

    private static IEnumerable<string> Rows(JsonNode result) => result["rows"]!.AsArray().Select(r => string.Join("|", r!.AsArray().Select(c => c is JsonValue v && v.TryGetValue<double>(out var d) ? d.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) : (string?)c)));

    [Fact]
    public async Task The_same_saved_query_gives_the_same_rows_as_the_semantic_module_in_json_csv_and_arrow()
    {
        await StartAsync();
        var (ada, token) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        PublishModel();
        Get<DefinitionFollower>().Poll();

        var expected = await CoreAsync(new UserRecordLike(ada.Id), "SELECT * FROM revenue_by_region");
        Assert.NotEmpty(expected["rows"]!.AsArray());

        using var asJson = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_by_region", token);
        await OkAsync(asJson);
        var json = await JsonOf(asJson);
        Assert.Equal("ok", (string?)json["status"]);
        Assert.Equal(Rows(expected), Rows(json));
        Assert.Equal(expected["request_hash"]!.ToJsonString(), json["request_hash"]!.ToJsonString());
        Assert.Equal(expected["rows"]!.AsArray().Count.ToString(), asJson.Headers.GetValues("X-Row-Count").Single());

        // CSV by Accept, as a report server asks: a header row of the column names, then the rows
        using var asCsv = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_by_region", token, accept: "text/csv");
        Assert.Equal("text/csv", asCsv.Content.Headers.ContentType!.MediaType);
        var lines = (await asCsv.Content.ReadAsStringAsync()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("customers_region,net", lines[0]);
        Assert.Equal(expected["rows"]!.AsArray().Count, lines.Length - 1);
        Assert.Equal(Rows(expected).Select(r => r.Replace('|', ',')), lines.Skip(1));

        // Arrow IPC by ?format, with the engine's metadata in the schema
        using var asArrow = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_by_region?format=arrow", token);
        Assert.Equal(GatewayFeeds.ArrowType, asArrow.Content.Headers.ContentType!.MediaType);
        using var reader = new ArrowStreamReader(await asArrow.Content.ReadAsStreamAsync());
        var batch = await reader.ReadNextRecordBatchAsync();
        Assert.Equal("complete", reader.Schema.Metadata["qis.status"]);
        Assert.Equal(["customers_region", "net"], reader.Schema.FieldsList.Select(f => f.Name));
        Assert.Equal(expected["rows"]!.AsArray().Count, batch!.Length);
        Assert.Equal((string?)expected["rows"]![0]![0], ((StringArray)batch.Column(0)).GetString(0));
    }

    [Fact]
    public async Task Parameters_come_from_the_query_string_or_the_body_typed_by_the_definition_and_limit_pages()
    {
        await StartAsync();
        var (ada, token) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        PublishModel();
        Get<DefinitionFollower>().Poll();

        var all = await JsonOf(await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_by_region", token));
        var firstRegion = (string)all["rows"]![0]![0]!;
        var other = (string)all["rows"]![1]![0]!;

        var one = await JsonOf(await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_in_regions?regions={firstRegion}", token));
        Assert.Equal([firstRegion], one["rows"]!.AsArray().Select(r => (string)r![0]!));       // one value of a many parameter is still a list
        var two = await JsonOf(await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_in_regions?regions={firstRegion}&regions={other}", token));
        Assert.Equal(2, two["rows"]!.AsArray().Count);
        var posted = await JsonOf(await CallAsync(HttpMethod.Post, $"/api/v1/queries/{Model}/revenue_in_regions", token, new JsonObject { ["parameters"] = new JsonObject { ["regions"] = new JsonArray(other) } }));
        Assert.Equal([other], posted["rows"]!.AsArray().Select(r => (string)r![0]!));
        var quarter = await JsonOf(await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_in_quarter?quarter=FY27-Q1", token));
        Assert.Equal("ok", (string?)quarter["status"]);
        Assert.Equal(Rows(await CoreAsync(new UserRecordLike(ada.Id), "SELECT * FROM revenue_in_quarter(quarter := 'FY27-Q1')")), Rows(quarter));

        // limit is the only paging: a smaller one cuts the answer and says so, one over the module's is refused
        var cut = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_by_region?limit=1", token);
        Assert.Single((await JsonOf(cut))["rows"]!.AsArray());
        Assert.Equal("true", cut.Headers.GetValues("X-Truncated").Single());
        using var tooMany = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_by_region?limit=10001", token);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
        Assert.Equal("invalid_limit", (string?)(await JsonOf(tooMany))["code"]);
        using var notANumber = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_by_region?limit=many", token);
        Assert.Equal(HttpStatusCode.BadRequest, notANumber.StatusCode);

        // a parameter the query does not define, a query the model does not have, a model that is not served and a format that is not one are refused in words
        using var unknownParameter = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_in_quarter?quarter=FY27-Q1&quarter2=1", token);
        Assert.Equal(HttpStatusCode.BadRequest, unknownParameter.StatusCode);
        using var missing = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/nothing_here", token);
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        using var noModel = await CallAsync(HttpMethod.Get, "/api/v1/queries/nowhere/revenue_by_region", token);
        Assert.Equal(HttpStatusCode.NotFound, noModel.StatusCode);
        using var badFormat = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_by_region?format=xml", token);
        Assert.Equal(HttpStatusCode.BadRequest, badFormat.StatusCode);
    }

    [Fact]
    public async Task A_persons_own_saved_query_and_a_request_run_as_that_person_and_the_one_without_the_data_sources_group_is_refused_in_words()
    {
        await StartAsync();
        var (ada, adaToken) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        var (_, bobToken) = HomeSeed.Person(Get<StateDatabase>(), "Bob", "bob@example.invalid", "visitors");
        PublishModel();
        Get<DefinitionFollower>().Poll();

        Get<DbDataGateway.Server.State.SavedQueryStore>().Save("ada@example.invalid", Model, "my_revenue", new DbDataKit.Semantic.SavedQueryDef
        {
            Sql = "SELECT customers.region, MEASURE(orders.net_revenue) FROM sales_catalog GROUP BY ALL ORDER BY customers.region LIMIT 1000",
        }, "Ada");
        var mine = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/my_revenue", adaToken);
        await OkAsync(mine);
        Assert.Equal(Rows(await CoreAsync(new UserRecordLike(ada.Id), "SELECT * FROM my_revenue")), Rows(await JsonOf(mine)));

        var request = new JsonObject
        {
            ["model"] = Model,
            ["request"] = JsonNode.Parse("""{"spec_version": "0.3", "kind": "query", "query": {"measures": ["orders.net_revenue"], "attributes": ["customers.region"], "order": [["customers.region", "asc"]]}}"""),
            ["limit"] = 2,
        };
        using var asked = await CallAsync(HttpMethod.Post, "/api/v1/query", adaToken, request);
        await OkAsync(asked);
        Assert.Equal(2, (await JsonOf(asked))["rows"]!.AsArray().Count);
        using var sqlAsked = await CallAsync(HttpMethod.Post, "/api/v1/query", adaToken, new JsonObject { ["model"] = Model, ["sql"] = "SELECT customers.region, MEASURE(orders.net_revenue) FROM sales_catalog GROUP BY ALL" }, accept: "text/csv");
        Assert.StartsWith("customers_region,", await sqlAsked.Content.ReadAsStringAsync());
        using var neither = await CallAsync(HttpMethod.Post, "/api/v1/query", adaToken, new JsonObject { ["model"] = Model });
        Assert.Equal(HttpStatusCode.BadRequest, neither.StatusCode);

        // Bob is in no group the data source names: the same call is refused for him in the semantic module's words, and so is the saved query
        using var refused = await CallAsync(HttpMethod.Post, "/api/v1/query", bobToken, request);
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal("denied", (string?)(await JsonOf(refused))["code"]);
        using var refusedFeed = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/revenue_by_region", bobToken);
        Assert.Equal(HttpStatusCode.Forbidden, refusedFeed.StatusCode);
        using var notBobs = await CallAsync(HttpMethod.Get, $"/api/v1/queries/{Model}/my_revenue", bobToken);
        Assert.NotEqual(HttpStatusCode.OK, notBobs.StatusCode);       // Ada's own query is not his
    }

    [Fact]
    public async Task Only_the_tokens_of_the_identity_home_are_taken_a_user_name_with_a_token_as_the_password_works_and_every_call_is_audited_without_a_value()
    {
        await StartAsync();
        var (_, token) = HomeSeed.Person(Get<StateDatabase>(), "Ada", "ada@example.invalid", Analysts);
        PublishModel();
        Get<DefinitionFollower>().Poll();
        var path = $"/api/v1/queries/{Model}/revenue_in_quarter?quarter=FY27-Q1";

        using var none = await CallAsync(HttpMethod.Get, path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Contains("Bearer", none.Headers.WwwAuthenticate.Select(h => h.Scheme));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(HttpMethod.Get, path, "nonsense")).StatusCode);

        // a console's signed management token is another audience: it is not a credential here
        var console = new ConsoleIdentity("console-1", ConsoleSigningKey.Generate());
        var foreign = console.Issuer().Issue(new ConsoleTokenRequest("some-instance", "someone", "Someone", ServerRole.Admin));
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(HttpMethod.Get, path, foreign)).StatusCode);

        // a report server's data source: the address as the user name, the personal access token as the password
        using var basic = await CallAsync(HttpMethod.Get, path, token, basicUser: "ada@example.invalid");
        await OkAsync(basic);
        Assert.Equal(HttpStatusCode.Unauthorized, (await CallAsync(HttpMethod.Get, path, "wrong", basicUser: "ada@example.invalid")).StatusCode);

        var entries = new DbDataGateway.Server.State.StateAuditLog(Get<StateDatabase>()).Read(50).Where(e => e.Command == "feed.run").ToList();
        var entry = Assert.Single(entries);
        Assert.Equal($"{Model}/revenue_in_quarter", entry.Target);
        Assert.Contains("parameters: quarter", entry.Outcome);
        Assert.DoesNotContain("FY27-Q1", entry.Outcome);
        Assert.Equal("Ada", entry.UserName);
    }

    [Fact]
    public async Task With_the_module_not_listed_nothing_listens_on_its_port_and_a_clash_of_ports_is_refused_naming_the_setting()
    {
        await StartAsync(feeds: false);
        using var http = Trusting();
        await Assert.ThrowsAnyAsync<HttpRequestException>(() => http.GetAsync($"https://127.0.0.1:{feedsPort}/health"));
        await app!.StopAsync();
        await app.DisposeAsync();
        app = null;

        var port = FreePort();
        var refused = await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            app = GatewayHost.Build([$"--Gateway:DataDirectory={dataDirectory}", "--Gateway:Modules:0=flightsql", "--Gateway:Modules:1=feeds", "--Gateway:Listen:Address=127.0.0.1", $"--Gateway:Listen:Port={port}", "--Gateway:Rest:Address=127.0.0.1", $"--Gateway:Rest:Port={port}"]);
            await app.StartAsync();
        });
        Assert.Contains("feeds module has its own port", refused.ToString());
    }
}
