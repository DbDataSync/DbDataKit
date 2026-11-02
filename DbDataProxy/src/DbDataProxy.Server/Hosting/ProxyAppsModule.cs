using System.Net;
using DbDataKit.Apps;
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
/// (<see cref="AppSite"/>); what an app does with data goes through the store and identity modules with a token, not through this listener. Sign-in, the shell page and the token broker are the next slices (AP05b).
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

        var site = new AppSite(new AppInstanceStore(new DefinitionStore(state)));
        var app = builder.Build();
        app.MapGet("/health/live", () => Results.Text("Healthy"));
        app.MapGet("/apps/{instance}/{**path}", (HttpContext context, string instance, string? path) => Serve(site, context, instance, path));
        app.MapFallback(() => Results.Text("The apps host of this DbDataProxy. An app is at /apps/<instance>/.", "text/plain"));
        await app.StartAsync(cancellationToken);
        _app = app;
    }

    private static async Task Serve(AppSite site, HttpContext context, string instance, string? path)
    {
        // /apps/<instance> without the slash: relative links in the page would resolve against /apps/
        if (string.IsNullOrEmpty(path) && !context.Request.Path.Value!.EndsWith('/'))
        {
            context.Response.Redirect($"/apps/{Uri.EscapeDataString(instance)}/");
            return;
        }
        var response = site.Get(instance, path ?? "");
        foreach (var (name, value) in response.Headers) context.Response.Headers[name] = value;
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        await context.Response.Body.WriteAsync(response.Body);
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
