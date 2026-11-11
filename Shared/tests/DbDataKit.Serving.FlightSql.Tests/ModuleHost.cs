using System.Net;
using DbDataKit.Serving.Tests;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataKit.Serving.FlightSql.Tests;

/// <summary>The Flight SQL module hosted in-process over the fake host's services (plain HTTP/2 on a loopback port), the way a product hosts it: the module as a singleton, its interceptor in the gRPC pipeline and the server as the Flight service.</summary>
public sealed class ModuleHost : IAsyncDisposable
{
    private readonly WebApplication _app;

    public FlightSqlModule Module { get; }
    public Uri Address { get; }
    public List<string> Logs { get; } = [];

    private ModuleHost(WebApplication app, FlightSqlModule module, Uri address) => (_app, Module, Address) = (app, module, address);

    public static async Task<ModuleHost> StartAsync(IServingServices? services = null, FlightSqlOptions? options = null, Action<FlightSqlModule>? configure = null)
    {
        var module = new FlightSqlModule(options ?? new FlightSqlOptions { ServerName = "TestServer" });
        await module.StartAsync(services ?? new HostServices(new FakeServingHost()));
        configure?.Invoke(module);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, o => o.Protocols = HttpProtocols.Http2));
        builder.Services.AddSingleton(module);
        builder.Services.AddGrpc(o => o.Interceptors.Add<FlightAuthInterceptor>()).AddFlightServer<ServingFlightServer>();
        var app = builder.Build();
        app.MapFlightEndpoint();
        await app.StartAsync();
        return new ModuleHost(app, module, new Uri(app.Urls.First()));
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        await Module.DisposeAsync();
    }
}
