using DbDataKit.Cli;
using DbDataKit.Web.Trust;
using Microsoft.Extensions.DependencyInjection;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// The command line: <c>dbdatagateway &lt;noun&gt; &lt;verb&gt;</c>, the grammar every DbData product shares (plan 13, W4). There is no default command: <c>server run</c> runs the gateway,
/// which is what the Windows service, the systemd unit and the container run.
/// </summary>
public static class Cli
{
    public const string Program = "dbdatagateway";

    public static Task<int> RunAsync(string[] args) => RunAsync(args, CliContext.Console());

    public static async Task<int> RunAsync(string[] args, CliContext context)
    {
        // the rename from the old product name is a clean break (plan 20, O14): leftovers of the old name stop every command, once, with a message that names them
        var renamed = RenamedFrom.Problems(args);
        if (renamed.Count > 0)
        {
            foreach (var problem in renamed) await context.Error.WriteLineAsync(problem);
            return GatewayConfigException.ExitCode;
        }
        return await Router().RunAsync(args, context);
    }

    /// <summary>What <c>ui mcp</c> never offers: secrets (they read standard input, which is the protocol's channel), queries (they return rows; an agent works without data) and adopting (a person's act).</summary>
    private static readonly string[] NotForAgents = ["secret", "query", "server pair", "server unpair", "datasource adopt", "export install", "identity", "apps", "access explain", "access coverage"];   // access explain and coverage: who may see what is not for an agent to enumerate; identity: client secrets and invitations establish trust, as pairing does

    public static NounRouter Router(Func<string, string?>? environment = null)
    {
        NounRouter? router = null;
        router = Build(() => router!, environment);
        return router;
    }

    private static NounRouter Build(Func<NounRouter> self, Func<string, string?>? environment) => new(Program,
    [
        ServerNoun.Create(RunServerAsync).With([.. PairingVerbs.Create(PairingFilesOf).Select(WarnWhenManagementIsOff)]),
        ServiceNoun(),
        DbDataKit.Web.Trust.JoinPairCommand.Create(new DbDataKit.Web.Trust.PairCommandOptions(Program,
            args => args.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : GatewayConfigFile.ResolveDataDirectory(),
            async _ =>
            {
                var (registered, problem) = await DbDataKit.Hosting.Cli.ServiceRestarter.RestartAsync(GatewayHost.RegistrationName, DbDataKit.Hosting.Startup.SuiteServices.Gateway.Program);
                return new DbDataKit.Web.Trust.ServiceRestartResult(registered, problem);
            })),
        CertNoun(),
        SecretNoun(),
        IdentityCommand.Noun(),
        AccessCommand.Noun(),
        AppsCommand.Noun(),
        ViewsCommand.Noun(),
        ExportCommand.Noun(),
        ConfigNoun(),
        StateNoun(),
        QueryNoun(),
        DbDataKit.Semantic.Cli.SemanticNoun.Create(GatewaySemanticSession.ConnectAsync, GatewaySemanticSession.Options, GatewaySemanticSession.Flags, GatewaySemanticSession.Usage),
        DataSourceNoun(),
        LineageCommand.Noun(),
        HealthNoun(),
        UiNoun.Create(self, Program, GatewayHost.Version, NotForAgents),
    ], GatewayHost.Version, () => DbDataKit.State.SuiteEnvironment.OldNameProblem(environment));

    /// <summary>
    /// Where the pairing verbs look: the gateway's <c>server.yaml</c> in the configuration folder of its data directory, and the pairing codes in the data directory itself (not in the folder a person keeps under version control).
    /// <c>--repo</c> names the data directory, as it names the server's directory in the other products.
    /// </summary>
    private static PairingFiles PairingFilesOf(string[] args)
    {
        var at = Array.LastIndexOf(args, "--repo");
        var dataDirectory = at >= 0 && at + 1 < args.Length ? Path.GetFullPath(args[at + 1]) : GatewayConfigFile.ResolveDataDirectory();
        // a group (a SQL Server or PostgreSQL state) keeps its pairings in the state, so a code made here is redeemed through any of its instances (plan 20 O04)
        var options = GatewayConfiguration.LoadOptions(dataDirectory);
        options.DataDirectory = dataDirectory;
        var group = options.State.ToSettings().IsSqlite ? null : IdentityCommand.OpenState(options);
        return new PairingFiles(GatewayConfigFile.PathIn(dataDirectory), Path.Combine(dataDirectory, "pairing-codes.json"), group);
    }

    /// <summary>
    /// <c>server pair</c> makes a code a console redeems at the management address; with the management API off (<c>management.enabled: false</c>, or <c>Tls.Mode</c> None, which the validator refuses with it on) no console can
    /// reach this gateway, so the verb says so before it prints a code (plan 33 SC40). The code is still made: the setting may be changed before the code expires.
    /// </summary>
    private static Verb WarnWhenManagementIsOff(Verb verb)
    {
        if (verb.Name != "pair") return verb;
        return verb with
        {
            Run = (args, context) =>
            {
                try
                {
                    var at = Array.LastIndexOf(args, "--repo");
                    var dataDirectory = at >= 0 && at + 1 < args.Length ? Path.GetFullPath(args[at + 1]) : GatewayConfigFile.ResolveDataDirectory();
                    var options = GatewayConfiguration.LoadOptions(dataDirectory);
                    if (!options.Management.Enabled)
                        context.Error.WriteLine("Warning: the management API is off here (`management.enabled: false`), so no console can reach this gateway: a pairing made now cannot be used. "
                            + "Remove that setting or set `management.enabled: true` (https on the management port, 7822 by default) and restart the gateway, then pair.");
                    else if (options.Tls.Mode == TlsMode.None)
                        context.Error.WriteLine("Warning: `tls.mode` is None, and the management API is https only, so no console can reach this gateway: a pairing made now cannot be used. Choose another TLS mode (SelfSigned, File or Store).");
                }
                catch (Exception ex) when (ex is GatewayConfigException or IOException)
                {
                    // the pairing itself reports an unreadable configuration
                }
                return verb.Run(args, context);
            },
        };
    }

    private static async Task<int> RunServerAsync(string[] args, CliContext context)
    {
        // every step of the start is reported the same way in every server: the step that failed and why, in the startup-failure file (a service has
        // no console, and "Cannot start service" alone says nothing), the event log for a Windows service, and a non-zero exit
        // the switch that skips the folder step's check is not a configuration key on the command line: it becomes the key startup.skip_folder_permissions_check,
        // so the server's settings (and /version) say the check was skipped however it was asked for
        var skipFlag = args.Contains(DbDataKit.Hosting.Startup.FolderPermissions.Flag);
        string[] hostArgs = skipFlag
            ? [.. args.Where(a => a != DbDataKit.Hosting.Startup.FolderPermissions.Flag), $"--Gateway:Startup:{DbDataKit.Hosting.Startup.FolderPermissions.Key}=true"]
            : args;
        var dataDirectory = GatewayConfigFile.ResolveDataDirectory(hostArgs);
        var service = DbDataKit.Hosting.Startup.SuiteServices.Gateway;
        Microsoft.AspNetCore.Builder.WebApplication? app = null;
        ListenerChoice? choice = null;
        var result = await new DbDataKit.Hosting.Startup.StartupSequence(new DbDataKit.Hosting.Startup.StartupOptions
            {
                Service = service,
                DataFolder = dataDirectory,
                LogsRoot = DbDataKit.Hosting.Startup.SuiteServices.LogsRootFor(service, dataDirectory),
                Version = GatewayHost.Version,
                Warning = line => context.Error.WriteLine(line),
            })
            .Step(DbDataKit.Hosting.Startup.StartupStep.Folder, step =>
            {
                // the data folder is checked before anything is made in it; the setting is read from config/server.yaml as every server reads its own
                var settingsFile = GatewayConfigFile.PathIn(dataDirectory);
                DbDataKit.Hosting.Startup.SkipFolderPermissions skip;
                try { skip = DbDataKit.Hosting.Startup.SkipFolderPermissions.Read(skipFlag, settingsFile); }
                catch (InvalidOperationException ex) { throw new GatewayConfigException(ex.Message, []); }
                // a variable overrides the file, as for every setting of the Gateway, so the check reads it as the server's settings will
                if (!skip.IsOn && GatewayConfigFile.SkipFolderPermissionsVariable() is { } variable) skip = new(false, $"the variable {variable}");
                step.CheckFolderPermissions(dataDirectory, skip, settingsFile);
                // earlier releases wrote the reason of a failed start to this file in the data folder; it describes no start of this one
                try { File.Delete(Path.Combine(dataDirectory, "start-failure.txt")); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                // the first start of a service writes its starter settings file, as the service's own account (the install makes only the data folder)
                // Design: docs/install.md#a-service-owns-its-data
                // only in a data folder that exists (the install made it, or the person who runs the server did): a start on a folder that is not there
                // makes nothing before its settings are read. A folder this account cannot write is not reported here: building the server opens it
                // and says why, a settings error keeping its exit code
                if (Directory.Exists(dataDirectory) && !Directory.Exists(GatewayConfigFile.DirectoryIn(dataDirectory)))
                {
                    try { GatewayConfigFile.EnsureStarter(dataDirectory); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
                // reads the settings of the data folder (config/server.yaml), validates them and builds the server
                app = GatewayHost.Build(hostArgs, chooseListeners: o => choice = GatewayListeners.Choose(o, step.Options.ExcludedPorts()));
            })
            .Step(DbDataKit.Hosting.Startup.StartupStep.Ports, (step, ct) =>
            {
                if (choice?.Failure is { } noPort) throw noPort;
                var options = app!.Services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<GatewayOptions>>().CurrentValue;
                // every address of every listener is a port the start may be refused: the step names the one the host could not bind
                var ports = GatewayListeners.Parsed([.. options.Public.Listen, .. options.Listen, .. options.Management.Listen, .. options.Identity.Listen, .. options.Apps.Listen, .. options.Store.Listen, .. options.Views.Listen, .. options.Mcp.Listen, .. options.Rest.Listen]).Select(u => u.Port).Where(p => p != 0);
                return StartAndPinAsync(step, app, ports, choice, dataDirectory, ct);
            })
            .Step(DbDataKit.Hosting.Startup.StartupStep.Join, (_, ct) => Management.GatewayManagementHost.JoinAsync(app!, dataDirectory, ct))
            .RunAsync(context.Cancellation);
        if (!result.Succeeded)
        {
            // invalid configuration keeps its documented exit code; its message names the key paths and sources, never a secret value
            await context.Error.WriteLineAsync(result.Error is GatewayConfigException or Secrets.PlainSecretNotAllowedException ? result.Error.Message : result.Summary());
            if (app is not null) await app.DisposeAsync();
            return result.Error is GatewayConfigException or Secrets.PlainSecretNotAllowedException ? GatewayConfigException.ExitCode : result.ExitCode;
        }

        try
        {
            // the host's own lifetime: Ctrl+C, SIGTERM, systemd and the Windows service manager all stop it the same way
            await Microsoft.Extensions.Hosting.HostingAbstractionsHostExtensions.WaitForShutdownAsync(app!, context.Cancellation);
        }
        finally
        {
            await app!.DisposeAsync();
        }
        return CliExit.Ok;
    }

    /// <summary>Starts the host (the ports step) and, once every listener is bound, writes the addresses that were chosen at this start to <c>server.yaml</c>, so the next start listens on the same ones.</summary>
    private static async Task StartAndPinAsync(DbDataKit.Hosting.Startup.StartupContext step, Microsoft.AspNetCore.Builder.WebApplication app, IEnumerable<int> ports, ListenerChoice? choice, string dataDirectory, CancellationToken ct)
    {
        await DbDataKit.Hosting.Startup.StartupPorts.BindAsync(step, app.StartAsync, ports, ct);
        if (choice is { Chosen.Count: > 0 }) GatewayListeners.Pin(GatewayConfigFile.PathIn(dataDirectory), choice.Chosen);
    }

    // ---- state ----

    /// <summary>
    /// The shared <c>state show|test|grants|init</c> verbs over the gateway's state: the <c>state:</c> section of its <c>server.yaml</c> (and the environment variables that override it), where <c>state init</c> writes it, the file <c>state.db</c> in its data directory (<c>--repo</c> names it, as for the pairing verbs), the
    /// modules and the default group it opens them with, and a password that is a secret reference looked up as the server looks it up.
    /// </summary>
    private static Noun StateNoun() => DbDataKit.State.Cli.StateVerbs.Create(new DbDataKit.State.Cli.StateVerbContext(
        Program, "the gateway",
        args =>
        {
            try
            {
                var directory = args.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : null;
                var options = GatewayConfiguration.LoadOptions(directory);
                if (directory is not null) options.DataDirectory = directory;
                var dataDirectory = GatewayHost.ResolveDataDirectory(options);
                return DbDataKit.State.Cli.StateTarget.From(
                    GatewayConfiguration.LoadState(directory),
                    State.GatewayState.PathIn(dataDirectory),
                    () => new DbDataKit.Secrets.SecretResolverChain(Secrets.SecretsRuntime.CreateProviders(options, Secrets.SecretsRuntime.OpenStore(options))),
                    GatewayConfigFile.PathIn(dataDirectory));
            }
            catch (GatewayConfigException ex) { throw new InvalidOperationException(ex.Message, ex); }
        },
        State.GatewayState.Modules, State.GatewayState.ScopeOf(), ["repo"], "[--repo <data directory>]")
    {
        // the keys of the secret store this command line reads (--repo names the data directory): what a moved signing key or group secret is decrypted with
        SecretSamplesFor = args =>
        {
            var directory = args.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : null;
            return State.GatewayStateSecrets.For(() =>
            {
                var options = GatewayConfiguration.LoadOptions(directory);
                if (directory is not null) options.DataDirectory = directory;
                return options;
            });
        },
    });

    // ---- service ----

    private static Noun ServiceNoun() => new("service", "Registration of 'server run' with Windows or systemd",
    [
        Service("install", "Register the Windows service (run elevated); it runs as NT SERVICE\\<name> unless --account says otherwise", EffectClass.TargetWrites,
            "[--name N] [--account A] [--password-secret env:NAME|file:PATH] [--data-dir PATH] [--skip-folder-permissions-check] [--dry-run]"),
        Service("uninstall", "Stop and remove the service", EffectClass.TargetWrites, "[--name N]"),
        Service("status", "Whether the service is installed and running", EffectClass.OfflineOnly, "[--name N]"),
        Service("start", "Start the service", EffectClass.TargetWrites, "[--name N]"),
        Service("stop", "Stop the service", EffectClass.TargetWrites, "[--name N]"),
        Service("restart", "Stop and start the service", EffectClass.TargetWrites, "[--name N]"),
        Service("check", "Check the registration, the account and the directory permissions", EffectClass.OfflineOnly, "[--name N]"),
        Service("unit", "Print a hardened systemd unit that runs 'server run'", EffectClass.OfflineOnly, "[--name N] [--exec /path/to/dbdatagateway]"),
    ]);

    private static Verb Service(string name, string summary, EffectClass effect, string usage) =>
        new(name, summary, effect, usage, async (args, context) => await ServiceCli.RunAsync([name, .. args], ServiceCliEnvironment.Real(DbDataKit.Hosting.Cli.HostingNouns.ConsoleFor(context))), DataSchemas.None);

    // ---- cert ----

    private static Noun CertNoun() => new("cert", "The server's TLS certificate",
    [
        Cert("show", "The certificate in use; never creates or changes anything", EffectClass.OfflineOnly),
        Cert("ensure", "Create the self-signed certificate if needed", EffectClass.RepoFilesOnly),
        Cert("check", "Exit 1 when the certificate is missing or due for rotation", EffectClass.OfflineOnly),
        Cert("renew", "Replace the self-signed certificate now", EffectClass.RepoFilesOnly),
    ]);

    private static Verb Cert(string name, string summary, EffectClass effect) =>
        new(name, summary, effect, "", (args, context) =>
        {
            var options = GatewayConfiguration.LoadOptions();
            return Task.FromResult(CertCli.Run([name, .. args], options, GatewayHost.ResolveDataDirectory(options), DbDataKit.Hosting.Cli.HostingNouns.ConsoleFor(context)));
        }, DataSchemas.None);

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
    ]);

    private static Verb Secret(string name, string summary, EffectClass effect, string usage) =>
        new(name, summary, effect, usage, async (args, context) =>
            await SecretCommand.RunAsync([name, .. args], GatewayConfiguration.LoadOptions(), DbDataKit.Secrets.Cli.SecretNoun.Captured(context, new DbDataKit.Secrets.Cli.SystemConsole())), DataSchemas.None);

    // ---- config ----

    private static Noun ConfigNoun() => new("config", "The server's own configuration",
    [
        new Verb("check", "Validate the configuration (exit 0 valid, 2 invalid)", EffectClass.OfflineOnly, "[--file F]",
            (args, context) => Task.FromResult(ConfigCommand.Run(["check", .. args], context.Out, context.Error)), DataSchemas.None),
        new Verb("show", "The effective configuration with secrets masked; --origin adds where each value came from", EffectClass.OfflineOnly, "[--origin] [--file F]",
            (args, context) => Task.FromResult(ConfigCommand.Run(["show", .. args], context.Out, context.Error)), DataSchemas.None),
    ]);

    // ---- query ----

    private static Noun QueryNoun() => new("query", "A client for smoke-testing a running gateway",
    [
        new Verb("run", "Run one query against a gateway and print the result", EffectClass.OfflineOnly,
            "--user U --password P --sql \"...\" [--address URL] [--datasource N] [--compression none|gzip|zstd|lz4] [--max-rows N] [--insecure]",
            (args, context) => QueryCommand.RunAsync(args, context.Out, context.Error), DataSchemas.None),
    ]);

    // ---- datasource ----

    private static Noun DataSourceNoun() => new("datasource", "The gateway's data sources (the name the query client's --datasource uses)",
    [
        new Verb("adopt", "Hand a data source to a paired server client (a Build server), which then manages it; the running gateway must have its local channel on", EffectClass.TargetWrites,
            DataSourceCommand.Usage, (args, context) => DataSourceCommand.AdoptAsync(args, context, PairingFilesOf),
            DataSchemas.Object([("name", DataSchemas.String), ("client", DataSchemas.String), ("adopted", DataSchemas.Boolean), ("previous_owner", DataSchemas.String)], "name", "client", "adopted")),
    ]);

    // ---- health ----

    private static Noun HealthNoun() => new("health", "Is a running gateway answering",
    [
        new Verb("check", "Exit 0 when the running gateway answers /health/live on its pinned listen address (for a container's HEALTHCHECK)", EffectClass.OfflineOnly, "[--url <url>] [--repo <data directory>]",
            async (args, context) =>
            {
                var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "url", "repo" });
                if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
                {
                    await context.Error.WriteLineAsync("Usage: health check [--url <url>] [--repo <data directory>]");
                    return CliExit.Usage;
                }
                var options = HealthCheck.Options(GatewayConfiguration.LoadOptions(parsed.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : null));
                var text = parsed.Get("url") ?? options.DefaultUrl;
                if (!Uri.TryCreate(text, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
                {
                    await context.Error.WriteLineAsync("The address must be an http or https URL.");
                    return CliExit.Usage;
                }
                var exit = await DbDataKit.Hosting.HealthProbeClient.RunAsync(url, TimeSpan.FromSeconds(4), null, options);
                context.Data["healthy"] = exit == 0;
                context.Data["url"] = url.ToString();
                await (exit == 0 ? context.Out : context.Error).WriteLineAsync(exit == 0 ? "healthy" : "unhealthy");
                return exit;
            }, DataSchemas.Object([("healthy", DataSchemas.Boolean), ("url", DataSchemas.String)], "healthy", "url")),
    ]);
}
