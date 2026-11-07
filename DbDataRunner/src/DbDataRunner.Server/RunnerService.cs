using DbDataKit.Cli;
using DbDataKit.Hosting.Certificates;
using DbDataKit.Hosting.Cli;
using DbDataKit.Hosting.Systemd;
using DbDataKit.Hosting.WindowsService;
using DbDataKit.Security.Acl;

namespace DbDataRunner.Server;

/// <summary>What the service verbs act on. Tests substitute fakes; <see cref="Real"/> is the machine.</summary>
public sealed class RunnerServiceEnvironment
{
    public required IServiceControlManager Scm { get; init; }
    public required IAclService Acl { get; init; }
    public required IElevationChecker Elevation { get; init; }

    /// <summary>The executable the service will run: the one the command was started from.</summary>
    public required string BinaryPath { get; init; }

    /// <summary>The server's directory when <c>--data-dir</c> is not given.</summary>
    public required string DefaultDataDirectory { get; init; }

    /// <summary>Maps the service account to the SIDs the directory ACLs are written for.</summary>
    public Func<ServiceAccount, IPrincipalResolver> PrincipalResolver { get; init; } = account => new WindowsPrincipalResolver(account);

    public static RunnerServiceEnvironment Real() => new()
    {
        Scm = OperatingSystem.IsWindows() ? new WindowsServiceControlManager() : new InMemoryServiceControlManager(),
        Acl = new FileSystemAclService(),
        Elevation = new ElevationDetector(),
        BinaryPath = Environment.ProcessPath ?? "",
        DefaultDataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "DbDataRunner", "server"),
    };
}

/// <summary>
/// <c>dbdatarunner service install|uninstall|status|start|stop|restart|doctor|unit</c>: registration of <c>server run</c> with Windows or systemd, the shared verbs over the shared command. The service runs as the virtual account
/// <c>NT SERVICE\DbDataRunner</c> unless <c>--account</c> says otherwise (the low-privilege account of plan 28 section 5: the tasks it starts run as it), and keeps everything it writes in one data directory, which only
/// administrators and the service can reach. <c>cert show|ensure|check|renew</c> is the server's TLS certificate, in that directory.
/// </summary>
public static class RunnerService
{
    public const string RegistrationName = "DbDataRunner";
    public const string DisplayName = "DbDataRunner server";
    public const string Description = "Schedules and runs the tasks of published packages, each in a process of its own, with retries, limits, run history and progress, over a management API a console pairs with.";

    public static Noun Noun(RunnerServiceEnvironment env) =>
        HostingNouns.Service(new ServiceCommand(Context(env), env.Scm, env.Acl, env.Elevation, null, true));

    public static ServiceCommandContext Context(RunnerServiceEnvironment env)
    {
        var installDirectory = Path.GetDirectoryName(env.BinaryPath) ?? env.BinaryPath;
        var admin = new AclRule(LayoutPrincipal.Administrators, AccessRights.Full);
        var system = new AclRule(LayoutPrincipal.System, AccessRights.Full);
        AclRule Svc(AccessRights r) => new(LayoutPrincipal.ServiceAccount, r);

        var layout = DirectoryLayout.Create(RegistrationName,
        [
            new LayoutEntry(LayoutRole.InstallRoot, installDirectory, new AclSpec(false, [admin, system, Svc(AccessRights.ReadExecute)]),
                "Binaries and the task host; the service can read and run them but not change them."),
            new LayoutEntry(LayoutRole.Data, env.DefaultDataDirectory, new AclSpec(true, [admin, system, Svc(AccessRights.Modify)]),
                "The server's state, certificate, package cache and scratch space; Users have no access."),
        ]);

        return new ServiceCommandContext
        {
            ServiceName = RegistrationName,
            DisplayName = DisplayName,
            Description = Description,
            BinaryPath = env.BinaryPath,
            Version = RunnerServer.Version,
            // the service runs the server, with its directory named so it does not depend on the account's profile
            Arguments = ["server", "run", "--repo", env.DefaultDataDirectory],
            Layout = layout,
            PrincipalResolver = env.PrincipalResolver,
            SystemdUnit = () => UnitOptions("dbdatarunner", env.BinaryPath.StartsWith('/') ? env.BinaryPath : "/opt/dbdatarunner/dbdatarunner"),
            StartMode = ServiceStartMode.Automatic,
            Failure = new FailurePolicy([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(60)], TimeSpan.FromDays(1)),
            DataDirectoryArgument = path => ["--repo", path],
        };
    }

    public static SystemdUnitOptions UnitOptions(string name, string executable) => new()
    {
        Name = name,
        Description = "DbDataRunner server",
        ExecStart = $"{executable} server run --repo /var/lib/{name}",
        WorkingDirectory = executable[..executable.LastIndexOf('/')],
        // DynamicUser: no account to manage; the state directory under /var/lib/<name> is created and owned by systemd.
        StateDirectories = [name],
    };

    // ---- cert

    /// <summary><c>cert show|ensure|check|renew</c> on the certificate in the server's directory (<c>tls.pfx</c>, self-signed), the shared verbs with <c>--repo</c> to name the directory.</summary>
    public static Noun CertNoun(Func<string, string?>? environment = null) => new("cert", "The server's TLS certificate",
    [
        Cert("show", "The certificate in use; never creates or changes anything", EffectClass.OfflineOnly, environment),
        Cert("ensure", "Create or rotate the managed certificate if needed", EffectClass.RepoFilesOnly, environment),
        Cert("check", "Exit 1 when the certificate is missing or due for rotation", EffectClass.OfflineOnly, environment),
        Cert("renew", "Replace the managed self-signed certificate now", EffectClass.RepoFilesOnly, environment),
    ]);

    private static Verb Cert(string name, string summary, EffectClass effect, Func<string, string?>? environment) =>
        new(name, summary, effect, "[--repo <server directory>]", async (args, context) =>
        {
            var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
            if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
            {
                await context.Error.WriteLineAsync($"Usage: cert {name} [--repo <server directory>]");
                return CliExit.Usage;
            }

            var directory = Path.GetFullPath(parsed.Get("repo") ?? RunnerSettings.DefaultDirectory(environment));
            var verb = HostingNouns.Cert(Command(directory)).Verbs.Single(v => v.Name == name);
            return await verb.Run([], context);
        }, DataSchemas.None);

    /// <summary>The shared certificate command over the server's own certificate: the same file, password file and options <see cref="DirectoryCertificate.Load"/> uses.</summary>
    public static CertCommand Command(string directory)
    {
        var passwordFile = Path.Combine(directory, DirectoryCertificate.PasswordFileName);
        void Restrict(string path)
        {
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var secrets = new DelegateSecretSource((_, generate) =>
        {
            if (File.Exists(passwordFile)) return File.ReadAllText(passwordFile).Trim();
            Directory.CreateDirectory(directory);
            var made = generate();
            File.WriteAllText(passwordFile, made);
            Restrict(passwordFile);
            return made;
        });
        var options = new CertificateOptions { Mode = CertificateMode.SelfSigned, Path = Path.Combine(directory, DirectoryCertificate.PfxFileName) };
        return new CertCommand(options, secrets, new CertificateProvider(secrets, restrictFileAccess: Restrict));
    }
}
