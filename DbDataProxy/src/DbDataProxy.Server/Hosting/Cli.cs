using DbDataKit.Cli;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// The command line: <c>dbdataproxy &lt;noun&gt; &lt;verb&gt;</c>, the grammar every DbData product shares (plan 13, W4). There is no default command: <c>server run</c> runs the proxy,
/// which is what the Windows service, the systemd unit and the container run.
/// </summary>
public static class Cli
{
    public const string Program = "dbdataproxy";

    public static Task<int> RunAsync(string[] args) => RunAsync(args, CliContext.Console());

    public static Task<int> RunAsync(string[] args, CliContext context) => Router().RunAsync(args, context);

    public static NounRouter Router() => new(Program,
    [
        ServerNoun.Create(RunServerAsync),
        ServiceNoun(),
        CertNoun(),
        SecretNoun(),
        ConfigNoun(),
        QueryNoun(),
        HealthNoun(),
    ]);

    private static async Task<int> RunServerAsync(string[] args, CliContext context)
    {
        try
        {
            var app = ProxyHost.Build(args);
            await app.RunAsync(context.Cancellation);
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is ProxyConfigException or Secrets.PlainSecretNotAllowedException)
        {
            // Invalid configuration: one precise message (key paths and sources, no secret values) and the documented exit code.
            await context.Error.WriteLineAsync(ex.Message);
            return ProxyConfigException.ExitCode;
        }
    }

    // ---- service ----

    private static Noun ServiceNoun() => new("service", "Registration of 'server run' with Windows or systemd",
    [
        Service("install", "Register the Windows service (run elevated); it runs as NT SERVICE\\<name> unless --account says otherwise", EffectClass.TargetWrites,
            "[--name N] [--account A] [--password-secret env:NAME|file:PATH] [--data-dir PATH] [--dry-run]"),
        Service("uninstall", "Stop and remove the service", EffectClass.TargetWrites, "[--name N]"),
        Service("status", "Whether the service is installed and running", EffectClass.OfflineOnly, "[--name N]"),
        Service("start", "Start the service", EffectClass.TargetWrites, "[--name N]"),
        Service("stop", "Stop the service", EffectClass.TargetWrites, "[--name N]"),
        Service("restart", "Stop and start the service", EffectClass.TargetWrites, "[--name N]"),
        Service("doctor", "Check the registration, the account and the directory permissions", EffectClass.OfflineOnly, "[--name N]"),
        Service("unit", "Print a hardened systemd unit that runs 'server run'", EffectClass.OfflineOnly, "[--name N] [--exec /path/to/dbdataproxy]"),
    ]);

    private static Verb Service(string name, string summary, EffectClass effect, string usage) =>
        new(name, summary, effect, usage, async (args, _) => await ServiceCli.RunAsync([name, .. args], ServiceCliEnvironment.Real()));

    // ---- cert ----

    private static Noun CertNoun() => new("cert", "The server's TLS certificate",
    [
        Cert("show", "The certificate in use; never creates or changes anything", EffectClass.OfflineOnly),
        Cert("ensure", "Create the self-signed certificate if needed", EffectClass.RepoFilesOnly),
        Cert("check", "Exit 1 when the certificate is missing or due for rotation", EffectClass.OfflineOnly),
        Cert("renew", "Replace the self-signed certificate now", EffectClass.RepoFilesOnly),
    ]);

    private static Verb Cert(string name, string summary, EffectClass effect) =>
        new(name, summary, effect, "", (args, _) =>
        {
            var options = ProxyConfiguration.LoadOptions();
            return Task.FromResult(CertCli.Run([name, .. args], options, ProxyHost.ResolveDataDirectory(options), new DbDataKit.Hosting.Cli.SystemConsole()));
        });

    // ---- secret ----

    private static Noun SecretNoun() => new("secret", "The encrypted secret store, secret references and protected values",
    [
        Secret("set", "Store one secret; the value comes from a prompt or standard input, never an argument", EffectClass.RepoFilesOnly, "<name> [--stdin]"),
        Secret("list", "Names and when each was written, never values", EffectClass.OfflineOnly, "[<prefix>]"),
        Secret("delete", "Remove one secret", EffectClass.RepoFilesOnly, "<name>"),
        Secret("check", "Whether each reference resolves (every one in the settings when none is named)", EffectClass.OfflineOnly, "[<reference> ...]"),
        Secret("rotate-key", "Re-encrypt every secret under a new key", EffectClass.RepoFilesOnly, ""),
        Secret("export", "Write every secret to a file under a passphrase", EffectClass.OfflineOnly, "<file> [--prefix <prefix>] [--passphrase-stdin]"),
        Secret("import", "Read secrets from an export file", EffectClass.RepoFilesOnly, "<file> [--overwrite] [--passphrase-stdin]"),
        Secret("protect", "An inline, encrypted dpapi: reference for one value (Windows, machine scope)", EffectClass.OfflineOnly, "[--stdin]"),
        new Verb("hash", "Print a password hash for a Proxy:Users entry; the password comes from a prompt or standard input", EffectClass.OfflineOnly, "", (args, context) => HashAsync(args, context)),
    ]);

    private static Verb Secret(string name, string summary, EffectClass effect, string usage) =>
        new(name, summary, effect, usage, async (args, _) =>
            await SecretCommand.RunAsync([name, .. args], ProxyConfiguration.LoadOptions(), new DbDataKit.Secrets.Cli.SystemConsole()));

    private static async Task<int> HashAsync(string[] args, CliContext context)
    {
        if (args.Length > 0)
        {
            await context.Error.WriteLineAsync("Usage: secret hash  (the password is read from a prompt or standard input, never an argument)");
            return CliExit.Usage;
        }
        string? password = ReadPasswordFromConsole();
        if (string.IsNullOrEmpty(password))
        {
            await context.Error.WriteLineAsync("A password is required.");
            return CliExit.Usage;
        }
        await context.Out.WriteLineAsync(PasswordHasher.Hash(password));
        return CliExit.Ok;
    }

    private static string? ReadPasswordFromConsole()
    {
        if (Console.IsInputRedirected) return Console.In.ReadLine();
        Console.Error.Write("Password: ");
        var sb = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is { Key: not ConsoleKey.Enter } key)
        {
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else sb.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }

    // ---- config ----

    private static Noun ConfigNoun() => new("config", "The server's own configuration",
    [
        new Verb("check", "Validate the configuration (exit 0 valid, 2 invalid)", EffectClass.OfflineOnly, "[--file F]",
            (args, context) => Task.FromResult(ConfigCommand.Run(["check", .. args], context.Out, context.Error))),
        new Verb("show", "The effective configuration with secrets masked; --origin adds where each value came from", EffectClass.OfflineOnly, "[--origin] [--file F]",
            (args, context) => Task.FromResult(ConfigCommand.Run(["show", .. args], context.Out, context.Error))),
    ]);

    // ---- query ----

    private static Noun QueryNoun() => new("query", "A client for smoke-testing a running proxy",
    [
        new Verb("run", "Run one query against a proxy and print the result", EffectClass.OfflineOnly,
            "--user U --password P --sql \"...\" [--address URL] [--datasource N] [--compression none|gzip|zstd|lz4] [--max-rows N] [--insecure]",
            (args, _) => QueryCommand.RunAsync(args)),
    ]);

    // ---- health ----

    private static Noun HealthNoun() => new("health", "Is a running proxy answering",
    [
        new Verb("check", "Exit 0 when the running proxy answers /health/live (for a container's HEALTHCHECK)", EffectClass.OfflineOnly, "[--url <url>]",
            async (args, context) =>
            {
                var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "url" });
                if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
                {
                    await context.Error.WriteLineAsync("Usage: health check [--url <url>]");
                    return CliExit.Usage;
                }
                var options = HealthCheck.Options(ProxyConfiguration.LoadOptions());
                var text = parsed.Get("url") ?? options.DefaultUrl;
                if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                {
                    await context.Error.WriteLineAsync("The address must be an http or https URL.");
                    return CliExit.Usage;
                }
                return await DbDataKit.Hosting.HealthProbeClient.RunAsync(url, TimeSpan.FromSeconds(4), null, options);
            }),
    ]);
}
