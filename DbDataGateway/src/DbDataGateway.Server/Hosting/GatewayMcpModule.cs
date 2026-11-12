using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Serving;
using DbDataKit.Serving.Semantic;
using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Secrets;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.Tls;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// The <c>mcp</c> module of the serving layer (plan 20 O12): the endpoint agents use, MCP over streamable HTTP (<c>POST /mcp</c>, JSON-RPC), on an https listener of its own with the Gateway's certificate. An agent signs in with a token of the identity home
/// (a service account's personal access token, or a person's) and is the caller of every tool: what it reads is what that identity could read through Flight SQL or the store module. A tool with an effect does not run: it is recorded as an approval request
/// (<see cref="McpApprovals"/>) and runs only after a person answers it on <c>POST /api/v1/mcp/approvals/{id}/approve</c> (or <c>deny</c>); <c>GET /api/v1/mcp/approvals</c> lists them for an approver. Every request, answer and run is an audit row naming the person and the agent.
/// </summary>
public sealed class GatewayMcpModule(IOptionsMonitor<GatewayOptions> options, StateDatabase state, TlsCertificateHolder certificate, SecretsRuntime secrets, IServingServices serving, SemanticModels semantic, PublishedEntityModels models,
    GatewayViewsPublishing views, HomeCallers homeCallers, ILoggerFactory loggers, GroupCertificate? group = null) : IHostedService, IServingModule, IAsyncDisposable
{
    private WebApplication? _app;
    private volatile bool _draining;

    /// <inheritdoc />
    public string Name => GatewayModules.Mcp;

    /// <summary>The module's own approvals and tools once it started (tests drive them directly).</summary>
    public McpApprovals? Approvals { get; private set; }

    Task IServingModule.StartAsync(IServingServices services, CancellationToken cancellationToken) => StartAsync(cancellationToken);

    public Task DrainAsync(CancellationToken cancellationToken = default)
    {
        _draining = true;
        return Task.CompletedTask;
    }

    public ServingHealth Health() => ServingModuleHealth.Of(_app is not null, _draining, state, ServingHealthState.Down);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (certificate.Current is null)
            certificate.Set(CertificateProvider.Load(current, GatewayHost.ResolveDataDirectory(current), loggers.CreateLogger("Tls"), secrets: secrets, group: group));

        var store = GatewayStoreModule.ApiOf(options, state, secrets, models);
        var tools = McpTools.Create(serving, semantic, store, () => views, () => homeCallers.GetAsync().AsTask().GetAwaiter().GetResult(), options);
        var approvals = Approvals = new McpApprovals(state, () => homeCallers.GetAsync().AsTask().GetAwaiter().GetResult(), tools.ToDictionary(t => t.Name, StringComparer.Ordinal), TimeSpan.FromMinutes(current.Mcp.ApprovalMinutes));
        var server = new McpServer(tools, approvals, typeof(GatewayMcpModule).Assembly.GetName().Version?.ToString() ?? "0");

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Parse(current.Mcp.Address), current.Mcp.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    https.ServerCertificateSelector = (_, _) => certificate.Current;
                    https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                });
            });
        });
        var app = builder.Build();
        app.MapGet("/health/live", () => Results.Text("Healthy"));
        Map(app, server, approvals);
        await app.StartAsync(cancellationToken);
        _app = app;
        _draining = false;
    }

    private void Map(WebApplication app, McpServer server, McpApprovals approvals)
    {
        async Task<IResult> As(HttpContext context, Func<StoreCaller, Task<IResult>> call)
        {
            var callers = await homeCallers.GetAsync(context.RequestAborted);
            if (callers.Authenticate(context.Request.Headers.Authorization.ToString()) is not { } caller)
            {
                context.Response.Headers.WWWAuthenticate = "Bearer realm=\"dbdatagateway-mcp\"";
                return Results.Json(new { error = "A token of the identity home is needed (a personal access token, or an access token it issued)." }, statusCode: StatusCodes.Status401Unauthorized);
            }
            return await call(caller);
        }

        app.MapPost("/mcp", (Func<HttpContext, Task<IResult>>)(context => As(context, async caller =>
        {
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (System.Text.Json.JsonException) { body = null; }
            if (body is null) return Results.Json(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = null, ["error"] = new JsonObject { ["code"] = -32700, ["message"] = "The body is not JSON." } }, statusCode: StatusCodes.Status400BadRequest);
            if (body is JsonArray batch)
            {
                var answers = new JsonArray();
                foreach (var message in batch)
                    if (message is not null && await server.HandleAsync(caller, message, context.RequestAborted) is { } answer) answers.Add(answer);
                return answers.Count == 0 ? Results.StatusCode(StatusCodes.Status202Accepted) : Results.Json(answers);
            }
            return await server.HandleAsync(caller, body, context.RequestAborted) is { } one ? Results.Json(one) : Results.StatusCode(StatusCodes.Status202Accepted);
        })));
        // no stream is offered: a client that asks for one is told so
        app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

        const string prefix = "/api/v1/mcp/approvals";
        app.MapGet(prefix, (Func<HttpContext, Task<IResult>>)(context => As(context, caller =>
            Task.FromResult(!McpApprovals.MayAnswer(caller)
                ? Results.Json(new { error = "Listing approval requests needs the approver or admin role on this server." }, statusCode: StatusCodes.Status403Forbidden)
                : Results.Json(new JsonObject { ["approvals"] = new JsonArray([.. approvals.List(pendingOnly: context.Request.Query["state"] != "all").Select(a => (JsonNode)McpApprovals.Json(a))]) })))));
        app.MapGet(prefix + "/{id}", (string id, HttpContext context) => As(context, caller =>
            Task.FromResult(!McpApprovals.MayAnswer(caller)
                ? Results.Json(new { error = "Reading an approval request needs the approver or admin role on this server." }, statusCode: StatusCodes.Status403Forbidden)
                : Guid.TryParse(id, out var guid) && approvals.Get(guid) is { } row ? Results.Json(McpApprovals.Json(row)) : Results.Json(new { error = "There is no such approval request." }, statusCode: StatusCodes.Status404NotFound))));
        app.MapPost(prefix + "/{id}/approve", (string id, HttpContext context) => Decide(context, id, approve: true));
        app.MapPost(prefix + "/{id}/deny", (string id, HttpContext context) => Decide(context, id, approve: false));

        Task<IResult> Decide(HttpContext context, string id, bool approve) => As(context, async caller =>
        {
            string? note = null;
            if (context.Request.ContentLength is not 0)
            {
                try { note = (await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted))?["note"]?.ToString(); }
                catch (System.Text.Json.JsonException) { }
            }
            if (!Guid.TryParse(id, out var guid)) return Results.Json(new { error = "There is no such approval request." }, statusCode: StatusCodes.Status404NotFound);
            var decision = await approvals.DecideAsync(caller, guid, approve, note, context.RequestAborted);
            return Results.Json(decision.Body, statusCode: decision.Status);
        });
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _draining = true;
        if (_app is { } running)
        {
            _app = null;
            await running.StopAsync(cancellationToken);
            await running.DisposeAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
