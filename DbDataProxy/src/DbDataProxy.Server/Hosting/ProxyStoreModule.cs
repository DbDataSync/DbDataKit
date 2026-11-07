using System.Net;
using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.State;
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
/// in the group's state, when the model asks for approval of the caller's writes. The callers are the identity home's people and service accounts (<see cref="StoreCallers"/>). What each call may do is <see cref="GroupStoreApi"/>'s,
/// which the apps host answers an app's data calls with too (O08c).
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
        app.MapGet("/health/live", () => Results.Text("Healthy"));
        Map(app, callers, ApiOf(options, state, secrets, models));
        await app.StartAsync(cancellationToken);
        _app = app;
    }

    /// <summary>The group's store for this instance: what the module's listener and the apps host's data calls answer with.</summary>
    public static GroupStoreApi ApiOf(IOptionsMonitor<ProxyOptions> options, StateDatabase state, SecretsRuntime secrets, PublishedEntityModels models) =>
        new(() => options.CurrentValue, secrets, models, new GroupChangeRequests(state));

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
        var users = new IdentityUserStore(state);
        var directory = new HomeDirectory(state);
        return new OpenIdProvider(state, users, directory, new OidcClients(state), new SigningKeys(state, KeySigningKeyProtector.FromBase64(held.RevealString())),
            ProxyIdentityModule.PublicUrlOf(current).AbsoluteUri);
    }

    private void Map(WebApplication app, StoreCallers callers, GroupStoreApi api)
    {
        const string prefix = "/api/v1/models";

        // who is calling; what they may do is the API's to say, the same for the apps host's calls
        async Task<IResult> As(HttpContext context, Func<StoreCaller, Task<StoreAnswer>> call)
        {
            if (callers.Authenticate(context.Request.Headers.Authorization.ToString()) is not { } caller)
                return Results.Json(new { error = "A token of the identity home is needed (a personal access token, or an access token it issued)." }, statusCode: StatusCodes.Status401Unauthorized);
            var answer = await call(caller);
            return Results.Json(answer.Body, statusCode: answer.Status);
        }
        static IReadOnlyDictionary<string, string> Query(HttpContext context) => context.Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
        static async Task<byte[]> Body(HttpContext context)
        {
            using var buffer = new MemoryStream();
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
            return buffer.ToArray();
        }

        app.MapGet(prefix, (Func<HttpContext, Task<IResult>>)(context => As(context, caller => Task.FromResult(api.List(caller)))));
        app.MapGet(prefix + "/{name}/entities/{entity}/row", (string name, string entity, HttpContext context) =>
            As(context, caller => api.RowAsync(caller, name, entity, Query(context), context.RequestAborted)));
        app.MapGet(prefix + "/{name}/entities/{entity}/rows", (string name, string entity, HttpContext context) =>
            As(context, caller => api.RowsAsync(caller, name, entity, Query(context), context.RequestAborted)));
        app.MapPost(prefix + "/{name}/changes", (string name, HttpContext context) =>
            As(context, async caller => await api.ChangesAsync(caller, name, await Body(context), mayWrite: null, context.RequestAborted)));
        app.MapGet(prefix + "/{name}/requests", (string name, HttpContext context) =>
            As(context, caller => Task.FromResult(api.Requests(caller, name, context.Request.Query["state"].ToString()))));
        app.MapGet(prefix + "/{name}/requests/{id}", (string name, string id, HttpContext context) =>
            As(context, caller => Task.FromResult(api.Request(caller, name, id))));
        app.MapPost(prefix + "/{name}/requests/{id}/approve", (string name, string id, HttpContext context) =>
            As(context, async caller => await api.DecideAsync(caller, name, id, await Body(context), approve: true, context.RequestAborted)));
        app.MapPost(prefix + "/{name}/requests/{id}/reject", (string name, string id, HttpContext context) =>
            As(context, async caller => await api.DecideAsync(caller, name, id, await Body(context), approve: false, context.RequestAborted)));
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
