using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;
using DbDataSync.Api.Auth;
using DbDataSync.Api.Configuration;
using DbDataSync.Core.Config;
using DbDataSync.Core.Git;

namespace DbDataSync.Cli;

/// <summary>What <see cref="ServiceCommand"/>'s Windows verbs touch outside the process — the Service Control Manager, the data
/// directory's permissions and the Event Log source — so a test can run them on any OS against an in-memory SCM.</summary>
internal sealed class ServiceEnvironment
{
    public required IServiceControlManager Scm { get; init; }

    /// <summary>Takes ownership of the data directory for the service account and grants it access (<c>icacls</c>).</summary>
    public required Action<string, string?> GrantDataDirectoryAccess { get; init; }

    /// <summary>Registers the Event Log source the service writes startup failures to.</summary>
    public required Action EnsureEventSource { get; init; }

    public static ServiceEnvironment Real() => new()
    {
        Scm = OperatingSystem.IsWindows() ? new WindowsServiceControlManager() : new InMemoryServiceControlManager(),
        GrantDataDirectoryAccess = (root, account) => ServiceCommand.GrantDataDirectoryAccess(root, account),
        EnsureEventSource = () =>
        {
            if (OperatingSystem.IsWindows())
                WindowsServiceEventLog.EnsureSourceRegistered();
        },
    };
}

/// <summary>
/// Registers this tool as a Windows service or (phase 111) a Linux systemd service, and says what it
/// did.
/// <para>
/// The binary registered is **the tool's own apphost shim** — the <c>dbdatasync.exe</c>/apphost the
/// SDK put in the user's tools directory — with <c>serve</c> and fully resolved paths as its
/// arguments. Resolved at install time and printed, because a service has no console to say "I could
/// not find my config repository" on: the moment to find that out is now.
/// </para>
/// <para>
/// The systemd path lives in its own file (<see cref="SystemdService"/>), the same way this file's
/// own <c>sc.exe</c> mechanics stay separate from it — each platform's machinery is separable, even
/// though both are reached through this one command.
/// </para>
/// </summary>
public static class ServiceCommand
{
    public const string ServiceName = "DbDataSync";

    private const string WindowsUsage = "Usage: dbdatasync service install|uninstall|status|start|stop|restart|doctor";

    public static int Run(string[] args)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine(WindowsUsage);
                return 1;
            }

            return args[0].ToLowerInvariant() switch
            {
                "install" => Install(args),
                "uninstall" => Uninstall(args),
                "status" => Status(),
                "start" or "stop" or "restart" => Control(args[0].ToLowerInvariant()),
                "doctor" => Doctor(),
                var other => Unknown(other),
            };
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            if (args.Length == 0)
            {
                Console.Error.WriteLine("Usage: dbdatasync service install|uninstall|status");
                return 1;
            }

            return args[0].ToLowerInvariant() switch
            {
                "install" => SystemdService.Install(args),
                "uninstall" => SystemdService.Uninstall(args),
                "status" => SystemdService.Status(),
                var other => Unknown(other),
            };
        }

        Console.Error.WriteLine(
            "Service registration is Windows and Linux (systemd) only. On macOS, run `dbdatasync serve` " +
            "directly, or use the container image.");
        return 1;
    }

    /// <param name="elevatedOverride">Only for <c>ServiceCommandTests</c> — the same test-only escape
    /// hatch <see cref="SystemdService.Install"/>'s own <c>executableOverride</c> is, and for the same
    /// reason: CI's <c>windows-latest</c> runners are elevated, so the refusal branch below is
    /// unreachable there without one.</param>
    /// <param name="environment">Only for tests: the Service Control Manager and the other machine-level effects.</param>
    /// <param name="executableOverride">Only for tests: skips resolving this process's path and the launcher conversion.</param>
    internal static int Install(
        string[] args, bool? elevatedOverride = null, ServiceEnvironment? environment = null, string? executableOverride = null)
    {
        var executable = executableOverride ?? Environment.ProcessPath;
        if (executable is null)
        {
            Console.Error.WriteLine("Could not determine this tool's own executable path.");
            return 1;
        }

        // Before anything that touches the machine, because every step below needs elevation and two
        // of them fail badly without it: a non-elevated run used to rewrite part of the data directory's
        // ACLs, then crash on the Event Log source (SecurityException, exit 127) and leave a
        // half-modified directory with nothing saying "run this elevated". Refusing up front is what
        // ToolCommand's Unix path already does ("Needs root. Run: sudo ...").
        // The OperatingSystem.IsWindows() half is for the analyzer, not the logic.
        var elevated = elevatedOverride ?? (!OperatingSystem.IsWindows() || WindowsElevation.IsAdministrator());

        if (!elevated)
        {
            Console.Error.WriteLine(
                "Needs Administrator. Registering a Windows service, taking ownership of the data " +
                "directory and creating the Event Log source all require elevation.");
            Console.Error.WriteLine(
                "Re-run this command from an elevated prompt (right-click the terminal, Run as " +
                "administrator). Nothing has been changed.");
            return 1;
        }

        var env = environment ?? ServiceEnvironment.Real();
        var root = Path.GetFullPath(CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot);
        // Explicit vs. defaulted matters: an explicit --url is written into dbdatasync.config.yaml
        // (below) so it survives every future restart; a defaulted one is never written, or a bare
        // re-run to change --account would stomp whatever App:Url the operator has since configured
        // through the Admin screen.
        var explicitUrl = CliOptions.Read(args, "--url");
        var url = explicitUrl
            ?? DbDataSyncConfigFile.Read(root).GetValueOrDefault("DbDataSync:App:Url")
            ?? ApiOptions.DefaultUrl;
        var account = CliOptions.Read(args, "--account");

        // Phase 196L: registered against the launcher, once — an update then never touches what the service names.
        // From a pre-196L --tool-path install this converts it first (saying so); the launcher replaces the tool's
        // shim at the same path, so the path registered is the one it always was.
        if (executableOverride is null)
            executable = LauncherSetup.EnsureAsync(root, Console.Out, Console.Error, CancellationToken.None).GetAwaiter().GetResult() ?? executable;

        // A warning, not a refusal — sc.exe has no equivalent of systemd's ProtectHome, so a
        // user-profile executable does not fail the service the way it can on Linux (SystemdService's
        // own hard error). It still breaks the moment that profile is cleaned up or the service is
        // re-registered from another account.
        if (CliOptions.IsUnderUserProfile(executable))
        {
            Console.WriteLine(
                $"Warning: '{executable}' is installed in a user profile — a service pointing here " +
                "breaks if that profile is removed, or you re-register from another account. Install " +
                "machine-wide first:");
            Console.WriteLine($"    dotnet tool install --tool-path \"{CliOptions.DefaultToolDir}\" DbDataSync");
            Console.WriteLine($"    \"{CliOptions.DefaultToolDir}\\dbdatasync.exe\" tool install");
            Console.WriteLine();
        }

        // A password for a named user account arrives on standard input, never on a command line (--password-stdin).
        // LocalSystem, a virtual account and a gMSA take none.
        SecureString? password = null;
        if (CliOptions.Has(args, "--password-stdin"))
        {
            var line = Console.In.ReadLine();
            if (string.IsNullOrEmpty(line))
            {
                Console.Error.WriteLine("--password-stdin was given but standard input was empty. Nothing has been changed.");
                return 1;
            }

            password = SecureStrings.From(line);
        }

        // Only when explicitly given, and only when there is already a git-tracked config to write it
        // into — service install assumes a repo `serve`/`setup` already created; skipping silently
        // otherwise, rather than writing an uncommitted file, matches every other config write in this
        // app being git-tracked.
        if (explicitUrl is not null && LibGit2Sharp.Repository.IsValid(root))
        {
            DbDataSyncConfigFile.SetValue(root, "DbDataSync:App", "Url", explicitUrl);
            new GitCommitService(root).CommitChanges(
                [DbDataSyncConfigFile.PathIn(root)], "Set 'DbDataSync:App:Url' in dbdatasync.config.yaml", CurrentUser.SystemAuthor);
        }

        Console.WriteLine($"Registering the '{ServiceName}' service:");
        Console.WriteLine($"  executable         {executable}");
        Console.WriteLine($"  config repository  {Path.Combine(root, "config")}");
        Console.WriteLine($"  console            {url}");
        Console.WriteLine($"  account            {account ?? "LocalSystem"}");

        // Not cosmetic: a connection using IntegratedAuth connects *as the service account*, and
        // phase 52's Windows auth authorises against a group this account may or may not be in. The
        // install command is where an operator finds that out rather than at the first failed run.
        Console.WriteLine();
        Console.WriteLine(
            "  Connections using integrated authentication will connect as this account.");

        using (password)
        {
            ServiceInstallSpec spec;
            try
            {
                spec = Spec(executable, root, account);
            }
            catch (ArgumentException ex)
            {
                Console.Error.WriteLine($"{ex.Message} Nothing has been changed.");
                return 1;
            }

            var provisioner = new ServiceProvisioner(env.Scm);

            // Validated before the machine is touched (the directory's ownership, the Event Log source), so a refusal
            // — a user account with no password, say — changes nothing.
            try
            {
                provisioner.Plan(spec, password);
            }
            catch (ServiceInstallException ex)
            {
                Console.Error.WriteLine($"{ex.Message} Nothing has been changed.");
                return 1;
            }

            env.GrantDataDirectoryAccess(root, account);

            // Phase 136: registered here, elevated, rather than lazily at the moment a startup failure
            // first needs to write to it — see WindowsServiceEventLog.EnsureSourceRegistered's own doc
            // comment. A registration failure is visible in this command's own output, not silently
            // deferred to the first real failure under the service.
            env.EnsureEventSource();

            InstallResult result;
            try
            {
                result = provisioner.Install(spec, password);
            }
            catch (ServiceControlException ex)
            {
                return Failed(ex);
            }
            catch (ServiceInstallException ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }

            Console.WriteLine(result.Outcome switch
            {
                InstallOutcome.Created => $"Service '{ServiceName}' installed.",
                InstallOutcome.Reconfigured => $"Service '{ServiceName}' updated.",
                _ => $"Service '{ServiceName}' already up to date.",
            });
            ServiceRegistration.Write(root, account ?? "LocalSystem", "windows");
            Console.WriteLine("Next: `dbdatasync config check`.");
            return 0;
        }
    }

    /// <summary>What Windows is told to run, and as whom. Kept as it has always been: <c>LocalSystem</c> unless <c>--account</c>
    /// says otherwise (so LocalSystem is allowed without a flag), start mode automatic, no failure-recovery actions and no service SID.</summary>
    internal static ServiceInstallSpec Spec(string executable, string root, string? account) => new()
    {
        Name = ServiceName,
        DisplayName = ServiceName,
        BinaryPath = executable,
        Arguments = ["serve", "--repo", root],
        Account = ServiceAccount.Parse(account ?? "LocalSystem", ServiceName),
        AllowLocalSystem = true,
        StartMode = ServiceStartMode.Automatic,
        Failure = new FailurePolicy([], TimeSpan.Zero),
        SidType = ServiceSidType.None,
    };

    /// <param name="elevatedOverride">Only for <c>ServiceCommandTests</c> — see <see cref="Install"/>'s
    /// own parameter of the same name.</param>
    /// <param name="environment">Only for tests: see <see cref="Install"/>.</param>
    internal static int Uninstall(string[] args, bool? elevatedOverride = null, ServiceEnvironment? environment = null)
    {
        var root = Path.GetFullPath(CliOptions.Read(args, "--repo") ?? CliOptions.DefaultRoot);

        // Same guard as Install, for the quieter half of the same bug. Removing the service needs elevation, and
        // without this the command answered with "Access is denied" *after* having already deleted the local
        // registration record below — a failed uninstall that left the service installed and running while
        // telling the rest of the tool it was gone.
        var elevated = elevatedOverride ?? (!OperatingSystem.IsWindows() || WindowsElevation.IsAdministrator());

        if (!elevated)
        {
            Console.Error.WriteLine("Needs Administrator. Removing a Windows service requires elevation.");
            Console.Error.WriteLine(
                "Re-run this command from an elevated prompt (right-click the terminal, Run as " +
                "administrator). Nothing has been changed.");
            return 1;
        }

        var env = environment ?? ServiceEnvironment.Real();
        bool removed;
        try
        {
            // Stops it first (waiting for it), then deletes.
            removed = new ServiceProvisioner(env.Scm).UninstallAsync(ServiceName, StopTimeout).GetAwaiter().GetResult();
        }
        catch (ServiceControlException ex)
        {
            return Failed(ex);
        }
        catch (TimeoutException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (!removed)
        {
            Console.WriteLine($"The '{ServiceName}' service is not installed.");
            return 1;
        }

        // After the service is gone, and only on success. The record is what ReadinessChecks (`config check`) and
        // ServeCommand's own startup-failure message read to report which account a registered service
        // runs as — the diagnostic context phases 135 and 136 added precisely so an Error 1053 names
        // something. Clearing it while the service is still installed does not just lose a file, it
        // makes both of those quietly report a machine that does not exist.
        ServiceRegistration.Clear(root);
        Console.WriteLine($"Removed the '{ServiceName}' service. Its repository and state are untouched.");
        return 0;
    }

    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    /// <summary>The service's state, account, start mode and command line.</summary>
    internal static int Status(ServiceEnvironment? environment = null)
    {
        var scm = (environment ?? ServiceEnvironment.Real()).Scm;
        try
        {
            var config = scm.GetConfig(ServiceName);
            if (config is null)
            {
                Console.WriteLine($"{ServiceName}: not installed");
                return 1;
            }

            var status = scm.QueryStatus(ServiceName);
            Console.WriteLine($"{ServiceName}: {status.State}{(status.ProcessId > 0 ? $" (pid {status.ProcessId})" : "")}");
            Console.WriteLine($"  account: {config.AccountName ?? "LocalSystem"}");
            Console.WriteLine($"  start:   {config.StartMode}");
            Console.WriteLine($"  command: {config.BinaryPathName}");
            return 0;
        }
        catch (ServiceControlException ex)
        {
            return Failed(ex);
        }
    }

    /// <summary><c>start</c>, <c>stop</c> and <c>restart</c>, each waiting until the service gets there — what the MSI can
    /// call instead of PowerShell.</summary>
    internal static int Control(string verb, bool? elevatedOverride = null, ServiceEnvironment? environment = null)
    {
        var elevated = elevatedOverride ?? (!OperatingSystem.IsWindows() || WindowsElevation.IsAdministrator());
        if (!elevated)
        {
            Console.Error.WriteLine($"Needs Administrator. Running '{verb}' on a Windows service requires elevation. Nothing has been changed.");
            return 1;
        }

        var provisioner = new ServiceProvisioner((environment ?? ServiceEnvironment.Real()).Scm);
        try
        {
            switch (verb)
            {
                case "start":
                    provisioner.StartAsync(ServiceName, StartTimeout).GetAwaiter().GetResult();
                    break;
                case "stop":
                    provisioner.StopAsync(ServiceName, StopTimeout).GetAwaiter().GetResult();
                    break;
                default:
                    provisioner.StopAsync(ServiceName, StopTimeout).GetAwaiter().GetResult();
                    provisioner.StartAsync(ServiceName, StartTimeout).GetAwaiter().GetResult();
                    break;
            }
        }
        catch (ServiceControlException ex)
        {
            return Failed(ex);
        }
        catch (TimeoutException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine(verb switch { "start" => "Service started.", "stop" => "Service stopped.", _ => "Service restarted." });
        return 0;
    }

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(60);

    /// <summary>The shared service doctor's checks of the registration (account, command line, start mode, recovery), plus the data directory.</summary>
    internal static int Doctor(string[]? args = null, ServiceEnvironment? environment = null)
    {
        var env = environment ?? ServiceEnvironment.Real();
        var root = Path.GetFullPath(CliOptions.Read(args ?? [], "--repo") ?? CliOptions.DefaultRoot);
        try
        {
            var executable = env.Scm.GetConfig(ServiceName)?.BinaryPathName is { Length: > 0 } installed
                ? ExecutableOf(installed)
                : Environment.ProcessPath ?? "";
            var checks = new List<DoctorCheck>
            {
                Directory.Exists(root)
                    ? new("data.directory", DoctorSeverity.Pass, $"{root} exists.")
                    : new("data.directory", DoctorSeverity.Fail, $"{root} does not exist."),
            };
            var report = new ServiceDoctor(env.Scm, new FileSystemAclService(), new WellKnownPrincipalResolver("S-1-5-18"))
                .Run(Spec(executable, root, account: env.Scm.GetConfig(ServiceName)?.AccountName), DirectoryLayout.Create(ServiceName, []), checks);

            foreach (var check in report.Checks)
            {
                var tag = check.Severity switch { DoctorSeverity.Pass => "ok  ", DoctorSeverity.Warn => "warn", _ => "FAIL" };
                Console.WriteLine($"[{tag}] {check.Id}: {check.Message}");
            }

            Console.WriteLine(report.Ok ? $"Doctor: OK ({report.Warnings} warning(s))." : $"Doctor: {report.Failures} failure(s).");
            return report.Ok ? 0 : 1;
        }
        catch (ServiceControlException ex)
        {
            return Failed(ex);
        }
    }

    /// <summary>The executable inside a service command line such as <c>"C:\x\dbdatasync.exe" serve --repo ...</c>.</summary>
    internal static string ExecutableOf(string commandLine)
    {
        var trimmed = commandLine.Trim();
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            return end > 0 ? trimmed[1..end] : trimmed[1..];
        }

        var space = trimmed.IndexOf(' ');
        return space > 0 ? trimmed[..space] : trimmed;
    }

    private static int Failed(ServiceControlException ex)
    {
        // An operator who gets an access-denied code back deserves the sentence rather than the number.
        Console.Error.WriteLine(ex.Error == ServiceControlError.AccessDenied
            ? "Access denied. Registering, controlling or removing a Windows service needs an elevated prompt — " +
              "run this from a command prompt started with 'Run as administrator'."
            : ex.Message);
        return 1;
    }

    /// <summary>
    /// Issuing and granting are one operation from the operator's point of view — the same principle
    /// phase 82 applied to a certificate's private key. Phase 112 made the data directory machine-wide
    /// (<c>%ProgramData%\DbDataSync</c> by default) rather than per-user, so a named service account
    /// is no longer guaranteed to have write access to it the way a person's own profile directory
    /// would be.
    /// <para>
    /// Phase 135: this runs for every account, including <c>LocalSystem</c> — libgit2's ownership-safety
    /// check (the same protection as git's own CVE-2022-24765 <c>safe.directory</c> fix) cares about the
    /// directory's <b>owner</b>, not its ACL, and a directory created by an earlier interactive run is
    /// not owned by <c>LocalSystem</c> just because <c>LocalSystem</c> already has access rights to it.
    /// <c>/setowner</c> runs before <c>/grant</c>, not instead of it — taking ownership alone gives the
    /// new owner implicit <c>WRITE_DAC</c> (the right to change permissions), not necessarily explicit
    /// data access. icacls's own recognized name for <c>LocalSystem</c> is <c>SYSTEM</c>, not the string
    /// used everywhere else in this file.
    /// </para>
    /// </summary>
    internal static void GrantDataDirectoryAccess(string root, string? account)
    {
        Directory.CreateDirectory(root);
        var effectiveAccount = string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase)
            ? null
            : account;
        effectiveAccount ??= "SYSTEM";

        RunIcacls(root, ["/setowner", effectiveAccount, "/T", "/C"],
            $"take ownership of '{root}' for '{effectiveAccount}'");
        RunIcacls(root, ["/grant", $"{effectiveAccount}:(OI)(CI)M", "/T"],
            $"grant '{effectiveAccount}' access to '{root}'");
    }

    /// <summary>Warns rather than fails — same non-fatal posture as before phase 135: an icacls failure
    /// here is surfaced but does not block <c>service install</c> from registering the service.</summary>
    private static void RunIcacls(string root, IReadOnlyList<string> arguments, string description)
    {
        var startInfo = new ProcessStartInfo("icacls") { UseShellExecute = false };
        startInfo.ArgumentList.Add(root);
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine($"  Warning: could not start icacls to {description}.");
            return;
        }

        process.WaitForExit();
        Console.WriteLine(process.ExitCode == 0
            ? $"  Ran icacls to {description}."
            : $"  Warning: icacls exited {process.ExitCode} trying to {description} — do this manually " +
              "before starting the service.");
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine(OperatingSystem.IsWindows()
            ? $"Unknown service command '{command}'. Use install, uninstall, status, start, stop, restart or doctor."
            : $"Unknown service command '{command}'. Use install, uninstall or status.");
        return 1;
    }

    /// <summary>What the service runs: the executable, <c>serve</c>, and the resolved data directory.</summary>
    internal static string BinPath(string executable, string root) => CommandLineQuoting.Build(executable, ["serve", "--repo", root]);

    internal static int Sc(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("sc.exe") { UseShellExecute = false };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            Console.Error.WriteLine("Could not start sc.exe.");
            return 1;
        }

        process.WaitForExit();

        // 5 is ERROR_ACCESS_DENIED. Registering a service needs elevation, and an operator who gets
        // an access-denied code back deserves the sentence rather than the number.
        if (process.ExitCode == 5)
        {
            Console.Error.WriteLine(
                "Access denied. Registering or removing a Windows service needs an elevated prompt — " +
                "run this from a command prompt started with 'Run as administrator'.");
        }

        return process.ExitCode;
    }
}
