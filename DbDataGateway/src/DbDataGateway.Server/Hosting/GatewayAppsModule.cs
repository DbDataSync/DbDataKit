using System.Net;
using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;
using DbDataKit.State;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Secrets;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.State;
using DbDataGateway.Server.Tls;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// The <c>apps</c> module of the serving layer (plan 26, AP05): the published apps of the group's state, each instance served by subpath (<c>/apps/&lt;instance&gt;/</c>) on a listener of its own, with the Gateway's certificate.
/// Like the identity home it is a web application of its own inside the process, so its pipeline never touches the Flight or management listeners. It serves an instance's bundle with the egress policy of its manifest
/// (<see cref="AppSite"/>); an app's data calls (<c>/apps/&lt;instance&gt;/_data/...</c>) are answered by the group's published entity models in this process (<see cref="GroupAppData"/>, plan 20 O08c), and its semantic calls (<c>_data/api/v1/semantic/...</c>) by the semantic module as the signed-in person (<see cref="GroupAppSemantic"/>, plan 22a GP35c), so the store module's listener need not be on. A person is signed in through the identity home (<see cref="OpenIdClient"/>) and needs a role on <c>app:&lt;instance&gt;</c> to see the shell page; the token broker is the next slice (AP05c).
/// </summary>
public sealed class GatewayAppsModule(IOptionsMonitor<GatewayOptions> options, StateDatabase state, TlsCertificateHolder certificate, SecretsRuntime secrets, PublishedEntityModels models, DbDataKit.Serving.IServingServices serving, DbDataKit.Serving.Semantic.SemanticModels semanticModels, HomeCallers homeCallers, GroupSessionKey sessionKey, ILoggerFactory loggers, GroupCertificate? group = null) : IHostedService, IAsyncDisposable
{
    /// <summary>The secret that holds the seed of the key the apps host signs its app tokens with: made on the first start, and copied to every other instance of a group.</summary>
    public const string SigningKeySecret = "dbdatakit:gateway:apps:signing-key";

    /// <summary>The folder next to the program that holds the runtime for apps of views only (<c>views-app.js</c>, <c>views-app.css</c>; plan 26, AP25), copied there by the build from <c>Shared/web/view-app/dist</c>.</summary>
    public const string ViewsRuntimeFolder = "apps-views";

    /// <summary>The issuer name in the tokens; a server that accepts them pairs this issuer's public key.</summary>
    public const string TokenIssuer = "dbdatagateway-apps";

    private WebApplication? _app;

    /// <summary>The certificate chooser the listener asks at each handshake; set before the listener starts.</summary>
    private AppCertificates? Certificates { get; set; }

    public static async Task<ConsoleSigningKey> SigningKeyAsync(GatewayOptions current, CancellationToken cancellationToken)
    {
        var store = SecretsRuntime.OpenStore(current);
        using var held = await store.TryResolveAsync(SecretRef.Parse("secret:" + SigningKeySecret), cancellationToken);
        if (held is not null)
        {
            try { return ConsoleSigningKey.FromSeed(Convert.FromBase64String(held.RevealString())); }
            catch (Exception ex) when (ex is FormatException or ArgumentException)
            { throw new SecretConfigurationException(SigningKeySecret, $"The secret {SigningKeySecret} is not a key seed: {ex.Message} Remove it to have one made."); }
        }
        var key = ConsoleSigningKey.Generate();
        using var value = SecretValue.From(Convert.ToBase64String(key.ExportSeed()));
        await store.SetAsync(SecretName.Parse(SigningKeySecret), value, cancellationToken);
        return key;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        // the Flight listener loads the certificate when Kestrel starts, which is after the hosted services: this module may be first
        if (certificate.Current is null)
            certificate.Set(CertificateProvider.Load(current, GatewayHost.ResolveDataDirectory(current), loggers.CreateLogger("Tls"), secrets: secrets, group: group));

        var instances = new AppInstanceStore(new DefinitionStore(state));
        var bindings = new AppBindings(instances);
        Certificates = new AppCertificates(bindings, () => certificate.Current, reference => secrets.ResolveSecret("Gateway:Apps:Addresses", reference), message => loggers.CreateLogger("Apps").LogWarning("{Problem}", message));
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Parse(current.Apps.Address), current.Apps.Port, listen =>
            {
                listen.Protocols = HttpProtocols.Http1AndHttp2;
                listen.UseHttps(https =>
                {
                    // by the name the client asks for (SNI): a custom domain's own certificate, the host's for everything else (plan 26 AP08)
                    https.ServerCertificateSelector = (_, name) => Certificates?.Select(name) ?? certificate.Current;
                    https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                });
            });
        });

        var approvals = new AppApprovalStore(new DefinitionStore(state));
        var home = HomeOf(current);
        var local = string.IsNullOrWhiteSpace(current.Apps.Home.Authority);
        // a home this instance serves itself has the certificate this instance has; any other home is checked as usual
        var handler = new HttpClientHandler();
        if (local) handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert is not null && certificate.Current is { } own && cert.Thumbprint == own.Thumbprint;
        var secret = string.IsNullOrWhiteSpace(current.Apps.Home.ClientSecret) ? null : secrets.ResolveSecret(SecretsRuntime.AppsHomeSecretKey, current.Apps.Home.ClientSecret);
        var client = new OpenIdClient(
            new OpenIdClientOptions(home.AbsoluteUri, current.Apps.Home.ClientId!, secret, new Uri(PublicUrlOf(current), AppsEndpoints.CallbackPath).AbsoluteUri),
            new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) });
        var key = await SigningKeyAsync(current, cancellationToken);
        // an instance the home has no client for yet (saved with the offline command, or at another instance of the group) gets one now
        try
        {
            foreach (var made in AppInstanceClients.Reconcile(state, instances, AppInstanceClients.CallbackOf(PublicUrlOf(current)), current.Apps.Home.ClientId))
                loggers.CreateLogger("Apps").LogInformation("Registered an OpenID client for the app instance {Instance} at the identity home.", made);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            loggers.CreateLogger("Apps").LogWarning(ex, "The app instances' OpenID clients could not be registered; the host's own tokens serve until they are.");
        }
        var app = builder.Build();
        app.MapGet("/health/live", () => Results.Text("Healthy"));
        // a name bound to an instance is routed to it by Host and path, before the routes see the request (plan 26 AP08)
        app.UseAppAddresses(bindings, instances, new TrustedProxies(current.Apps.TrustedProxies));
        app.UseRouting();
        app.MapApps(instances, new AppSite(instances, name => instances.Resolve(name) is { } r ? approvals.ApprovedEgress(r) : AppEgress.None, views: ViewsRuntime.FromDirectory(Path.Combine(AppContext.BaseDirectory, ViewsRuntimeFolder))), client, new AppSessions(refresh: client.RefreshAsync, refreshAfter: TimeSpan.FromSeconds(current.Apps.RolesRefreshSeconds), store: new AppSessionStore(state, sessionKey)),
            new AppTokenBroker(new ConsoleTokenIssuer(TokenIssuer, key)), new AppDataGateway(instances, new GroupAppData(GatewayStoreModule.ApiOf(options, state, secrets, models)), new ConsoleIdentity(TokenIssuer, key), approvals: approvals, semantic: new GroupAppSemantic(serving, semanticModels, homeCallers, options)), new AppStorage(state), approvals, () => options.CurrentValue.Apps.StorageCapMb * 1024L * 1024L);
        app.MapFallback(() => Results.Text("The apps host of this DbDataGateway. An app is at /apps/<instance>/.", "text/plain"));
        await app.StartAsync(cancellationToken);
        _app = app;
    }

    /// <summary>The address people reach the apps host at: the setting, or the first TLS host name (localhost when there is none) on the module's port.</summary>
    public static Uri PublicUrlOf(GatewayOptions options) =>
        !string.IsNullOrWhiteSpace(options.Apps.PublicUrl)
            ? new Uri(options.Apps.PublicUrl.TrimEnd('/') + "/")
            : new UriBuilder(Uri.UriSchemeHttps, options.Tls.Hostnames.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)) ?? "localhost", options.Apps.Port).Uri;

    private static Uri HomeOf(GatewayOptions options) =>
        new((string.IsNullOrWhiteSpace(options.Apps.Home.Authority) ? GatewayIdentityModule.PublicUrlOf(options).AbsoluteUri : options.Apps.Home.Authority).TrimEnd('/'));

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) await _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
