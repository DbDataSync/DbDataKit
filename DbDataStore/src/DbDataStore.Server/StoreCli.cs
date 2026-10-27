using System.Net.Http;
using DbDataKit.Cli;
using DbDataKit.Hosting.Cli;
using DbDataKit.Web.Trust;

namespace DbDataStore.Server;

/// <summary>
/// The command line, in the grammar every DbData product shares (plan 13, C02): <c>dbdatastore server run|pair|clients|unpair</c>, <c>service</c> and <c>health check</c>. <c>--repo</c> names the server's directory
/// (<see cref="StoreSettings.DirectoryVariable"/> by default), as it does for the other products' pairing verbs.
/// </summary>
public static class StoreCli
{
    public const string Program = "dbdatastore";

    /// <summary>Where <c>health check</c> asks by default: the server's own address, as a container sees it.</summary>
    public const string HealthUrl = "https://localhost:8490/health/live";

    public static Task<int> RunAsync(string[] args) => RunAsync(args, CliContext.Console());

    public static async Task<int> RunAsync(string[] args, CliContext context)
    {
        // Ctrl+C and the service manager's stop both end `server run` cleanly.
        using var stop = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) => { e.Cancel = true; stop.Cancel(); };
        Console.CancelKeyPress += onCancel;
        using var terminate = System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });
        try { return await Router().RunAsync(args, context with { Cancellation = context.Cancellation.CanBeCanceled ? context.Cancellation : stop.Token }); }
        finally { Console.CancelKeyPress -= onCancel; }
    }

    public static NounRouter Router(Func<string, string?>? environment = null) => new(Program,
    [
        ServerNoun.Create((args, context) => RunServerAsync(args, context, environment))
            .With([.. PairingVerbs.Create(args => new PairingFiles(Path.Combine(DirectoryOf(args, environment), "server.yaml")))]),
        StoreService.Noun(StoreServiceEnvironment.Real()),
        HealthNoun.Create(new HttpHealthProbe(LoopbackTolerantClient()), HealthUrl),
    ], StoreServer.Version);

    /// <summary>The server's certificate is self-signed, so a probe of its own address cannot validate it; it is accepted for a loopback address only, where the answer is this machine and the probe reads a status code and no body.</summary>
    private static HttpClient LoopbackTolerantClient() => new(new HttpClientHandler
    {
        ServerCertificateCustomValidationCallback = (request, _, _, errors) => errors == System.Net.Security.SslPolicyErrors.None || request.RequestUri is { IsLoopback: true },
    });

    private static string DirectoryOf(string[] args, Func<string, string?>? environment)
    {
        // the last one wins, so an option given to the service command after the registered default overrides it
        var i = Array.LastIndexOf(args, "--repo");
        return Path.GetFullPath(i >= 0 && i + 1 < args.Length ? args[i + 1] : StoreSettings.DefaultDirectory(environment));
    }

    private static async Task<int> RunServerAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "listen" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Unexpected: {string.Join(' ', parsed.Unknown.Concat(parsed.Words))}. Usage: server run [--repo <server directory>] [--listen https://<address>:<port>].");
            return CliExit.Usage;
        }
        if (!Uri.TryCreate(parsed.Get("listen") ?? StoreSettings.DefaultListen, UriKind.Absolute, out var listen) || listen.Scheme != Uri.UriSchemeHttps)
        {
            await context.Error.WriteLineAsync("--listen is an https address, such as https://127.0.0.1:8490.");
            return CliExit.Usage;
        }

        var settings = new StoreSettings(DirectoryOf(args, environment), listen);
        Directory.CreateDirectory(settings.Directory);
        var app = StoreServer.Build(settings, StoreServer.Certificate(settings.Directory), line => context.Error.WriteLine(line));
        await context.Out.WriteLineAsync($"Serving on {listen} (instance {StoreServer.InstanceId(settings.ServerYaml)}). Pair a client with: dbdatastore server pair --max-role <roles> [--repo <server directory>]");
        // the host's own lifetime: Ctrl+C, SIGTERM, systemd and the Windows service manager all stop it the same way
        await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.RunAsync(app, context.Cancellation);
        return CliExit.Ok;
    }
}
