using System.Net;
using DbDataKit.Apps;
using DbDataKit.Identity;
using DbDataKit.Secrets;
using DbDataKit.Web.Trust;
using DbDataKit.State;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;
using DbDataProxy.Server.Tls;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// The <c>apps</c> module of the serving layer (plan 26, AP05): the published apps of the group's state, each instance served by subpath (<c>/apps/&lt;instance&gt;/</c>) on a listener of its own, with the Proxy's certificate.
/// Like the identity home it is a web application of its own inside the process, so its pipeline never touches the Flight or management listeners. It serves an instance's bundle with the egress policy of its manifest
/// (<see cref="AppSite"/>); what an app does with data goes through the store and identity modules with a token, not through this listener. A person is signed in through the identity home (<see cref="OpenIdClient"/>) and needs a role on <c>app:&lt;instance&gt;</c> to see the shell page; the token broker is the next slice (AP05c).
/// </summary>
public sealed class ProxyAppsModule(IOptionsMonitor<ProxyOptions> options, StateDatabase state, TlsCertificateHolder certificate, SecretsRuntime secrets, ILoggerFactory loggers, GroupCertificate? group = null) : IHostedService, IAsyncDisposable
{
    /// <summary>The secret that holds the seed of the key the apps host signs its app tokens with: made on the first start, and copied to every other instance of a group.</summary>
    public const string SigningKeySecret = "dbdata:proxy:apps:signing-key";

    /// <summary>The issuer name in the tokens; a server that accepts them pairs this issuer's public key.</summary>
    public const string TokenIssuer = "dbdataproxy-apps";

    private WebApplication? _app;

    public static async Task<ConsoleSigningKey> SigningKeyAsync(ProxyOptions current, CancellationToken cancellationToken)
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
            certificate.Set(CertificateProvider.Load(current, ProxyHost.ResolveDataDirectory(current), loggers.CreateLogger("Tls"), secrets: secrets, group: group));

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
                    https.ServerCertificateSelector = (_, _) => certificate.Current;
                    https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                });
            });
        });

        var instances = new AppInstanceStore(new DefinitionStore(state));
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
        var app = builder.Build();
        app.MapGet("/health/live", () => Results.Text("Healthy"));
        app.MapApps(instances, new AppSite(instances), client, new AppSessions(refresh: client.RefreshAsync, refreshAfter: TimeSpan.FromSeconds(current.Apps.RolesRefreshSeconds)),
            new AppTokenBroker(new ConsoleTokenIssuer(TokenIssuer, key)), new AppDataGateway(instances, StoresOf(current), new ConsoleIdentity(TokenIssuer, key)), new AppStorage(state), new AppApprovalStore(new DefinitionStore(state)));
        app.MapFallback(() => Results.Text("The apps host of this DbDataProxy. An app is at /apps/<instance>/.", "text/plain"));
        await app.StartAsync(cancellationToken);
        _app = app;
    }

    /// <summary>The address people reach the apps host at: the setting, or the first TLS host name (localhost when there is none) on the module's port.</summary>
    public static Uri PublicUrlOf(ProxyOptions options) =>
        !string.IsNullOrWhiteSpace(options.Apps.PublicUrl)
            ? new Uri(options.Apps.PublicUrl.TrimEnd('/') + "/")
            : new UriBuilder(Uri.UriSchemeHttps, options.Tls.Hostnames.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)) ?? "localhost", options.Apps.Port).Uri;

    /// <summary>The Store servers this host is paired with (<c>dbdataproxy apps store-pair</c>).</summary>
    public static AppStores StoresOf(ProxyOptions options) => new(Path.Combine(ProxyHost.ResolveDataDirectory(options), "apps", "stores.json"));

    private static Uri HomeOf(ProxyOptions options) =>
        new((string.IsNullOrWhiteSpace(options.Apps.Home.Authority) ? ProxyIdentityModule.PublicUrlOf(options).AbsoluteUri : options.Apps.Home.Authority).TrimEnd('/'));

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_app is not null) await _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
