using System.Net;
using System.Security.Cryptography.X509Certificates;
using DbDataGateway.Server.Configuration;
using DbDataGateway.Server.Secrets;
using DbDataGateway.Server.Tls;
using DbDataKit.Serving;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// One module on the public listener: the web application the module built (reached through its <see cref="InProcessServer"/>, not through a port) and which requests are its.
/// A module with a <see cref="Prefix"/> (<c>/store</c>, set by the administrator) answers the paths under it as if they began at the root, with the prefix in <c>PathBase</c>, or at the path <see cref="Inner"/> makes of them (the identity home);
/// a module with <see cref="Claims"/> (the apps host) says itself which requests are its and takes the path as it is.
/// </summary>
public sealed class PublicMount
{
    public required string Name { get; init; }

    public required InProcessServer Server { get; init; }

    /// <summary>The path prefix the module is under on this listener; the prefix moves into <c>PathBase</c> for the module.</summary>
    public string? Prefix { get; init; }

    /// <summary>The path the module has for what follows the prefix (<c>/sign-in</c>, or empty for the prefix itself); null: that path as it is, <c>/</c> for none.</summary>
    public Func<string, string>? Inner { get; init; }

    /// <summary>Which requests are the module's, for a module that keeps the paths it has.</summary>
    public Func<HttpContext, bool>? Claims { get; init; }

    /// <summary>The certificate for a handshake by the name the client asks for, for a module that has certificates of its own (the apps host's custom domains); null: the Gateway's.</summary>
    public Func<string?, X509Certificate2?>? Certificate { get; init; }

    internal bool Matches(HttpContext context) =>
        Prefix is { } prefix && context.Request.Path.StartsWithSegments(prefix) || Claims?.Invoke(context) == true;
}

/// <summary>
/// The path each module answers under on the public listener, as the administrator set it (<c>identity.path</c>, <c>apps.path</c>, <c>store.path</c>, <c>views.path</c>, <c>rest.path</c> for feeds, <c>mcp.path</c>). A module with a <c>listen</c> of its own
/// has no prefix: it answers at the root of that listener.
/// </summary>
public static class PublicPaths
{
    /// <summary>The prefix of <paramref name="module"/> on the public listener, <c>/store</c> and the like; empty for a module with a <c>listen</c> of its own.</summary>
    public static string Of(GatewayOptions o, string module) => module switch
    {
        GatewayModules.Identity => o.Identity.Listen.Count == 0 ? Clean(o.Identity.Path) : "",
        GatewayModules.Apps => o.Apps.Listen.Count == 0 ? Clean(o.Apps.Path) : "",
        GatewayModules.Store => o.Store.Listen.Count == 0 ? Clean(o.Store.Path) : "",
        GatewayModules.Views => o.Views.Listen.Count == 0 ? Clean(o.Views.Path) : "",
        GatewayModules.Mcp => o.Mcp.Listen.Count == 0 ? Clean(o.Mcp.Path) : "",
        GatewayModules.Feeds => o.Rest.Listen.Count == 0 ? Clean(o.Rest.Path) : "",
        _ => "",
    };

    /// <summary>The apps host's prefix as the browser uses it: the setting when the host is on the public listener, <c>/apps</c> when it has an address of its own.</summary>
    public static string Apps(GatewayOptions o) => Of(o, GatewayModules.Apps) is { Length: > 0 } p ? p : DbDataKit.Apps.AppsPaths.Default;

    private static string Clean(string path) => "/" + path.Trim().Trim('/');

    /// <summary>Whether <paramref name="path"/> is a prefix: <c>/</c> and one or more segments of letters, digits and <c>-._~</c>, no trailing slash.</summary>
    public static bool IsPrefix(string? path) => path is { Length: > 1 } && path[0] == '/' && path[^1] != '/' && !path.Contains("//", StringComparison.Ordinal)
        && path.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '.' or '_' or '~');
}

/// <summary>The modules mounted on the public listener. A module adds itself when it starts and removes itself when it stops; a request for a path nobody is mounted at is a 404.</summary>
public sealed class PublicMounts
{
    private readonly object _gate = new();
    private PublicMount[] _mounts = [];

    public IReadOnlyList<PublicMount> Snapshot => Volatile.Read(ref _mounts);

    public void Add(PublicMount mount)
    {
        lock (_gate) _mounts = [.. _mounts.Where(m => m.Name != mount.Name), mount];
    }

    public void Remove(string name)
    {
        lock (_gate) _mounts = [.. _mounts.Where(m => m.Name != name)];
    }

    /// <summary>The address the public listener answers at on this machine (<c>https://localhost:7820</c>), without a path.</summary>
    public static string AddressOf(GatewayOptions options) => $"https://localhost:{GatewayListeners.PublicPort(options)}";
}

/// <summary>
/// A server that opens no port: the web application built on it hands the pipeline to <see cref="ForwardAsync"/>, and the public listener passes each request of the module's paths to it. The module keeps its own pipeline, services,
/// sign-in and fallback policy, none of which touch another module's.
/// </summary>
public sealed class InProcessServer : IServer
{
    private volatile Func<IFeatureCollection, Task>? _handler;

    public IFeatureCollection Features { get; } = new FeatureCollection();

    public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull
    {
        _handler = async features =>
        {
            var context = application.CreateContext(features);
            Exception? failure = null;
            try { await application.ProcessRequestAsync(context); }
            catch (Exception ex) { failure = ex; throw; }
            finally { application.DisposeContext(context, failure); }
        };
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _handler = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _handler = null;

    /// <summary>The features the module's request is made of: the connection's and the response as they are (the same response goes back), and the request with its path as the module sees it. What a request has already cached for the listener's own pipeline
    /// (services, items, query, form, cookies, the endpoint) is left out, so the module builds its own.</summary>
    private static readonly HashSet<Type> NotCarried =
    [
        typeof(IServiceProvidersFeature), typeof(IItemsFeature), typeof(IQueryFeature), typeof(IFormFeature), typeof(IRequestCookiesFeature), typeof(IResponseCookiesFeature),
        typeof(IEndpointFeature), typeof(IRouteValuesFeature), typeof(IHttpRequestFeature), typeof(IRequestBodyPipeFeature), typeof(IHttpActivityFeature),
    ];

    public Task ForwardAsync(HttpContext context, string? stripPrefix, Func<string, string>? inner = null)
    {
        var handler = _handler ?? throw new InvalidOperationException("The module is not running.");
        var features = new FeatureCollection();
        foreach (var (type, value) in context.Features)
            if (!NotCarried.Contains(type)) features[type] = value;
        var request = context.Features.GetRequiredFeature<IHttpRequestFeature>();
        var pathBase = new PathString(request.PathBase);
        var path = new PathString(request.Path);
        if (stripPrefix is not null && path.StartsWithSegments(stripPrefix, out var rest))
        {
            pathBase = pathBase.Add(new PathString(path.Value![..stripPrefix.Length]));
            path = inner is null ? (rest.HasValue ? rest : new PathString("/")) : new PathString(inner(rest.Value ?? ""));
        }
        features.Set<IHttpRequestFeature>(new ForwardedRequest(request, pathBase.Value ?? "", path.Value ?? ""));
        return handler(features);
    }

    /// <summary>The listener's request, with another <c>PathBase</c> and <c>Path</c>.</summary>
    private sealed class ForwardedRequest(IHttpRequestFeature original, string pathBase, string path) : IHttpRequestFeature
    {
        public string Protocol { get; set; } = original.Protocol;
        public string Scheme { get; set; } = original.Scheme;
        public string Method { get; set; } = original.Method;
        public string PathBase { get; set; } = pathBase;
        public string Path { get; set; } = path;
        public string QueryString { get; set; } = original.QueryString;
        public string RawTarget { get; set; } = original.RawTarget;
        public IHeaderDictionary Headers { get; set; } = original.Headers;
        public Stream Body { get; set; } = original.Body;
    }
}

/// <summary>
/// The Gateway's public HTTPS listener (<c>public:</c>): one port for the modules that have none of their own, routed by path. Each module mounts itself (<see cref="PublicMounts"/>) when it starts; a request for a path that
/// no running module is mounted at, a module that is off included, is a 404. The certificate is the Gateway's, looked up at each handshake, or the one a mounted module chooses by name (the apps host's custom domains).
/// </summary>
public sealed class PublicListener(IOptionsMonitor<GatewayOptions> options, PublicMounts mounts, TlsCertificateHolder certificate, SecretsRuntime secrets, ILoggerFactory loggers, GroupCertificate? group = null) : IHostedService, IAsyncDisposable
{
    private WebApplication? _app;

    /// <summary>The modules that answer on the public listener rather than on a port of their own: the ones that are on and have no <c>port</c> set.</summary>
    public static IReadOnlyList<string> MountedModules(GatewayOptions o) => [.. new (string Name, int Own)[]
    {
        (GatewayModules.Identity, o.Identity.Listen.Count), (GatewayModules.Apps, o.Apps.Listen.Count), (GatewayModules.Store, o.Store.Listen.Count), (GatewayModules.Views, o.Views.Listen.Count),
        (GatewayModules.Feeds, o.Rest.Listen.Count), (GatewayModules.Mcp, o.Mcp.Listen.Count),
    }.Where(m => GatewayModules.Has(o, m.Name) && m.Own == 0).Select(m => m.Name)];

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var current = options.CurrentValue;
        // no address, no listener: a Gateway built without the start sequence's choice of ports (a test) has none unless it is given one
        if (current.Public.Listen.Count == 0) return;
        // the Flight listener loads the certificate when Kestrel starts, which is after the hosted services: this listener may be first
        if (certificate.Current is null)
            certificate.Set(CertificateProvider.Load(current, GatewayHost.ResolveDataDirectory(current), loggers.CreateLogger("Tls"), secrets: secrets, group: group));

        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = ["--hostBuilder:reloadConfigOnChange=false"], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            foreach (var url in GatewayListeners.Parsed(current.Public.Listen))
                kestrel.Listen(url.Address, url.Port, listen =>
                {
                    listen.Protocols = HttpProtocols.Http1AndHttp2;
                    listen.UseHttps(https =>
                    {
                        https.ServerCertificateSelector = (_, name) => mounts.Snapshot.Select(m => m.Certificate?.Invoke(name)).FirstOrDefault(c => c is not null) ?? certificate.Current;
                        https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                    });
                });
        });
        var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Path == "/health/live")
            {
                await Results.Text("Healthy").ExecuteAsync(context);
                return;
            }
            foreach (var mount in mounts.Snapshot)
                if (mount.Matches(context))
                {
                    await mount.Server.ForwardAsync(context, mount.Prefix is { } p && context.Request.Path.StartsWithSegments(p) ? p : null, mount.Inner);
                    return;
                }
            await Results.Json(new { error = "There is nothing at this path: a module that is not on, or a path no module serves.", code = "not_found" }, statusCode: StatusCodes.Status404NotFound).ExecuteAsync(context);
        });
        await app.StartAsync(cancellationToken);
        _app = app;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
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

/// <summary>What a module's web application listens on: the addresses of its own when its <c>listen</c> is set, otherwise nothing, and the public listener forwards to it.</summary>
internal static class ModuleListener
{
    /// <summary>Configures <paramref name="builder"/> to listen on <paramref name="listen"/> over HTTPS, or, with no port, to be forwarded to; returns the server to mount in that case, null when the module has addresses of its own.</summary>
    public static InProcessServer? Configure(WebApplicationBuilder builder, IReadOnlyList<string> listen, Func<Microsoft.AspNetCore.Connections.ConnectionContext?, string?, X509Certificate2?> certificate)
    {
        var urls = GatewayListeners.Parsed(listen).ToList();
        if (urls.Count == 0)
        {
            var server = new InProcessServer();
            builder.Services.AddSingleton<IServer>(server);
            return server;
        }
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            foreach (var url in urls)
                kestrel.Listen(url.Address, url.Port, endpoint =>
                {
                    endpoint.Protocols = HttpProtocols.Http1AndHttp2;
                    endpoint.UseHttps(https =>
                    {
                        https.ServerCertificateSelector = certificate;
                        https.SslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13;
                    });
                });
        });
        return null;
    }

    /// <summary>Mounts a module that was given no port; refuses to start when there is nowhere to mount it.</summary>
    public static void Mount(PublicMounts? mounts, InProcessServer? server, Func<InProcessServer, PublicMount> mount)
    {
        if (server is null) return;
        if (mounts is null) throw new InvalidOperationException("The module has no port of its own and no public listener to be mounted on.");
        mounts.Add(mount(server));
    }
}
