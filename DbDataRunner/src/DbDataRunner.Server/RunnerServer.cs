using DbDataKit.Runner;
using System.Net;
using System.Reflection;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Hosting;
using DbDataKit.Runner.Api;
using DbDataKit.Hosting.Certificates;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataRunner.Server;

/// <summary>
/// The DbDataRunner server (plan 28a R10): Kestrel over HTTPS with the shared trust code of <c>DbDataKit.Web</c> (pairing, key rotation, the version header, the token check), the management API of <see cref="RunnerApi"/>
/// a console is the client of, and the runner itself (<see cref="RunnerEngine"/>) as a hosted service. It has no web page of its own. The certificate is self-signed and kept in the server's directory, and a client pins its
/// fingerprint at pairing, so nothing else about it needs to be trusted.
/// </summary>
public static class RunnerServer
{
    public static string Version { get; } =
        (typeof(RunnerServer).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    public static X509Certificate2 Certificate(string directory) => DirectoryCertificate.Load(directory);

    /// <summary>The server's instance id, made the first time and kept in its state database.</summary>
    public static string InstanceId(StateDatabase state) => GroupIdentity.InstanceId(new GroupSettings(state), "runner");

    /// <summary>The server's instance id as the directory's state holds it (opens the state, so it is the SQLite file of the directory unless the environment names another).</summary>
    public static string InstanceId(string directory, StateSettings? settings = null, Func<string, string?>? environment = null)
    {
        return InstanceId(RunnerState.Open(directory, settings, environment));
    }

    public static WebApplication Build(RunnerSettings settings, X509Certificate2 certificate, Action<string> log, Func<string, string?>? environment = null) =>
        Build(settings, certificate, log, RunnerState.Open(settings.Directory, settings.State, environment, settings.Group));

    /// <summary>Builds the server over a state database it owns from now on (closed with the host).</summary>
    public static WebApplication Build(RunnerSettings settings, X509Certificate2 certificate, Action<string> log, StateDatabase state)
    {
        if (settings.Listen.Scheme != Uri.UriSchemeHttps) throw new ArgumentException("A DbDataRunner server listens on HTTPS only.", nameof(settings));
        var instanceId = InstanceId(state);
        var files = new PairingFiles(settings.ServerYaml, Group: state);
        var clients = files.Clients();
        var fingerprint = PairingProof.CertificateFingerprint(certificate);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new LineLoggerProvider(log));
        // service and systemd modes, draining on stop, /health and /version, request limits: the host every DbData server shares
        builder.UseDbDataHost(host =>
        {
            host.Product = RunnerSettings.Product;
            host.ServiceName = RunnerService.RegistrationName;
            host.Version = Version;
        });
        // stopping waits for the running tasks to take their cancellation, and then for the processes to go
        builder.Services.Configure<HostOptions>(h => h.ShutdownTimeout = TimeSpan.FromTicks(Math.Max(h.ShutdownTimeout.Ticks, (settings.CancellationGrace + TimeSpan.FromSeconds(30)).Ticks)));
        var address = settings.Listen.HostNameType == UriHostNameType.Dns && settings.Listen.Host != "localhost"
            ? IPAddress.Any
            : settings.Listen.Host == "localhost" ? IPAddress.Loopback : IPAddress.Parse(settings.Listen.Host);
        builder.WebHost.ConfigureKestrel(o => o.Listen(address, settings.Listen.Port, listen => listen.UseHttps(certificate)));

        builder.Services.AddSingleton(state);
        builder.Services.AddSingleton(sp => new RunnerEngine(settings, state, sp.GetRequiredService<ILoggerFactory>().CreateLogger("DbDataRunner.Engine")));
        builder.Services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RunnerEngine>());

        var app = builder.Build();
        app.UseManagementApiVersion();
        app.UseDbDataPipeline();
        app.MapDbDataEndpoints();
        app.MapPairing(new PairingService(instanceId, fingerprint, clients, files.Codes()));
        app.AnnounceToConsole(RunnerSettings.Product, instanceId, settings.Listen.Port, clients, files.Codes());
        app.MapKeyRotation(new KeyRotationService(instanceId, clients, new MemoryReplayCache()));
        var tokens = new ConsoleTokenValidator(instanceId, clients, new MemoryReplayCache());
        RunnerWebhooks.Map(app, state, () => app.Services.GetRequiredService<RunnerEngine>(), tokens, settings.Secrets ?? RunnerState.Secrets(null));   // before the token check: a signed webhook has no token
        app.UseManagementAuthentication(tokens, refused => log($"A call was refused ({refused.Failure}): {refused.Detail}"));
        RunnerApi.Map(app, state, () => app.Services.GetRequiredService<RunnerEngine>(), instanceId, new RunnerApiOptions("/api/v1", RunnerCallers.TokenResolver, RunnerSettings.Product, Version));
        RunnerPublishApi.Map(app, new RunnerPublishing(state, settings.MaxArtifactBytes, settings.SignatureVerifier, settings.InstanceLease, new DbDataKit.Runner.Staging.ActivationOptions { MaxWait = settings.ActivationMaxWait }));
        RunnerGroupApi.Map(app, state, () => app.Services.GetRequiredService<RunnerEngine>(), "/api/v1", RunnerCallers.TokenResolver);
        return app;
    }
}

/// <summary>Sends the host's own log lines (warnings and above, and the runner's) to a function, so a command line and a test see what the runner says.</summary>
internal sealed class LineLoggerProvider(Action<string> write) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new LineLogger(categoryName, write);

    public void Dispose()
    {
    }

    private sealed class LineLogger(string category, Action<string> write) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information && category.StartsWith("DbDataRunner", StringComparison.Ordinal) || logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            write($"{logLevel}: {formatter(state, exception)}" + (exception is null ? "" : $" ({exception.GetType().Name}: {exception.Message})"));
        }
    }
}
