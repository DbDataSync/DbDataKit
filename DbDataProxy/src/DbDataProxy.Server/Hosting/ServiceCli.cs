using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.Systemd;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;
using DbDataProxy.Server.Configuration;

namespace DbDataProxy.Server.Hosting;

/// <summary>What the service verbs act on. Tests substitute fakes; <see cref="Real"/> is the machine.</summary>
public sealed class ServiceCliEnvironment
{
    public required IServiceControlManager Scm { get; init; }

    public required IAclService Acl { get; init; }

    public required IElevationChecker Elevation { get; init; }

    public required IConsole Console { get; init; }

    public required string BinaryPath { get; init; }

    /// <summary>Data directory when <c>--data-dir</c> is not given (from the settings files and DBPROXY_ variables).</summary>
    public required Func<string> DefaultDataDirectory { get; init; }

    /// <summary>Writes the starter settings file into the data directory after an install when it has none; null does nothing (tests).</summary>
    public Action<string>? EnsureSettingsFile { get; init; }

    public bool IsWindows { get; init; } = OperatingSystem.IsWindows();

    /// <summary>Asks the user for a password (hidden input); null when there is no terminal to ask on.</summary>
    public Func<string, string?>? PromptPassword { get; init; }

    public Func<string, string?> GetEnvironmentVariable { get; init; } = System.Environment.GetEnvironmentVariable;

    /// <summary>Maps the service account to the SIDs the directory ACLs are written for.</summary>
    public Func<ServiceAccount, IPrincipalResolver> PrincipalResolver { get; init; } = account => new WindowsPrincipalResolver(account);

    public static ServiceCliEnvironment Real() => new()
    {
        Scm = OperatingSystem.IsWindows() ? new WindowsServiceControlManager() : new InMemoryServiceControlManager(),
        Acl = new FileSystemAclService(),
        Elevation = new ElevationDetector(),
        Console = new SystemConsole(),
        BinaryPath = Environment.ProcessPath ?? "",
        DefaultDataDirectory = () => ProxyHost.ResolveDataDirectory(ProxyConfiguration.LoadOptions()),
        EnsureSettingsFile = dataDirectory => ProxyConfigFile.EnsureStarter(dataDirectory),
        PromptPassword = System.Console.IsInputRedirected ? null : ConsolePrompt.ReadHidden,
    };
}

/// <summary>Reads a line from the terminal without echoing it.</summary>
public static class ConsolePrompt
{
    public static string? ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        var sb = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is { Key: not ConsoleKey.Enter } key)
        {
            if (key.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else sb.Append(key.KeyChar);
        }

        Console.Error.WriteLine();
        return sb.ToString();
    }
}

/// <summary>
/// <c>dbdataproxy service install|uninstall|start|stop|restart|status|doctor|unit</c> on top of the shared service
/// command (SCM API, no sc.exe). <c>install</c> and <c>uninstall</c> keep working as top-level verbs.
/// <para>
/// The service runs as the virtual account <c>NT SERVICE\DbDataProxy</c> unless <c>--account</c> says otherwise
/// (LocalSystem needs <c>--allow-localsystem</c>). A password is never placed on the service command line:
/// <c>--password-secret env:NAME|file:PATH</c> or a prompt are preferred; <c>--password</c> still works but warns.
/// </para>
/// </summary>
public static class ServiceCli
{
    public const string DisplayName = "DbDataProxy Arrow Flight SQL proxy";
    public const string Description = "Arrow Flight SQL proxy that forwards queries to ODBC/ADO.NET databases and compresses the results.";

    public const string Usage = """
        Usage: dbdataproxy service <install|uninstall|start|stop|restart|status|doctor|unit> [options]
          install   [--name N] [--account virtual|DOMAIN\user|DOMAIN\gmsa$] [--allow-localsystem]
                    [--password-secret env:NAME|file:PATH | --password-stdin] [--data-dir PATH] [--dry-run]
                    Register the Windows service (run elevated). Default account: NT SERVICE\<name>.
          uninstall [--name N]            Stop and remove the service
          start|stop|restart|status|doctor [--name N]
          unit      (or --systemd) [--name N] [--exec /path/to/dbdataproxy]
                                          Print a hardened systemd unit
        """;

    public static int Run(string[] args, ServiceCliEnvironment env) => RunAsync(args, env).GetAwaiter().GetResult();

    public static async Task<int> RunAsync(string[] args, ServiceCliEnvironment env)
    {
        var console = env.Console;
        ExtractedArgs extracted;
        try
        {
            extracted = Extract(args);
        }
        catch (ArgumentException ex)
        {
            console.Error.WriteLine(ex.Message);
            return ExitCodes.Usage;
        }

        string verb = extracted.Verb ?? "";
        if (verb == "unit" || extracted.Systemd) return Unit(env, extracted);
        if (verb is not ("install" or "uninstall" or "start" or "stop" or "restart" or "status" or "doctor"))
        {
            console.Error.WriteLine(Usage);
            return ExitCodes.Usage;
        }

        if (!env.IsWindows)
        {
            console.Error.WriteLine("Service management is only available on Windows. On Linux use 'service unit' (systemd) or Docker.");
            return ExitCodes.Failure;
        }

        if (verb == "install" && Path.GetFileNameWithoutExtension(env.BinaryPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            console.Error.WriteLine("Run the service command from dbdataproxy.exe (not 'dotnet dbdataproxy.dll'): the service registers the executable it is started from.");
            return ExitCodes.Failure;
        }

        // Password handling: the shared command only reads one from standard input, so every supported source is
        // turned into that here. Nothing the user typed or referenced reaches a command line or a log.
        string? password = null;
        var forwarded = extracted.Rest.ToList();
        if (verb == "install")
        {
            var resolved = ResolvePassword(extracted, forwarded, env);
            if (resolved.Error is not null)
            {
                console.Error.WriteLine(resolved.Error);
                return ExitCodes.Usage;
            }

            password = resolved.Password;
        }

        if (password is not null && !forwarded.Contains("--password-stdin")) forwarded.Add("--password-stdin");

        string name = extracted.Name ?? ProxyHost.RegistrationName;
        string dataDirectory = ReadOption(forwarded, "--data-dir") is { } d ? Path.GetFullPath(d) : env.DefaultDataDirectory();
        if (ReadOption(forwarded, "--data-dir") is not null) ReplaceOption(forwarded, "--data-dir", dataDirectory);

        var context = Context(name, env.BinaryPath, dataDirectory, env.PrincipalResolver);
        var command = new ServiceCommand(context, env.Scm, env.Acl, env.Elevation, null, true);
        IConsole shared = password is null ? console : new PasswordConsole(console, password);
        int rc = await command.ExecuteAsync([verb, .. forwarded], shared);

        if (rc == 0 && verb == "install" && !forwarded.Contains("--dry-run"))
        {
            env.EnsureSettingsFile?.Invoke(dataDirectory);
            console.Out.WriteLine($"Settings: {ProxyConfigFile.PathIn(dataDirectory)} (appsettings.example.json beside {env.BinaryPath} shows every option). Then run: dbdataproxy service start --name {name}");
            console.Out.WriteLine("Open the listening port in the Windows firewall if clients connect from other machines.");
        }

        return rc;
    }

    /// <summary>The shared command's inputs for this product: layout, failure actions, display text.</summary>
    public static ServiceCommandContext Context(string name, string binaryPath, string dataDirectory, Func<ServiceAccount, IPrincipalResolver> principals)
    {
        string installDirectory = Path.GetDirectoryName(binaryPath) ?? binaryPath;
        var admin = new AclRule(LayoutPrincipal.Administrators, AccessRights.Full);
        var system = new AclRule(LayoutPrincipal.System, AccessRights.Full);
        AclRule Svc(AccessRights r) => new(LayoutPrincipal.ServiceAccount, r);

        // Proxy keeps everything it writes (generated certificate and its password file) in one flat data directory.
        var layout = DirectoryLayout.Create(name,
        [
            new LayoutEntry(LayoutRole.InstallRoot, installDirectory, new AclSpec(false, [admin, system, Svc(AccessRights.ReadExecute)]),
                "Binaries; the service can read and run them but not change them."),
            new LayoutEntry(LayoutRole.Data, dataDirectory, new AclSpec(true, [admin, system, Svc(AccessRights.Modify)]),
                "Settings (appsettings.json), the generated TLS certificate and its protected password; Users have no access."),
        ]);

        return new ServiceCommandContext
        {
            ServiceName = name,
            DisplayName = DisplayName,
            Description = Description,
            BinaryPath = binaryPath,
            Version = ProxyHost.Version,
            // the service runs the server: there is no default command
            Arguments = ["server", "run"],
            Layout = layout,
            PrincipalResolver = principals,
            // Same as before: start at boot (not delayed); restart after a crash after 5s, 5s, then 60s; reset after a day.
            StartMode = ServiceStartMode.Automatic,
            Failure = new FailurePolicy([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)], TimeSpan.FromDays(1)),
            DataDirectoryArgument = path => [$"--{ProxyOptions.SectionName}:{nameof(ProxyOptions.DataDirectory)}={path}"],
        };
    }

    private static int Unit(ServiceCliEnvironment env, ExtractedArgs args)
    {
        string name = (args.Name ?? "dbdataproxy").ToLowerInvariant();
        // On Linux the unit runs the executable it is generated from; elsewhere (a unit written on a Windows
        // machine) --exec gives the path on the target, defaulting to the conventional /opt location.
        string exe = args.Exec ?? (env.BinaryPath.StartsWith('/') ? env.BinaryPath : "/opt/dbdataproxy/dbdataproxy");
        try
        {
            env.Console.Out.Write(SystemdUnitGenerator.Generate(UnitOptions(name, exe)));
            return ExitCodes.Ok;
        }
        catch (ArgumentException ex)
        {
            env.Console.Error.WriteLine(ex.Message);
            return ExitCodes.Usage;
        }
    }

    public static SystemdUnitOptions UnitOptions(string name, string executable) => new()
    {
        Name = name,
        Description = "DbDataProxy Arrow Flight SQL proxy",
        ExecStart = executable + " server run",
        WorkingDirectory = executable[..executable.LastIndexOf((char)47)], // a Unix path whatever OS generates it
        // DynamicUser: no account to manage; the state directory under /var/lib/<name> is created and owned by systemd.
        StateDirectories = [name],
        Environment = new Dictionary<string, string> { ["DBPROXY_Proxy__DataDirectory"] = $"/var/lib/{name}" },
    };

    private sealed record ExtractedArgs(string? Verb, string? Name, bool Systemd, string? Exec, string? PasswordOnCommandLine, string? PasswordSecret, List<string> Rest);

    private static ExtractedArgs Extract(string[] args)
    {
        string? verb = null, name = null, password = null, secret = null, exec = null;
        bool systemd = false;
        var rest = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            switch (a)
            {
                case "--systemd": systemd = true; break;
                case "--name": name = Next(args, ref i, a); break;
                case "--exec": exec = Next(args, ref i, a); break;
                case "--password": password = Next(args, ref i, a); break;
                case "--password-secret": secret = Next(args, ref i, a); break;
                default:
                    if (a.StartsWith("--name=", StringComparison.Ordinal)) name = a["--name=".Length..];
                    else if (a.StartsWith("--password=", StringComparison.Ordinal)) password = a["--password=".Length..];
                    else if (a.StartsWith("--password-secret=", StringComparison.Ordinal)) secret = a["--password-secret=".Length..];
                    else if (!a.StartsWith("--", StringComparison.Ordinal) && verb is null) verb = a.ToLowerInvariant();
                    else rest.Add(a);
                    break;
            }
        }

        return new ExtractedArgs(verb, name, systemd, exec, password, secret, rest);
    }

    private static string Next(string[] args, ref int i, string option) =>
        i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Option {option} needs a value.");

    private static string? ReadOption(List<string> args, string name)
    {
        int i = args.IndexOf(name);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    private static void ReplaceOption(List<string> args, string name, string value) => args[args.IndexOf(name) + 1] = value;

    private sealed record ResolvedPassword(string? Password, string? Error);

    private static ResolvedPassword ResolvePassword(ExtractedArgs extracted, List<string> forwarded, ServiceCliEnvironment env)
    {
        if (extracted.PasswordOnCommandLine is not null && extracted.PasswordSecret is not null)
            return new(null, "Give either --password-secret or --password, not both.");

        if (extracted.PasswordOnCommandLine is { } given)
        {
            env.Console.Error.WriteLine(
                "Warning: --password puts the password on the command line, where other users, process listings and shell history can see it. " +
                "Use --password-secret env:NAME or file:PATH, or omit it and type the password when prompted.");
            return new(given, null);
        }

        if (extracted.PasswordSecret is { } reference)
        {
            var (value, error) = ReadSecret(reference, env);
            return new(value, error);
        }

        // An explicit account needs a password; ask for it instead of failing, unless stdin carries it already.
        var account = ReadOption(forwarded, "--account");
        bool needsPassword = account is not null
            && ServiceAccount.Parse(account, extracted.Name ?? ProxyHost.RegistrationName).RequiresPassword
            && !forwarded.Contains("--password-stdin") && !forwarded.Contains("--dry-run");
        if (needsPassword && env.PromptPassword is { } prompt)
        {
            var typed = prompt($"Password for {account}: ");
            return string.IsNullOrEmpty(typed) ? new(null, "A password is required for that account.") : new(typed, null);
        }

        return new(null, null);
    }

    /// <summary>Secret references understood here: <c>env:NAME</c> and <c>file:PATH</c> (first line).</summary>
    public static (string? Value, string? Error) ReadSecret(string reference, ServiceCliEnvironment env)
    {
        int colon = reference.IndexOf(':');
        // A drive letter is not a scheme ("C:\secrets\pw.txt"): only env: and file: are schemes.
        string scheme = colon > 1 ? reference[..colon].ToLowerInvariant() : "env";
        string target = colon > 1 ? reference[(colon + 1)..] : reference;
        switch (scheme)
        {
            case "env":
                var value = env.GetEnvironmentVariable(target);
                return string.IsNullOrEmpty(value) ? (null, $"The environment variable '{target}' named by --password-secret is empty or not set.") : (value, null);
            case "file":
                try
                {
                    var line = File.ReadLines(target).FirstOrDefault();
                    return string.IsNullOrEmpty(line) ? (null, $"The file '{target}' named by --password-secret is empty.") : (line, null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return (null, $"Cannot read the file named by --password-secret: {ex.Message}");
                }
            default:
                return (null, $"Unknown --password-secret scheme '{scheme}'. Use env:NAME or file:PATH.");
        }
    }

    /// <summary>Console whose standard input is the resolved password and whose output is the real console.</summary>
    private sealed class PasswordConsole(IConsole inner, string password) : IConsole
    {
        public TextWriter Out => inner.Out;

        public TextWriter Error => inner.Error;

        public string? ReadLine() => password;
    }
}

/// <summary>Reads the proxy's own settings the way the host does, for commands that run without starting it.</summary>
public static class ProxyConfiguration
{
    public static ProxyOptions LoadOptions(string? baseDirectory = null, string? dataDirectory = null)
    {
        // The same layers as the host: the file beside the executable, then the data directory's, then the environment.
        var configuration = new ConfigurationBuilder()
            .SetBasePath(baseDirectory ?? AppContext.BaseDirectory)
            .AddJsonFile(ProxyConfigFile.FileName, optional: true)
            .AddJsonFile(ProxyConfigFile.PathIn(dataDirectory ?? ProxyConfigFile.ResolveDataDirectory()), optional: true)
            .AddEnvironmentVariables("DBPROXY_")
            .Build();
        return configuration.GetSection(ProxyOptions.SectionName).Get<ProxyOptions>() ?? new ProxyOptions();
    }
}
