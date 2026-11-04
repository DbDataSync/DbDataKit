using System.Data.Common;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Store;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;
using DbDataProxy.Server.Serving;
using DbDataProxy.Server.Tls;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// The <c>store</c> module of the serving layer (plan 20 O08a): master data read and written through the models a DbDataStore server published to the group (O10), straight to the model's tables, so it goes on with the store's
/// server stopped. On an HTTPS listener of its own, with the paths a DbDataStore server answers (<c>/api/v1/models/{model}/entities/{entity}/row|rows</c>, <c>/changes</c>, <c>/requests</c>), so a client is written once:
/// a row read now, as of a date or with its history; a page of rows; a batch of changes through <see cref="ModelWriter"/> (every rule, version check and audit row; a refusal names each rule broken); and a change request,
/// in the group's state, when the model asks for approval of the caller's writes. The callers are the identity home's people and service accounts (<see cref="StoreCallers"/>).
/// </summary>
public sealed class ProxyStoreModule(IOptionsMonitor<ProxyOptions> options, StateDatabase state, TlsCertificateHolder certificate, SecretsRuntime secrets, PublishedEntityModels models, ILoggerFactory loggers,
    GroupCertificate? group = null) : IHostedService, IAsyncDisposable
{
    private WebApplication? _app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (certificate.Current is null)
            certificate.Set(CertificateProvider.Load(current, ProxyHost.ResolveDataDirectory(current), loggers.CreateLogger("Tls"), secrets: secrets, group: group));

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Parse(current.Store.Address), current.Store.Port, listen =>
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
        var callers = new StoreCallers(state, ServerIdOf(current, state), await HomeTokensAsync(current, cancellationToken));
        var requests = new GroupChangeRequests(state);
        app.MapGet("/health/live", () => Results.Text("Healthy"));
        Map(app, callers, requests);
        await app.StartAsync(cancellationToken);
        _app = app;
    }

    /// <summary>The group's server id, which the home's roles name (<c>server:&lt;id&gt;</c>): the instance id every instance of the group shares (O04a).</summary>
    public static string ServerIdOf(ProxyOptions options, StateDatabase state)
    {
        var dataDirectory = ProxyHost.ResolveDataDirectory(options);
        return new PairingFiles(ProxyConfigFile.PathIn(dataDirectory), Path.Combine(dataDirectory, "pairing-codes.json"), State.ProxyState.IsGroup(state) ? state : null).InstanceId("proxy", "dbdataproxy server");
    }

    /// <summary>The home's access tokens are checked against its signing keys when this instance holds their key (the identity module's secret); without it, personal access tokens only. Nothing is made here.</summary>
    private async Task<OpenIdProvider?> HomeTokensAsync(ProxyOptions current, CancellationToken cancellationToken)
    {
        using var held = await SecretsRuntime.OpenStore(current).TryResolveAsync(SecretRef.Parse("secret:" + ProxyIdentityModule.SigningKeySecret), cancellationToken);
        if (held is null) return null;
        var users = new UserStore(state);
        var directory = new HomeDirectory(state);
        return new OpenIdProvider(state, users, directory, new OidcClients(state), new SigningKeys(state, KeySigningKeyProtector.FromBase64(held.RevealString())),
            ProxyIdentityModule.PublicUrlOf(current).AbsoluteUri);
    }

    private void Map(WebApplication app, StoreCallers callers, GroupChangeRequests requests)
    {
        const string prefix = "/api/v1/models";

        // who is calling, with the role a call needs, and the model it is about as this instance serves it
        (StoreCaller? Caller, IResult? Problem) Who(HttpContext context, ServerRole needs)
        {
            if (callers.Authenticate(context.Request.Headers.Authorization.ToString()) is not { } caller)
                return (null, Results.Json(new { error = "A token of the identity home is needed (a personal access token, or an access token it issued)." }, statusCode: StatusCodes.Status401Unauthorized));
            if (!caller.Holds(needs) && !caller.Holds(ServerRole.Admin))
                return (null, Results.Json(new { error = $"This needs the {needs.ToString().ToLowerInvariant()} role on this group." }, statusCode: StatusCodes.Status403Forbidden));
            return (caller, null);
        }
        (ServedEntityModel? Model, IResult? Problem) Served(string name, string? entity = null)
        {
            if (models.Find(name) is not { } served) return (null, Results.NotFound(new { error = $"No entity model '{name}' is published to this group." }));
            if (entity is not null && served.Model.Entity(entity) is null) return (null, Results.NotFound(new { error = $"The model '{name}' has no entity '{entity}'." }));
            return (served, null);
        }

        app.MapGet(prefix, (HttpContext context) =>
        {
            var (_, problem) = Who(context, ServerRole.Viewer);
            if (problem is not null) return problem;
            return Results.Json(new JsonObject
            {
                ["models"] = new JsonArray([.. models.All.OrderBy(m => m.Model.Name, StringComparer.Ordinal).Select(m => (JsonNode)new JsonObject
                {
                    ["name"] = m.Model.Name, ["title"] = m.Model.Title, ["version"] = m.Version, ["entities"] = new JsonArray([.. m.Model.Entities.Select(e => (JsonNode)e.Name)]),
                })]),
            });
        });

        // one row by its key: ?key.<attribute>=<value>; ?as_of=<time> the version that held then; ?history=true every version
        app.MapGet(prefix + "/{name}/entities/{entity}/row", async (string name, string entity, HttpContext context) =>
        {
            var (_, denied) = Who(context, ServerRole.Viewer);
            if (denied is not null) return denied;
            var (served, missing) = Served(name, entity);
            if (missing is not null) return missing;
            var key = context.Request.Query.Where(q => q.Key.StartsWith("key.", StringComparison.Ordinal)).ToDictionary(q => q.Key[4..], q => (object?)q.Value.ToString());
            return await WithWriter(served!, context, async writer =>
            {
                if (context.Request.Query["history"] == "true")
                    return Results.Json(new JsonObject { ["versions"] = new JsonArray([.. (await writer.HistoryAsync(entity, key, context.RequestAborted)).Select(r => (JsonNode)RowJson.Row(r))]) });
                StoredRow? row;
                if (context.Request.Query["as_of"].ToString() is { Length: > 0 } asOf)
                {
                    if (!DateTimeOffset.TryParse(asOf, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var moment)) return Results.BadRequest(new { error = $"'{asOf}' is not a time." });
                    row = await writer.GetAsOfAsync(entity, key, moment, context.RequestAborted);
                }
                else row = await writer.GetAsync(entity, key, context.RequestAborted);
                return row is null ? Results.NotFound(new { error = $"`{entity}` has no such row." }) : Results.Json(RowJson.Row(row));
            });
        });

        // a page of rows in key order: ?limit=100&offset=0
        app.MapGet(prefix + "/{name}/entities/{entity}/rows", async (string name, string entity, HttpContext context) =>
        {
            var (_, denied) = Who(context, ServerRole.Viewer);
            if (denied is not null) return denied;
            var (served, missing) = Served(name, entity);
            if (missing is not null) return missing;
            if (!int.TryParse(context.Request.Query["limit"].ToString() is { Length: > 0 } l ? l : "100", out var limit) || !int.TryParse(context.Request.Query["offset"].ToString() is { Length: > 0 } o ? o : "0", out var offset))
                return Results.BadRequest(new { error = "limit and offset are whole numbers." });
            return await WithWriter(served!, context, async writer =>
                Results.Json(new JsonObject { ["rows"] = new JsonArray([.. (await writer.ListAsync(entity, limit, offset, context.RequestAborted)).Select(r => (JsonNode)RowJson.Row(r))]), ["limit"] = limit, ["offset"] = offset }));
        });

        // { "changes": [...], "description": "..." }: written at once, or kept as a change request (202) when the model asks for approval of this caller's writes
        app.MapPost(prefix + "/{name}/changes", async (string name, HttpContext context) =>
        {
            var (caller, denied) = Who(context, ServerRole.Author);
            if (denied is not null) return denied;
            var (served, missing) = Served(name);
            if (missing is not null) return missing;
            JsonNode? body;
            try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { return Results.BadRequest(new { error = "The body is not JSON." }); }
            if (body?["changes"] is not JsonArray list || list.Count == 0) return Results.BadRequest(new { error = "The body needs a non-empty \"changes\" array." });
            var (changes, bad) = RowJson.Changes(list);
            if (bad is not null) return Results.BadRequest(new { error = bad });
            if (ChangeApproval.Needs(served!.Model, caller!.IsAgent))
            {
                var request = requests.Propose(name, caller, (string?)body["description"], (JsonArray)JsonNode.Parse(list.ToJsonString())!);
                return Results.Json(request.ToJson(withChanges: false), statusCode: StatusCodes.Status202Accepted);
            }
            return await WithWriter(served, context, async writer =>
            {
                await writer.ApplyAsync(changes!, new WriteActor(caller.UserId, caller.Name), context.RequestAborted);
                return Results.Json(new { applied = changes!.Count });
            });
        });

        app.MapGet(prefix + "/{name}/requests", (string name, HttpContext context) =>
        {
            var (_, denied) = Who(context, ServerRole.Viewer);
            if (denied is not null) return denied;
            var wanted = context.Request.Query["state"].ToString();
            if (wanted.Length > 0 && wanted is not (GroupChangeRequest.Pending or GroupChangeRequest.Approved or GroupChangeRequest.Rejected)) return Results.BadRequest(new { error = "state is pending, approved or rejected." });
            return Results.Json(new JsonObject { ["requests"] = new JsonArray([.. requests.List(name, wanted.Length > 0 ? wanted : null).Select(r => (JsonNode)r.ToJson(withChanges: false))]) });
        });

        app.MapGet(prefix + "/{name}/requests/{id}", (string name, string id, HttpContext context) =>
        {
            var (_, denied) = Who(context, ServerRole.Viewer);
            if (denied is not null) return denied;
            return requests.Find(name, id) is { } request ? Results.Json(request.ToJson()) : Results.NotFound(new { error = "There is no such change request." });
        });

        async Task<IResult> Decide(string name, string id, HttpContext context, bool approve)
        {
            var (served, missing) = Served(name);
            if (missing is not null) return missing;
            var (caller, denied) = Who(context, ChangeApproval.AdministratorDecides(served!.Model) ? ServerRole.Admin : ServerRole.Author);
            if (denied is not null) return denied;
            if (caller!.IsAgent) return Results.Json(new { error = "An agent cannot decide a change request." }, statusCode: StatusCodes.Status403Forbidden);
            if (requests.Find(name, id) is not { } request) return Results.NotFound(new { error = "There is no such change request." });
            if (request.State != GroupChangeRequest.Pending) return Results.Json(new { error = $"This change request was already {request.State}." }, statusCode: StatusCodes.Status409Conflict);
            JsonNode? body = null;
            if (context.Request.ContentLength is > 0)
            {
                try { body = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
                catch (JsonException) { return Results.BadRequest(new { error = "The body is not JSON." }); }
            }
            var note = (string?)body?["note"];
            if (!approve)
                return requests.Decide(request, GroupChangeRequest.Rejected, caller, note) ? Results.Json(requests.Find(name, id)!.ToJson(withChanges: false))
                    : Results.Json(new { error = "This change request was decided meanwhile." }, statusCode: StatusCodes.Status409Conflict);
            var (changes, bad) = RowJson.Changes(request.Changes);
            if (bad is not null) return Results.BadRequest(new { error = bad });
            return await WithWriter(served, context, async writer =>
            {
                await writer.ApplyAsync(changes!, new WriteActor(caller.UserId, $"{caller.Name ?? caller.UserId}, approving {request.ProposedByName ?? request.ProposedBy}'s change request {request.Id}"), context.RequestAborted);
                requests.Decide(request, GroupChangeRequest.Approved, caller, note);
                return Results.Json(requests.Find(name, id)!.ToJson(withChanges: false));
            });
        }
        app.MapPost(prefix + "/{name}/requests/{id}/approve", (string name, string id, HttpContext context) => Decide(name, id, context, approve: true));
        app.MapPost(prefix + "/{name}/requests/{id}/reject", (string name, string id, HttpContext context) => Decide(name, id, context, approve: false));
    }

    /// <summary>Opens the model's data source (published with it, O04b) and runs <paramref name="work"/> with a writer on the model's tables; the writer's refusals become the answers a DbDataStore server gives.</summary>
    private async Task<IResult> WithWriter(ServedEntityModel served, HttpContext context, Func<ModelWriter, Task<IResult>> work)
    {
        var current = options.CurrentValue;
        if (!current.DataSources.TryGetValue(served.DataSource, out var source))
            return Results.Json(new { error = $"The data source '{served.DataSource}' the model's tables are on is not served here." }, statusCode: StatusCodes.Status502BadGateway);
        var engine = source.Provider.ToLowerInvariant() switch { "sqlserver" => "sqlserver", "postgres" => "postgres", _ => null };
        if (engine is null) return Results.Json(new { error = $"The data source '{served.DataSource}' is {source.Provider}; a model's tables are on SQL Server or PostgreSQL." }, statusCode: StatusCodes.Status502BadGateway);
        string text;
        try { text = secrets.ResolveConnectionString($"Proxy:DataSources:{served.DataSource}:ConnectionString", source.ConnectionString, source.Provider) ?? source.ConnectionString; }
        catch (Exception ex) when (ex is SecretNotFoundException or InvalidOperationException)
        { return Results.Json(new { error = $"The login of the data source '{served.DataSource}' cannot be found here." }, statusCode: StatusCodes.Status502BadGateway); }
        try
        {
            await using DbConnection connection = engine == "sqlserver" ? new Microsoft.Data.SqlClient.SqlConnection(text) : new Npgsql.NpgsqlConnection(text);
            await connection.OpenAsync(context.RequestAborted);
            return await work(new ModelWriter(served.Model, connection, engine, schema: served.Model.Name));
        }
        catch (WriteRefusedException refused) { return Results.Json(RowJson.Refusal(refused), statusCode: StatusCodes.Status422UnprocessableEntity); }
        catch (VersionConflictException conflict) { return Results.Json(new { error = conflict.Message, expected_version = conflict.Expected, current_version = conflict.Current }, statusCode: StatusCodes.Status409Conflict); }
        catch (RowNotFoundException ex) { return Results.NotFound(new { error = ex.Message }); }
        catch (Exception ex) when (ex is StoreNotSupportedException or DbDataKit.DataModel.Storage.StorageException) { return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status422UnprocessableEntity); }
        catch (DbException) { return Results.Json(new { error = $"The database of the data source '{served.DataSource}' could not be reached." }, statusCode: StatusCodes.Status502BadGateway); }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) await _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
