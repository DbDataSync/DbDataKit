using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json.Nodes;
using DbDataGateway.Client;
using DbDataKit.Apps;
using DbDataKit.Serving;
using DbDataKit.Serving.Conformance;
using DbDataKit.Serving.Semantic;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Serving;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Npgsql;

namespace DbDataGateway.Tests.Serving;

/// <summary>A module of the rig: it asks a question as a named person and answers in the form the suite compares.</summary>
internal abstract class RigModule(ModuleAccessRig rig, string name) : IConformanceModule
{
    protected ModuleAccessRig Rig { get; } = rig;

    public string Name { get; } = name;

    public abstract bool Asks(ConformanceQuestion question);

    public abstract Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default);

    protected RigPerson Person(string name) => Rig.People[name];

    /// <summary>The answer of a REST-shaped route: a 2xx is its rows, a refusal of the serving layer's kinds (400, 401, 403, 404, 422) is a refusal, anything else is not an answer and fails the test.</summary>
    protected static ModuleAnswer OfHttp(HttpStatusCode status, string text)
    {
        if ((int)status is >= 200 and < 300) return ModuleAccessRig.RowsOf(JsonNode.Parse(text)!);
        if (status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.UnprocessableEntity) return ModuleAnswer.Refusal($"{(int)status}: {text}");
        throw new InvalidOperationException($"{(int)status}: {text}");
    }
}

/// <summary>The reference: the serving core's own answer, the semantic request run through <see cref="IServingQueries"/> as the person.</summary>
internal sealed class CoreModule(ModuleAccessRig rig) : RigModule(rig, "core")
{
    public override bool Asks(ConformanceQuestion question) => true;

    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        try { return await ModuleAccessRig.RowsOf(await Rig.Get<IServingServices>().Queries.RunAsync(Rig.ContextOf(person), new ServingRequest.Semantic(SemanticModels.SourcePrefix + ModuleAccessRig.Model, ModuleAccessRig.AskOf(question).Json), cancellationToken), cancellationToken); }
        catch (ServingException e) when (e is not ServingBackendException) { return ModuleAnswer.Refusal(e.Message); }
    }
}

/// <summary>The Flight SQL module through a real client: a semantic request, or semantic SQL, sent as a statement against <c>semantic:orders_model</c>.</summary>
internal sealed class FlightModule(ModuleAccessRig rig, bool sql) : RigModule(rig, sql ? "flightsql (semantic sql)" : "flightsql (semantic request)")
{
    public override bool Asks(ConformanceQuestion question) => sql ? ModuleAccessRig.AskOf(question).Sql is not null : true;

    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        var ask = ModuleAccessRig.AskOf(question);
        var p = Person(person);
        using var client = new GatewayFlightClient(Rig.FlightAddress, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler { SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true } } });
        try
        {
            await client.AuthenticateAsync(p.Address, p.Token);
            var result = await client.QueryAsync(sql ? ask.Sql! : ask.Json, new QueryOptions { DataSource = "semantic:" + ModuleAccessRig.Model }, cancellationToken);
            var rows = new List<string>();
            await foreach (var batch in result)
                for (var r = 0; r < batch.Length; r++)
                    rows.Add(ModuleAnswer.Row(Enumerable.Range(0, batch.ColumnCount).Select(c => ArrowCell(batch.Column(c), r)).ToArray()));
            return ModuleAnswer.Of(rows);
        }
        catch (Exception ex) when (Rpc(ex) is { StatusCode: StatusCode.PermissionDenied or StatusCode.InvalidArgument or StatusCode.NotFound or StatusCode.Unauthenticated } rpc)
        {
            return ModuleAnswer.Refusal(rpc.Status.Detail);
        }
    }

    private static RpcException? Rpc(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException) if (e is RpcException rpc) return rpc;
        return null;
    }

    private static object? ArrowCell(Apache.Arrow.IArrowArray array, int row) => array.IsNull(row) ? null : array switch
    {
        Apache.Arrow.StringArray s => s.GetString(row),
        Apache.Arrow.DoubleArray d => d.GetValue(row)!.Value,
        Apache.Arrow.Decimal128Array m => m.GetValue(row)!.Value,
        Apache.Arrow.Int64Array l => l.GetValue(row)!.Value,
        Apache.Arrow.Int32Array n => n.GetValue(row)!.Value,
        _ => array.GetType().Name,
    };
}

/// <summary>The <c>feeds</c> module over its HTTPS listener: a semantic request, semantic SQL and a saved query, as the person behind the token.</summary>
internal sealed class FeedsModule(ModuleAccessRig rig, string mode) : RigModule(rig, $"feeds ({mode})")
{
    public override bool Asks(ConformanceQuestion question) => mode switch
    {
        "request" => question.Id != "members",       // the route runs queries; a members listing is not one
        "sql" => ModuleAccessRig.AskOf(question).Sql is not null,
        _ => ModuleAccessRig.AskOf(question).Saved is not null,
    };

    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        var ask = ModuleAccessRig.AskOf(question);
        using var http = ModuleAccessRig.Trusting();
        using var request = mode == "saved"
            ? new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{Rig.FeedsPort}/api/v1/queries/{ModuleAccessRig.Model}/{ask.Saved}")
            : new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{Rig.FeedsPort}/api/v1/query")
            {
                Content = new StringContent(new JsonObject { ["model"] = ModuleAccessRig.Model, [mode == "sql" ? "sql" : "request"] = mode == "sql" ? ask.Sql : JsonNode.Parse(ask.Json) }.ToJsonString(), Encoding.UTF8, "application/json"),
            };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Person(person).Token);
        using var response = await http.SendAsync(request, cancellationToken);
        return OfHttp(response.StatusCode, await response.Content.ReadAsStringAsync(cancellationToken));
    }
}

/// <summary>An app's semantic call as the apps host hands it to the group (<see cref="GroupAppSemantic"/>), as the person the app's token names.</summary>
internal sealed class AppsSemanticModule(ModuleAccessRig rig) : RigModule(rig, "apps host (semantic call)")
{
    public override bool Asks(ConformanceQuestion question) => question.Id != "members";

    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        var host = new GroupAppSemantic(Rig.Get<IServingServices>(), Rig.Get<SemanticModels>(), Rig.Get<HomeCallers>(), Rig.Get<IOptionsMonitor<GatewayOptions>>());
        var p = Person(person);
        var answer = await host.SendAsync(new AppSemanticCall("conformance-app", p.UserId, p.Name, ServerRole.Viewer, [ModuleAccessRig.Model], AppSemanticRoute.Query, ModuleAccessRig.Model, Encoding.UTF8.GetBytes(ModuleAccessRig.AskOf(question).Json)), cancellationToken);
        return OfHttp((HttpStatusCode)answer.Status, Encoding.UTF8.GetString(answer.Body));
    }
}

/// <summary>The <c>mcp</c> module's read tool <c>query_semantic</c>, as the agent's person.</summary>
internal sealed class McpSemanticModule(ModuleAccessRig rig) : RigModule(rig, "mcp (query_semantic)")
{
    public override bool Asks(ConformanceQuestion question) => question.Id != "members";

    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        using var http = ModuleAccessRig.Trusting();
        var call = new JsonObject
        {
            ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "tools/call",
            ["params"] = new JsonObject { ["name"] = "query_semantic", ["arguments"] = new JsonObject { ["model"] = ModuleAccessRig.Model, ["request"] = JsonNode.Parse(ModuleAccessRig.AskOf(question).Json) } },
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{Rig.McpPort}/mcp") { Content = new StringContent(call.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Person(person).Token);
        using var response = await http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var result = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!["result"]!;
        return (bool)result["isError"]! ? ModuleAnswer.Refusal((string)result["content"]![0]!["text"]!) : ModuleAccessRig.RowsOf(result["structuredContent"]!);
    }
}

/// <summary>
/// The <c>views</c> module's render route over its HTTPS listener: a published dashboard drawn as the person. Its widgets are the questions: a table of revenue by region, a table of orders by customer, and a control whose values are the members of the region attribute.
/// A widget that is an error is a refusal.
/// </summary>
internal sealed class ViewsModule(ModuleAccessRig rig) : RigModule(rig, "views (dashboard render)")
{
    public override bool Asks(ConformanceQuestion question) => question.Id is "by-region" or "by-customer" or "members";

    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        using var http = ModuleAccessRig.Trusting();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://127.0.0.1:{Rig.ViewsPort}/api/v1/dashboards/orders/render") { Content = new StringContent("""{"parameters":{},"layout":"desk"}""", Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Person(person).Token);
        using var response = await http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) return OfHttp(response.StatusCode, text);
        var widgets = JsonNode.Parse(text)!["widgets"]!.AsObject();
        var widget = widgets[question.Id switch { "by-region" => "by_region", "by-customer" => "by_customer", _ => "regions" }]!;
        if ((string?)widget["type"] == "error") return ModuleAnswer.Refusal((string?)widget["message"] ?? "error");
        return question.Id == "members"
            ? ModuleAnswer.Of(widget["values"]!.AsArray().Select(v => ModuleAnswer.Row(ModuleAccessRig.JsonCell(v!["value"]))))
            : ModuleAnswer.Of(widget["rows"]!.AsArray().Select(r => ModuleAnswer.Row(r!["cells"]!.AsArray().Select(c => ModuleAccessRig.JsonCell(c!["v"])).ToArray())));
    }
}

/// <summary>
/// The views module's subscriptions (the scheduled export): a subscription of the person is due, the Gateway draws the dashboard as its subscriber and hands the print page to delivery; the rows of the page's widgets are the answer. A widget that is an error is a refusal.
/// </summary>
internal sealed class SubscriptionModule(ModuleAccessRig rig) : RigModule(rig, "views (subscription export)")
{
    public override bool Asks(ConformanceQuestion question) => question.Id is "by-region" or "by-customer" or "members";

    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        var p = Person(person);
        var state = Rig.Get<DbDataKit.State.StateDatabase>();
        var subscriptions = Rig.Get<DbDataGateway.Server.Hosting.GatewaySubscriptions>();
        var id = subscriptions.Subscriptions.Create(new DbDataKit.State.SubscriptionFields($"conformance {Guid.NewGuid():N}", "dashboard", "orders", null, null, "pdf", null, "mail", null, "0 7 * * *", "UTC", false, "email"), p.UserId, p.Name, p.UserId, DateTimeOffset.UtcNow.AddMinutes(-1));
        await subscriptions.RunPassAsync(cancellationToken);
        var message = new DbDataKit.State.DeliveryStore(state).List(source: $"subscription:{id:D}").SingleOrDefault(m => m.Attachments.Count > 0);
        if (message is null) return ModuleAnswer.Refusal("the subscription sent nothing: " + subscriptions.Subscriptions.Find(id)?.LastDetail);
        using var read = new DbDataKit.State.PayloadStore(state).OpenRead(message.Attachments[0].Payload);
        using var bytes = new MemoryStream();
        read.CopyTo(bytes);
        var page = Encoding.UTF8.GetString(bytes.ToArray());
        var match = System.Text.RegularExpressions.Regex.Match(page, "id=\"dbdatakit-print\"[^>]*>(?<json>.*?)</script>", System.Text.RegularExpressions.RegexOptions.Singleline);
        var widgets = JsonNode.Parse(System.Net.WebUtility.HtmlDecode(match.Groups["json"].Value))!["rendered"]!["widgets"]!.AsObject();
        var widget = widgets[question.Id switch { "by-region" => "by_region", "by-customer" => "by_customer", _ => "regions" }]!;
        if ((string?)widget["type"] == "error") return ModuleAnswer.Refusal((string?)widget["message"] ?? "error");
        return question.Id == "members"
            ? ModuleAnswer.Of(widget["values"]!.AsArray().Select(v => ModuleAnswer.Row(ModuleAccessRig.JsonCell(v!["value"]))))
            : ModuleAnswer.Of(widget["rows"]!.AsArray().Select(r => ModuleAnswer.Row(r!["cells"]!.AsArray().Select(c => ModuleAccessRig.JsonCell(c!["v"])).ToArray())));
    }
}

/// <summary>The negative module of GP14: it has its own connection to the data source and reads the tables itself, so no rule of the core applies to it.</summary>
internal sealed class DirectSourceModule(ModuleAccessRig rig) : RigModule(rig, "direct-source")
{
    public override bool Asks(ConformanceQuestion question) => true;

    public override Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        var sql = question.Id switch
        {
            "by-region" => "SELECT c.region, SUM(f.net_amount), COUNT(DISTINCT f.order_id) FROM fact_sales f JOIN dim_customer c ON c.customer_key = f.customer_key GROUP BY c.region",
            "total" => "SELECT SUM(net_amount), COUNT(DISTINCT order_id) FROM fact_sales",
            "members" => "SELECT DISTINCT region FROM dim_customer WHERE region IS NOT NULL",
            _ => "SELECT c.customer, COUNT(DISTINCT f.order_id) FROM fact_sales f JOIN dim_customer c ON c.customer_key = f.customer_key GROUP BY c.customer",
        };
        var rows = new List<string>();
        DbConnection connection = Rig.Engine == "mssql" ? new SqlConnection(Connection()) : new NpgsqlConnection(Connection());
        using (connection)
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            while (reader.Read()) rows.Add(ModuleAnswer.Row(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
        }

        return Task.FromResult(ModuleAnswer.Of(rows));
    }

    private string Connection() => Rig.Get<IOptionsMonitor<GatewayOptions>>().CurrentValue.DataSources[ModuleAccessRig.Source].ConnectionString;
}

/// <summary>The other negative module (UC15): it answers from a cache of its own whose key is the question, so the person who asked first decides what everyone after gets.</summary>
internal sealed class NaiveCachingModule(IConformanceModule inner) : IConformanceModule
{
    private readonly Dictionary<string, ModuleAnswer> _kept = [];

    public string Name => "naive-cache over " + inner.Name;

    public bool Asks(ConformanceQuestion question) => inner.Asks(question);

    public async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        if (_kept.TryGetValue(question.Id, out var answer)) return answer;
        return _kept[question.Id] = await inner.AskAsync(person, question, cancellationToken);
    }
}

/// <summary>Flight SQL pass-through: a statement of the person's own to the data source, not to a model. <c>by-region</c> reads the customer table the model puts rules on; <c>plain</c> reads a table no rule covers.</summary>
internal sealed class PassThroughModule(ModuleAccessRig rig) : RigModule(rig, "flightsql (statement to the data source)")
{
    public override bool Asks(ConformanceQuestion question) => question.Id is "by-region" or "plain";

    public override async Task<ModuleAnswer> AskAsync(string person, ConformanceQuestion question, CancellationToken cancellationToken = default)
    {
        var p = Person(person);
        using var client = new GatewayFlightClient(Rig.FlightAddress, new GrpcChannelOptions { HttpHandler = new SocketsHttpHandler { SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = (_, _, _, _) => true } } });
        try
        {
            await client.AuthenticateAsync(p.Address, p.Token);
            var result = await client.QueryAsync(question.Id == "plain" ? "SELECT id, note FROM plain_notes" : "SELECT region FROM dim_customer", new QueryOptions { DataSource = ModuleAccessRig.Source }, cancellationToken);
            var rows = new List<string>();
            await foreach (var batch in result)
                for (var r = 0; r < batch.Length; r++)
                    rows.Add(ModuleAnswer.Row(Enumerable.Range(0, batch.ColumnCount).Select(c => (object?)(batch.Column(c) switch
                    {
                        Apache.Arrow.StringArray s => s.IsNull(r) ? null : s.GetString(r),
                        Apache.Arrow.Int32Array n => n.GetValue(r)!.Value,
                        var other => other.GetType().Name,
                    })).ToArray()));
            return ModuleAnswer.Of(rows);
        }
        catch (RpcException rpc) when (rpc.StatusCode is StatusCode.PermissionDenied or StatusCode.InvalidArgument or StatusCode.NotFound)
        {
            return ModuleAnswer.Refusal(rpc.Status.Detail);
        }
    }
}
