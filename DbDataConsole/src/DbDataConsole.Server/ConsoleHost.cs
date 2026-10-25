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
        builder.WebHost.ConfigureKestrel(o => o.Listen(AddressOf(settings.Listen), settings.Listen.Port, listen => listen.UseHttps(certificate)));

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
        builder.Services.AddSingleton<ConsoleRelay>();
        configure?.Invoke(builder.Services);

        var app = builder.Build();
        if (app.Services.GetRequiredService<PasskeyOptions>().Problem() is { } problem)
            log($"Passkeys are misconfigured and will not work: {problem}");

        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();

        // The web app is code, public on GitHub; the data is behind /api, which the fallback policy closes. Served before authentication so the sign-in screen can be.
        if (Directory.Exists(webRoot))
        {
            var files = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(webRoot);
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
            app.UseStaticFiles(new StaticFileOptions { FileProvider = files });
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapRelay();

        // Deep links are the web app's routes, not this server's: /users has to return the app. An unmatched /api route is a 404, never the app.
        app.MapFallback(async context =>
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
        }).AllowAnonymous();
        return app;
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
