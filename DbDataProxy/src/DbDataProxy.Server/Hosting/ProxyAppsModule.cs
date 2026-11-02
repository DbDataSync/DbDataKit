using System.Net;
using DbDataKit.Apps;
using DbDataKit.Identity;
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
public sealed class ProxyAppsModule(IOptionsMonitor<ProxyOptions> options, StateDatabase state, TlsCertificateHolder certificate, SecretsRuntime secrets, ILoggerFactory loggers) : IHostedService, IAsyncDisposable
{
    private WebApplication? _app;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        // the Flight listener loads the certificate when Kestrel starts, which is after the hosted services: this module may be first
        if (certificate.Current is null)
            certificate.Set(CertificateProvider.Load(current, ProxyHost.ResolveDataDirectory(current), loggers.CreateLogger("Tls"), secrets: secrets));

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
        var app = builder.Build();
        app.MapGet("/health/live", () => Results.Text("Healthy"));
        app.MapApps(instances, new AppSite(instances), client, new AppSessions());
        app.MapFallback(() => Results.Text("The apps host of this DbDataProxy. An app is at /apps/<instance>/.", "text/plain"));
        await app.StartAsync(cancellationToken);
        _app = app;
    }

    /// <summary>The address people reach the apps host at: the setting, or the first TLS host name (localhost when there is none) on the module's port.</summary>
    public static Uri PublicUrlOf(ProxyOptions options) =>
        !string.IsNullOrWhiteSpace(options.Apps.PublicUrl)
            ? new Uri(options.Apps.PublicUrl.TrimEnd('/') + "/")
            : new UriBuilder(Uri.UriSchemeHttps, options.Tls.Hostnames.FirstOrDefault(h => !string.IsNullOrWhiteSpace(h)) ?? "localhost", options.Apps.Port).Uri;

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
