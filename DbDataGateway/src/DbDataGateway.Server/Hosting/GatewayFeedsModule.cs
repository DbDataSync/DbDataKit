using System.Net;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Secrets;
using DbDataGateway.Server.Serving;
using DbDataGateway.Server.Tls;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// The <c>feeds</c> module of the serving layer (plan 14a SL31, was J28; the views, store and apps modules' pattern): saved queries and semantic requests as JSON, CSV or Arrow IPC over HTTPS on the public listener under <c>/feeds</c>, or on a listener of its own when <c>rest.port</c> is set, with the Gateway's
/// certificate (looked up at each handshake, so a rotated one is served without a restart). A web application of its own inside the process: its pipeline never touches the Flight, management or other module listeners. The routes
/// and their rules are <see cref="GatewayFeeds"/>'s; the module is on when <c>modules:</c> lists <c>feeds</c>, and its address, port and row limits are the <c>rest:</c> section of <c>server.yaml</c>.
/// </summary>
public sealed class GatewayFeedsModule(IOptionsMonitor<GatewayOptions> options, GatewayFeeds feeds, TlsCertificateHolder certificate, SecretsRuntime secrets, ILoggerFactory loggers, GroupCertificate? group = null, PublicMounts? mounts = null) : IHostedService, IAsyncDisposable
{
    private WebApplication? _app;

    /// <summary>The address the module answers at once it started (<c>https://localhost:7820/feeds</c>), or null.</summary>
    public string? Address { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        // the Flight listener loads the certificate when Kestrel starts, which is after the hosted services: this module may be first
        if (certificate.Current is null)
            certificate.Set(CertificateProvider.Load(current, GatewayHost.ResolveDataDirectory(current), loggers.CreateLogger("Tls"), secrets: secrets, group: group));

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        var inProcess = ModuleListener.Configure(builder, current.Rest.Listen, (_, _) => certificate.Current);
        var app = builder.Build();
        feeds.Map(app);
        await app.StartAsync(cancellationToken);
        _app = app;
        if (inProcess is not null)
        {
            Address = PublicMounts.AddressOf(current) + PublicPaths.Of(current, GatewayModules.Feeds);
            ModuleListener.Mount(mounts, inProcess, server => new PublicMount { Name = GatewayModules.Feeds, Server = server, Prefix = PublicPaths.Of(current, GatewayModules.Feeds) });
            return;
        }
        var bound = app.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>().Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()?.Addresses.FirstOrDefault();
        Address = bound?.Replace("[::]", "localhost", StringComparison.Ordinal).Replace("0.0.0.0", "localhost", StringComparison.Ordinal).TrimEnd('/');
    }


    public async Task StopAsync(CancellationToken cancellationToken)
    {
        mounts?.Remove(GatewayModules.Feeds);
        if (_app is not null) await _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
