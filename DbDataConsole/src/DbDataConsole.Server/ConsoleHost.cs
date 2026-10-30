using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json.Serialization;
using DbDataKit.Hosting;
using Microsoft.AspNetCore.DataProtection;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Secrets.Store;
using DbDataKit.State;
using DbDataKit.Web.Auth;
using DbDataKit.Web.Auth.Controllers;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DbDataConsole.Server;

/// <summary>
/// The dbdatakit console host (plan 13, U07): Kestrel over HTTPS serving the console's web app and the API behind it, with the shared sign-in (<see cref="SessionAuthRegistration"/>): passkeys, sessions, invitations and users.
/// The console is the install's identity home by default, so the users live in its own state database; the first administrator is made by redeeming the invitation it writes to <c>FIRST-RUN.txt</c> when it first starts.
/// </summary>
public static class ConsoleHost
{
    public static readonly AuthProduct Auth = new("DbDataKit", "dbdatakit user invite");

    public static string Version { get; } =
        (typeof(ConsoleHost).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static X509Certificate2 Certificate(string directory) => DirectoryCertificate.Load(directory);

    /// <param name="webRoot">The built web app; the folder <c>wwwroot</c> beside the program when null. Without it the host serves the API and a page that says the app is not installed.</param>
    public static WebApplication Build(ConsoleSettings settings, X509Certificate2 certificate, Action<string> log, string? webRoot = null, Action<IServiceCollection>? configure = null)
    {
        if (settings.Listen.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("The console listens on HTTPS only.", nameof(settings));
        webRoot ??= Path.Combine(AppContext.BaseDirectory, "wwwroot");

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new LineLoggerProvider(log));
        // service and systemd modes, draining on stop, /health and /version: the host every DbData server shares
        builder.UseDbDataHost(host =>
        {
            host.Product = ConsoleSettings.Product;
            host.ServiceName = ConsoleService.RegistrationName;
            host.Version = Version;
        });
        if (settings.AnnounceListen is { } announce && (announce.Scheme != Uri.UriSchemeHttp || announce.Port == settings.Listen.Port))
            throw new ArgumentException("The announce address is a plain http address on a port of its own.", nameof(settings));
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.Listen(AddressOf(settings.Listen), settings.Listen.Port, listen => listen.UseHttps(certificate));
            if (settings.AnnounceListen is { } announce) o.Listen(AddressOf(announce), announce.Port);
        });

        // A passkey belongs to a host name; unless the configuration says otherwise it is the one people open the console at.
        var relyingParty = $"{Auth.Section}:Passkeys:RelyingPartyId";
        if (string.IsNullOrWhiteSpace(builder.Configuration[relyingParty]))
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [relyingParty] = settings.Public.Host });

        // The framework's key ring is kept with the rest of what the console owns, so a container that is replaced does not lose it and the log does not warn about it.
        builder.Services.AddDataProtection().SetApplicationName("dbdatakit-console").PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(settings.Directory, "dataprotection")));

        builder.Services.AddControllers()
            // controller discovery starts from the entry assembly, which is not always this one (the tests): name the one that holds the sign-in controllers
            .AddApplicationPart(typeof(AuthController).Assembly)
            .AddApplicationPart(typeof(ConsoleHost).Assembly)
            .AddJsonOptions(options => options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddSingleton(_ => ConsoleState.Open(settings.Directory));
        builder.Services.AddSingleton(sp => new NotificationStore(sp.GetRequiredService<StateDatabase>()));
        builder.Services.AddSessionAuth(Auth,
            sp => sp.GetRequiredService<StateDatabase>(),
            sp => PasskeyOptions.FromConfiguration(sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(), Auth, settings.Public.GetLeftPart(UriPartial.Authority), [], []),
            _ => settings.Directory);
        // the servers this console manages, and the key it signs for them with
        builder.Services.AddSingleton(_ => ConsoleSecrets.Open(settings.Directory));
        builder.Services.AddSingleton(sp => new ConsoleKeyStore(sp.GetRequiredService<EncryptedFileSecretStore>(), sp.GetRequiredService<EncryptedFileSecretStore>()));
        builder.Services.AddSingleton(_ => new InstanceRegistry(settings.InstancesPath));
        builder.Services.AddSingleton<ConsoleInstances>();
        builder.Services.AddSingleton(_ => new PendingInstances(settings.PendingPath));
        builder.Services.AddSingleton<ConsoleRelay>();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        if (app.Services.GetRequiredService<PasskeyOptions>().Problem() is { } problem)
            log($"Passkeys are misconfigured and will not work: {problem}");

        // The private port answers announcements and nothing else; the announcement path answers on no other port
        var announcePort = settings.AnnounceListen?.Port;
        app.Use(async (context, next) =>
        {
            var onAnnouncePort = announcePort is { } port && context.Connection.LocalPort == port;
            var isAnnouncement = context.Request.Path.Equals(ConsoleAnnouncer.Path, StringComparison.Ordinal);
            if (!onAnnouncePort && !isAnnouncement) { await next(); return; }
            if (!onAnnouncePort || !isAnnouncement || !HttpMethods.IsPost(context.Request.Method)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            if (context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit) limit.MaxRequestBodySize = 4096;
            Announcement? announcement;
            try { announcement = await context.Request.ReadFromJsonAsync<Announcement>(new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web), context.RequestAborted); }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or Microsoft.AspNetCore.Http.BadHttpRequestException) { announcement = null; }
            var resolves = announcement?.Host is { } host && PendingInstances.HostPattern().IsMatch(host) && await ResolvesAsync(host);
            var refusal = announcement is null ? "the body is not an announcement" : context.RequestServices.GetRequiredService<PendingInstances>().Announce(announcement, context.Connection.RemoteIpAddress, resolves);
            if (refusal is null) { context.Response.StatusCode = StatusCodes.Status204NoContent; return; }
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(new { error = refusal }, context.RequestAborted);
        });

        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();

        // The web app is code, public on GitHub; the data is behind /api, which the fallback policy closes. Served before authentication so the sign-in screen can be.
        if (Directory.Exists(webRoot))
        {
            var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }

        app.UseWebSockets();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapRelay();

        // Deep links are the web app's routes, not this server's: /users has to return the app. An unmatched /api route is a 404, never the app.
        RequestDelegate webApp = async context =>
        {
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase)) { context.Response.StatusCode = StatusCodes.Status404NotFound; return; }
            var index = Path.Combine(webRoot, "index.html");
            if (File.Exists(index))
            {
                context.Response.ContentType = "text/html; charset=utf-8";
                await context.Response.SendFileAsync(index);
                return;
            }
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("The dbdatakit console is running, but its web app is not installed here (no wwwroot).");
        };
        app.MapFallback(webApp).AllowAnonymous();
        // The default fallback leaves out a path whose last segment has an extension, as a missing file's address does. A server's own names are routes too, and one with a dot in it (a Sync table mapping `dbo.Orders`)
        // is a deep link under /servers/<id>/, so those paths are matched on their own: nothing under /servers/ is a file, which the static files above would have served before the routes saw it.
        app.MapFallback("/servers/{*path}", webApp).AllowAnonymous();
        return app;
    }

    /// <summary>Whether the console can look the name up, which a name on the network the servers share with it can.</summary>
    private static async Task<bool> ResolvesAsync(string host)
    {
        try
        {
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            return (await Dns.GetHostAddressesAsync(host, limit.Token)).Length > 0;
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException) { return false; }
    }

    private static IPAddress AddressOf(Uri listen) =>
        listen.Host == "localhost" ? IPAddress.Loopback
        : listen.HostNameType == UriHostNameType.Dns ? IPAddress.Any
        : IPAddress.Parse(listen.Host);

    /// <summary>Sends the host's log lines to one callback (the command's error stream), with the level.</summary>
    private sealed class LineLoggerProvider(Action<string> write) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Line(write);
        public void Dispose() { }

        private sealed class Line(Action<string> write) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                write($"{logLevel.ToString().ToLowerInvariant()}: {formatter(state, exception)}");
        }
    }
}
