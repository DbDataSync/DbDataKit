using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DbDataKit.Semantic;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataKit.State.Tests;
using DbDataKit.Views.Export;
using DbDataKit.Views.Tests;

namespace DbDataKit.Views.Hosting.Tests;

/// <summary>Callers by bearer token: the people and the apps of a test. Nothing is checked beyond the lookup; the identity home's tokens are the host's business.</summary>
internal sealed class TestCallers : IViewsCallers
{
    public static readonly ServingCaller All = new("u-all", "alice", new HashSet<string> { "analyst" }, new Dictionary<string, string>());
    public static readonly ServingCaller West = new("u-west", "wendy", new HashSet<string> { "analyst" }, new Dictionary<string, string> { ["region"] = "West" });

    public Dictionary<string, ViewsPrincipal> Tokens { get; } = new()
    {
        ["all"] = new(All),
        ["west"] = new(West),
        // an app's token: the person is Wendy, and the app's manifest names the model the dashboard queries
        ["app-sales"] = new(West, new ViewsAppScope("portal", ["sales_catalog"])),
        ["app-other"] = new(West, new ViewsAppScope("budget", ["other_model"])),
        ["app-none"] = new(All, new ViewsAppScope("empty", [])),
    };

    public ValueTask<ViewsPrincipal?> AuthenticateAsync(string? authorization, string? clientAddress, CancellationToken cancellationToken) =>
        ValueTask.FromResult(authorization is not null && authorization.StartsWith("Bearer ", StringComparison.Ordinal) && Tokens.TryGetValue(authorization[7..], out var p) ? p : null);
}

internal sealed class ThrowingAudit : IViewsAudit
{
    public void Record(AuditEvent entry) => throw new InvalidOperationException("the audit database is gone");
}

internal sealed record Reply(int Status, JsonNode? Body, HttpResponseHeaders Headers)
{
    public JsonObject Object => (JsonObject)Body!;

    public string Text => Body?.ToJsonString() ?? "";
}

/// <summary>
/// The module as a host runs it: a group's state database on the engine under test (definitions, files, audit), the kinds registered in the publisher's registry, the module with its own listener over the semantic engine on the conformance host,
/// followed from the state, and a client that speaks to it over HTTP with a bearer token.
/// </summary>
internal sealed class ViewsHarness : IAsyncDisposable
{
    public const string Model = "sales_catalog";

    public static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-08T12:00:00Z");

    private readonly HttpClient http = new();

    public StateTestClock Clock { get; } = new(Start);
    public ManualTimeProvider ModuleClock { get; } = new(Start);
    public ConformanceHost Host { get; } = new();
    public DefinitionPublisher Publisher { get; private set; } = null!;
    public DefinitionStore Store { get; private set; } = null!;
    public PayloadStore Payloads { get; private set; } = null!;
    public AuditLog Audit { get; private set; } = null!;
    public TestCallers Callers { get; } = new();
    public ViewsPublications Published { get; private set; } = null!;
    public ViewsModule Module { get; private set; } = null!;
    public string Address => Module.Address!;

    public static string CatalogText => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog.yaml"));

    public static string ModelContent => new JsonObject { ["catalog"] = CatalogText }.ToJsonString();

    public static async Task<ViewsHarness> StartAsync(StateEngines engines, string engine, TimeSpan? cacheTtl = null, IViewsAudit? audit = null, bool publish = true, bool useModuleClock = false, bool follow = true, IReportExporter? exporter = null, string? pageAssets = null)
    {
        var h = new ViewsHarness();
        var database = engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module, AuditModule.Module], new StateScope(Group: "main"), time: h.Clock);
        h.Store = new DefinitionStore(database);
        h.Payloads = new PayloadStore(database);
        h.Audit = new AuditLog(database, "views");
        var kinds = new DefinitionKinds().Register(new DefinitionKind("semantic-model", ["serving"], PublishMode.Serve) { Formats = DefinitionFormats.Of(2) });
        ViewsPublications.Register(kinds, h.Store, h.Payloads);
        h.Publisher = new DefinitionPublisher(database, kinds, h.Payloads, "serving");
        h.Published = new ViewsPublications(h.Store, h.Publisher, h.Payloads);
        if (publish) h.PublishAll();

        var catalog = Catalog.Load(Path.Combine(AppContext.BaseDirectory, "conformance", "catalog.yaml"));
        var services = new HostedServices(h.Host).WithSemantic(new SemanticModels().Add(catalog, new Dictionary<string, string> { ["sqlserver_sales"] = "warehouse" }));
        var options = new ViewsOptions
        {
            CacheTtl = cacheTtl ?? TimeSpan.Zero, Time = useModuleClock ? h.ModuleClock : TimeProvider.System, Follow = follow, PollInterval = TimeSpan.FromHours(1), DrainTimeout = TimeSpan.FromSeconds(20),
            Resolver = new ResolverOptions { WidgetTimeout = TimeSpan.FromSeconds(20) }, Exporter = exporter, PageAssets = pageAssets,
        };
        h.Module = new ViewsModule(options, h.Published, h.Callers, audit ?? new StateViewsAudit(h.Audit));
        await h.Module.StartAsync(services);
        return h;
    }

    public void PublishAll()
    {
        Publisher.Publish(new PublishRequest("semantic-model", Model, ModelContent, "ada", "prod", PublishMode.Serve));
        Publisher.Publish(new PublishRequest("dashboard", "sales", Dashboard(), "ada", "prod", PublishMode.Serve, [$"semantic-model:{Model}@1"]));
        Publisher.Publish(new PublishRequest("report", "summary", Report(), "ada", "prod", PublishMode.Serve, [$"semantic-model:{Model}@1"]));
    }

    public static string Dashboard(string title = "Sales") => $$"""
        schemaVersion: 1
        kind: dashboard
        title: {{title}}
        parameters:
          - {name: region, type: string, for: customers.region, many: true, default: []}
        widgets:
          regions:
            type: control
            parameter: region
            control: multiselect
            values: {attribute: customers.region}
          revenue:
            type: metric
            label: Revenue
            encodings: {value: orders.net_revenue}
            query:
              model: {{Model}}
              filters: [{attribute: customers.region, operator: equals, values: {param: region} }]
          by_region:
            type: table
            encodings:
              columns: [{field: customers.region}, {field: orders.net_revenue}]
            query: {model: {{Model}}, limit: 100}
        layouts:
          - name: desk
            type: reflow
            default: true
            grid: {columns: 12, rows: 6}
            tiles:
              - {widget: regions, x: 0, y: 0, w: 12, h: 1}
              - {widget: revenue, x: 0, y: 1, w: 4, h: 2}
              - {widget: by_region, x: 4, y: 1, w: 8, h: 5}
          - name: kpi
            type: reflow
            when: {width: {max: 600} }
            grid: {columns: 12, rows: 3}
            tiles:
              - {widget: regions, x: 0, y: 0, w: 12, h: 1}
              - {widget: revenue, x: 0, y: 1, w: 12, h: 2}
        """;

    public static string Report() => $$"""
        schemaVersion: 1
        kind: report
        title: Summary
        widgets:
          revenue:
            type: metric
            label: Revenue
            encodings: {value: orders.net_revenue}
            query: {model: {{Model}}}
        layouts:
          - name: print
            type: flow
            default: true
            sections:
              - title: Summary
                blocks:
                  - {widget: revenue}
        """;

    public async Task<Reply> SendAsync(HttpMethod method, string path, string? token, JsonNode? body = null)
    {
        using var request = new HttpRequestMessage(method, Address + path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new Reply((int)response.StatusCode, text.Length > 0 && text.StartsWith('{') ? JsonNode.Parse(text) : null, response.Headers);
    }

    /// <summary>A GET whose answer is not JSON (a PDF, a page, a font): the status, the bytes and the headers.</summary>
    public async Task<(int Status, byte[] Bytes, HttpResponseMessage Response)> GetBytesAsync(string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Address + path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response);
    }

    public Task<Reply> GetAsync(string path, string? token) => SendAsync(HttpMethod.Get, path, token);

    public Task<Reply> RenderAsync(string token, JsonNode? body = null, string kind = "dashboards", string name = "sales") =>
        SendAsync(HttpMethod.Post, $"/api/v1/{kind}/{name}/render", token, body ?? new JsonObject());

    public async ValueTask DisposeAsync()
    {
        await Module.StopAsync();
        await Module.DisposeAsync();
        http.Dispose();
        Host.Dispose();
    }
}
