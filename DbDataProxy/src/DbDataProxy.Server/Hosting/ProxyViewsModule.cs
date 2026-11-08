using System.Net;
using DbDataKit.Hosting;
using DbDataKit.Serving;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;
using DbDataProxy.Server.Serving;
using DbDataProxy.Server.Tls;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// The <c>views</c> module of the serving layer (plan 32, DBR11b; J28's pattern as the store and apps modules follow it): published dashboards and reports over REST on an HTTPS listener of its own, with the Proxy's certificate (looked up per handshake, so
/// a rotated one is served without a restart). The module is <see cref="DbDataKit.Views.Hosting.ViewsModule"/> with no listener and no follower of its own: its routes are mounted here, and the Proxy's one definition follower hands it each served version
/// (<see cref="ProxyViews.Publications"/>), so a publish through any instance is served by all. The callers are the identity home's people (a personal access token, or an access token the home issued) and every widget runs as them through the serving core.
/// The same routes answer a console on the management API (<c>ProxyViewsManagement</c>), as the person the console vouches for. An app's narrowed token reaches the module in process, through the apps host (<c>ViewsApi</c> with a <c>ViewsAppScope</c>): not built.
/// </summary>
public sealed class ProxyViewsModule(IOptionsMonitor<ProxyOptions> options, ProxyViews views, IServingServices services, TlsCertificateHolder certificate, SecretsRuntime secrets, ILoggerFactory loggers,
    GroupCertificate? group = null) : IHostedService, IAsyncDisposable
{
    private WebApplication? _app;

    /// <summary>The address people reach the module at once it started (<c>https://127.0.0.1:8820</c>), or null.</summary>
    public string? Address { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        if (certificate.Current is null)
            certificate.Set(CertificateProvider.Load(current, ProxyHost.ResolveDataDirectory(current), loggers.CreateLogger("Tls"), secrets: secrets, group: group));
        await views.Module.StartAsync(services, cancellationToken);

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Listen(IPAddress.Parse(current.Views.Address), current.Views.Port, listen =>
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
        views.Module.Map(app);
        await app.StartAsync(cancellationToken);
        _app = app;
        var bound = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        views.Address = Address = bound?.Replace("[::]", "localhost", StringComparison.Ordinal).Replace("0.0.0.0", "localhost", StringComparison.Ordinal).TrimEnd('/');
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // new requests are refused and the ones in flight finish, then the listener goes
        await views.Module.DrainAsync(cancellationToken);
        if (_app is not null) await _app.StopAsync(cancellationToken);
        await views.Module.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}

/// <summary>The module's health among the Proxy's readiness components: a version the instance cannot serve, or a drain, is degraded; a module that did not start is not ready.</summary>
public sealed class ViewsReadinessProbe(ProxyViews views) : IReadinessProbe
{
    public string Name => "views";

    public Task<ComponentHealth> CheckAsync(CancellationToken cancellationToken)
    {
        var health = views.Module.Health();
        return Task.FromResult(new ComponentHealth(Name, health.State switch
        {
            ServingHealthState.Ok => HealthStatus.Healthy,
            ServingHealthState.Degraded => HealthStatus.Degraded,
            _ => HealthStatus.Unhealthy,
        }, health.Detail));
    }
}
