using DbDataKit.Security.Acl;
using System.Security;
using DbDataKit.Hosting.Systemd;
using DbDataKit.Hosting.WindowsService;

namespace DbDataKit.Hosting.Cli;

public static class ExitCodes
{
    public const int Ok = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int NotElevated = 3;
}

/// <summary>Product-specific facts the shared command needs.</summary>
public sealed class ServiceCommandContext
{
    public required string ServiceName { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>The service executable (normally under the <c>current</c> junction).</summary>
    public required string BinaryPath { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string? EventLogSource { get; init; }

    public string? Version { get; init; }

    public required DirectoryLayout Layout { get; init; }

    /// <summary>Supplies the layout-principal resolver once the account is known (the service SID is machine specific).</summary>
    public required Func<ServiceAccount, IPrincipalResolver> PrincipalResolver { get; init; }

    /// <summary>Product's systemd unit, for <c>service unit</c> on Linux.</summary>
    public Func<SystemdUnitOptions>? SystemdUnit { get; init; }

    /// <summary>Extra doctor checks, e.g. certificate expiry.</summary>
    public Func<IEnumerable<DoctorCheck>>? ExtraDoctorChecks { get; init; }

    public TimeSpan StopTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Start mode; null keeps the library default (automatic, delayed).</summary>
    public ServiceStartMode? StartMode { get; init; }

    /// <summary>Failure actions; null keeps <see cref="FailurePolicy.Default"/>.</summary>
    public FailurePolicy? Failure { get; init; }

    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>Service SID type; null keeps unrestricted.</summary>
    public ServiceSidType? SidType { get; init; }

    /// <summary>Trim the service token to these privileges; null leaves the default set.</summary>
    public IReadOnlyList<string>? RequiredPrivileges { get; init; }

    /// <summary>
    /// Option name that supplies a data directory (default <c>data-dir</c>) and the argument the service is started
    /// with for it; null makes <c>--data-dir</c> add <c>--data-dir PATH</c> to the service command line. Products
    /// whose host takes it as configuration (<c>--Section:Key=PATH</c>) set <see cref="DataDirectoryArgument"/>.
    /// </summary>
    public Func<string, IReadOnlyList<string>>? DataDirectoryArgument { get; init; }
}

/// <summary>
/// <c>service install|uninstall|start|stop|restart|status|doctor|unit|run</c>. Passwords are only read from stdin
/// (<c>--password-stdin</c>), never from the command line.
/// </summary>
public sealed class ServiceCommand(
    ServiceCommandContext context,
    IServiceControlManager scm,
    IAclService acl,
    IElevationChecker elevation,
    IEventLogSourceRegistrar? eventLog = null,
    bool applyAcls = true,
    Func<TimeSpan, Task>? delay = null)
{
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "password-stdin", "allow-localsystem", "dry-run", "allow-downgrade", "console",
    };

    public const string Usage =
        "Usage: service <install|uninstall|start|stop|restart|status|doctor|unit|run> [options]\n" +
        "  install   [--account virtual|gmsa$|DOMAIN\\user] [--password-stdin] [--allow-localsystem]\n" +
        "            [--data-dir PATH] [--dry-run] [--allow-downgrade --installed-version V]\n" +
        "  run       run in the foreground (same as starting the product with --console)";

    public int Execute(string[] args, IConsole console) =>
        ExecuteAsync(args, console).GetAwaiter().GetResult();

    public async Task<int> ExecuteAsync(string[] args, IConsole console)
    {
        ParsedArgs parsed;
        try
        {
            parsed = ParsedArgs.Parse(args, Flags);
        }
        catch (ArgumentException ex)
        {
            console.Error.WriteLine(ex.Message);
            return ExitCodes.Usage;
        }

        if (parsed.Has("password"))
        {
            console.Error.WriteLine("Passwords are not accepted on the command line. Use --password-stdin and pipe the value in.");
            return ExitCodes.Usage;
        }

        var verb = parsed.Positionals.FirstOrDefault()?.ToLowerInvariant();
        var provisioner = new ServiceProvisioner(scm, eventLog, delay);
        try
        {
            switch (verb)
            {
                case "install": return Install(parsed, console, provisioner);
                case "uninstall": return await UninstallAsync(parsed, console, provisioner);
                case "start": return await StartStop(console, "start", () => provisioner.StartAsync(context.ServiceName, context.StopTimeout));
                case "stop": return await StartStop(console, "stop", () => provisioner.StopAsync(context.ServiceName, context.StopTimeout));
                case "restart":
                    return await StartStop(console, "restart", async () =>
                    {
                        await provisioner.StopAsync(context.ServiceName, context.StopTimeout);
                        await provisioner.StartAsync(context.ServiceName, context.StopTimeout);
                    });
                case "status": return Status(console);
                case "doctor": return Doctor(parsed, console);
                case "unit": return Unit(console);
                case "run":
                    console.Out.WriteLine("Run the product executable with --console to host it in the foreground.");
                    return ExitCodes.Ok;
                default:
                    console.Error.WriteLine(Usage);
                    return ExitCodes.Usage;
            }
        }
        catch (ServiceInstallException ex)
        {
            console.Error.WriteLine(ex.Message);
            return ExitCodes.Failure;
        }
        catch (ServiceControlException ex)
        {
            console.Error.WriteLine(ex.Error == ServiceControlError.AccessDenied
                ? ElevationDetector.Message("this operation")
                : ex.Message);
            return ex.Error == ServiceControlError.AccessDenied ? ExitCodes.NotElevated : ExitCodes.Failure;
        }
        catch (TimeoutException ex)
        {
            console.Error.WriteLine(ex.Message);
            return ExitCodes.Failure;
        }
    }

    private ServiceInstallSpec BuildSpec(ParsedArgs parsed)
    {
        var args = context.Arguments.ToList();
        if (parsed.Get("data-dir") is { } dataDir)
        {
            args.AddRange(context.DataDirectoryArgument?.Invoke(dataDir) ?? ["--data-dir", dataDir]);
        }

        return new ServiceInstallSpec
        {
            Name = context.ServiceName,
            DisplayName = context.DisplayName,
            Description = context.Description,
            BinaryPath = context.BinaryPath,
            Arguments = args,
            Account = ServiceAccount.Parse(parsed.Get("account"), context.ServiceName),
            AllowLocalSystem = parsed.Has("allow-localsystem"),
            StartMode = context.StartMode ?? ServiceStartMode.AutomaticDelayed,
            Failure = context.Failure ?? FailurePolicy.Default,
            Dependencies = context.Dependencies,
            SidType = context.SidType ?? ServiceSidType.Unrestricted,
            RequiredPrivileges = context.RequiredPrivileges,
            EventLogSource = context.EventLogSource,
            Version = context.Version,
        };
    }

    private int Install(ParsedArgs parsed, IConsole console, ServiceProvisioner provisioner)
    {
        var dryRun = parsed.Has("dry-run");
        if (!dryRun && !elevation.IsElevated)
        {
            console.Error.WriteLine(ElevationDetector.Message("service install"));
            return ExitCodes.NotElevated;
        }

        var spec = BuildSpec(parsed);
        SecureString? password = null;
        if (parsed.Has("password-stdin"))
        {
            var line = console.ReadLine();
            if (string.IsNullOrEmpty(line))
            {
                console.Error.WriteLine("--password-stdin was given but standard input was empty.");
                return ExitCodes.Usage;
            }

            password = SecureStrings.From(line);
        }

        using (password)
        {
            var options = new InstallOptions
            {
                DryRun = dryRun,
                AllowDowngrade = parsed.Has("allow-downgrade"),
                InstalledVersion = parsed.Get("installed-version"),
            };
            var result = provisioner.Install(spec, password, options);
            console.Out.Write(result.Plan.Render());

            if (!dryRun && applyAcls)
            {
                try
                {
                    var resolver = context.PrincipalResolver(spec.EffectiveAccount);
                    foreach (var entry in context.Layout.Entries.Where(e => !e.IsLink && e.Acl is not null))
                    {
                        acl.Apply(entry, resolver);
                    }
                }
                catch (Exception ex) when (ex is System.Security.Principal.IdentityNotMappedException or UnauthorizedAccessException or IOException or ArgumentException or PlatformNotSupportedException)
                {
                    // The service exists by now: say so, and that running install again finishes the job.
                    console.Error.WriteLine($"Service '{spec.Name}' was registered, but its directory permissions could not be applied: {ex.Message}");
                    console.Error.WriteLine("Fix the cause (for example the account name) and run 'service install' again; it is safe to repeat.");
                    return ExitCodes.Failure;
                }

                console.Out.WriteLine("Directory permissions applied.");
            }

            console.Out.WriteLine(result.Outcome switch
            {
                InstallOutcome.Created => $"Service '{spec.Name}' installed as {spec.EffectiveAccount.Name}.",
                InstallOutcome.Reconfigured => $"Service '{spec.Name}' updated.",
                InstallOutcome.Unchanged => $"Service '{spec.Name}' already up to date.",
                _ => "Dry run: no changes made.",
            });
            return ExitCodes.Ok;
        }
    }

    private async Task<int> UninstallAsync(ParsedArgs parsed, IConsole console, ServiceProvisioner provisioner)
    {
        var dryRun = parsed.Has("dry-run");
        if (!dryRun && !elevation.IsElevated)
        {
            console.Error.WriteLine(ElevationDetector.Message("service uninstall"));
            return ExitCodes.NotElevated;
        }

        var removed = await provisioner.UninstallAsync(context.ServiceName, context.StopTimeout, dryRun, context.EventLogSource);
        console.Out.WriteLine(!removed ? "Service is not installed." : dryRun ? "Dry run: would stop and delete the service." : "Service removed.");
        return ExitCodes.Ok;
    }

    private async Task<int> StartStop(IConsole console, string verb, Func<Task> action)
    {
        if (!elevation.IsElevated)
        {
            console.Error.WriteLine(ElevationDetector.Message($"service {verb}"));
            return ExitCodes.NotElevated;
        }

        await action();
        console.Out.WriteLine($"Service {verb} completed.");
        return ExitCodes.Ok;
    }

    private int Status(IConsole console)
    {
        var cfg = scm.GetConfig(context.ServiceName);
        if (cfg is null)
        {
            console.Out.WriteLine($"{context.ServiceName}: not installed");
            return ExitCodes.Failure;
        }

        var status = scm.QueryStatus(context.ServiceName);
        console.Out.WriteLine($"{context.ServiceName}: {status.State}{(status.ProcessId > 0 ? $" (pid {status.ProcessId})" : "")}");
        console.Out.WriteLine($"  account: {cfg.AccountName}");
        console.Out.WriteLine($"  start:   {cfg.StartMode}");
        console.Out.WriteLine($"  command: {cfg.BinaryPathName}");
        return ExitCodes.Ok;
    }

    private int Doctor(ParsedArgs parsed, IConsole console)
    {
        var spec = BuildSpec(parsed);
        var existing = scm.GetConfig(context.ServiceName);
        var account = existing?.AccountName is { } n ? ServiceAccount.Parse(n, context.ServiceName) : spec.EffectiveAccount;
        var doctor = new ServiceDoctor(scm, acl, context.PrincipalResolver(account));
        var report = doctor.Run(spec, context.Layout, context.ExtraDoctorChecks?.Invoke());
        foreach (var c in report.Checks)
        {
            var tag = c.Severity switch { DoctorSeverity.Pass => "ok  ", DoctorSeverity.Warn => "warn", _ => "FAIL" };
            console.Out.WriteLine($"[{tag}] {c.Id}: {c.Message}");
        }

        console.Out.WriteLine(report.Ok ? $"Doctor: OK ({report.Warnings} warning(s))." : $"Doctor: {report.Failures} failure(s).");
        return report.Ok ? ExitCodes.Ok : ExitCodes.Failure;
    }

    private int Unit(IConsole console)
    {
        if (context.SystemdUnit is null)
        {
            console.Error.WriteLine("This product does not define a systemd unit.");
            return ExitCodes.Failure;
        }

        console.Out.Write(SystemdUnitGenerator.Generate(context.SystemdUnit()));
        return ExitCodes.Ok;
    }
}
