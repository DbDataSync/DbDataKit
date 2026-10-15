using System.Security;
using System.Text;

namespace DbDataKit.Hosting.WindowsService;

public enum ServiceAccountKind
{
    /// <summary><c>NT SERVICE\Name</c>: no password, least privilege, own SID. The default.</summary>
    VirtualAccount,

    /// <summary>Group managed service account <c>DOMAIN\name$</c>: no password supplied by us.</summary>
    Gmsa,

    /// <summary>Explicit account; password required and supplied only as a <see cref="SecureString"/>.</summary>
    User,

    LocalService,
    NetworkService,

    /// <summary>Refused unless the spec sets <see cref="ServiceInstallSpec.AllowLocalSystem"/>.</summary>
    LocalSystem,
}

public sealed record ServiceAccount(ServiceAccountKind Kind, string Name)
{
    public static ServiceAccount Virtual(string serviceName) =>
        new(ServiceAccountKind.VirtualAccount, $@"NT SERVICE\{serviceName}");

    public static ServiceAccount Gmsa(string name)
    {
        if (!name.EndsWith('$') || !name.Contains('\\'))
        {
            throw new ArgumentException(@"A gMSA is written DOMAIN\name$.", nameof(name));
        }

        return new(ServiceAccountKind.Gmsa, name);
    }

    public static ServiceAccount User(string name) => new(ServiceAccountKind.User, name);

    public static ServiceAccount LocalSystem { get; } = new(ServiceAccountKind.LocalSystem, "LocalSystem");

    public static ServiceAccount LocalService { get; } = new(ServiceAccountKind.LocalService, @"NT AUTHORITY\LocalService");

    public static ServiceAccount NetworkService { get; } = new(ServiceAccountKind.NetworkService, @"NT AUTHORITY\NetworkService");

    public bool RequiresPassword => Kind == ServiceAccountKind.User;

    /// <summary>Value for lpServiceStartName; null means LocalSystem.</summary>
    public string? ScmName => Kind == ServiceAccountKind.LocalSystem ? null : Name;

    /// <summary>
    /// Parse a user-facing account string. Empty/"virtual" gives the virtual account; names ending in <c>$</c> are
    /// gMSAs; SYSTEM spellings map to LocalSystem (so the AllowLocalSystem gate cannot be bypassed by spelling).
    /// </summary>
    public static ServiceAccount Parse(string? text, string serviceName)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Equals("virtual", StringComparison.OrdinalIgnoreCase))
        {
            return Virtual(serviceName);
        }

        var t = text.Trim();
        var bare = t[(t.LastIndexOf('\\') + 1)..];
        if (bare.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) || bare.Equals("SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            return LocalSystem;
        }

        if (bare.Equals("LocalService", StringComparison.OrdinalIgnoreCase)) return LocalService;
        if (bare.Equals("NetworkService", StringComparison.OrdinalIgnoreCase)) return NetworkService;
        if (t.StartsWith(@"NT SERVICE\", StringComparison.OrdinalIgnoreCase)) return new(ServiceAccountKind.VirtualAccount, t);
        if (t.EndsWith('$')) return Gmsa(t.Contains('\\') ? t : $@".\{t}");
        return User(t);
    }
}

public enum ServiceStartMode
{
    Automatic,
    AutomaticDelayed,
    Manual,
    Disabled,
}

public enum ServiceSidType
{
    None = 0,
    Unrestricted = 1,
    Restricted = 3,
}

public sealed record FailurePolicy(IReadOnlyList<TimeSpan> RestartDelays, TimeSpan ResetPeriod, bool ApplyToNonCrashFailures = true)
{
    /// <summary>Restart 3 times (5s, 15s, 60s), reset the counter after a day.</summary>
    public static FailurePolicy Default { get; } = new([TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)], TimeSpan.FromDays(1));
}

/// <summary>Everything needed to install (or reconcile) one service. No secrets live here.</summary>
public sealed record ServiceInstallSpec
{
    public required string Name { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>Full path of the service executable (typically <c>...\current\Product.exe</c>).</summary>
    public required string BinaryPath { get; init; }

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public ServiceAccount? Account { get; init; }

    public bool AllowLocalSystem { get; init; }

    public ServiceStartMode StartMode { get; init; } = ServiceStartMode.AutomaticDelayed;

    public IReadOnlyList<string> Dependencies { get; init; } = [];

    public FailurePolicy Failure { get; init; } = FailurePolicy.Default;

    public ServiceSidType SidType { get; init; } = ServiceSidType.Unrestricted;

    /// <summary>Trim the token to these privileges (e.g. <c>SeChangeNotifyPrivilege</c>). Null leaves the default set.</summary>
    public IReadOnlyList<string>? RequiredPrivileges { get; init; }

    /// <summary>Event Log source to register (null = none).</summary>
    public string? EventLogSource { get; init; }

    /// <summary>The version being installed; used to refuse silent downgrades.</summary>
    public string? Version { get; init; }

    public ServiceAccount EffectiveAccount => Account ?? ServiceAccount.Virtual(Name);

    public string CommandLine => CommandLineQuoting.Build(BinaryPath, Arguments);
}

public static class CommandLineQuoting
{
    public static string Build(string exe, IEnumerable<string> args)
    {
        var sb = new StringBuilder(Quote(exe, forceQuotes: true));
        foreach (var a in args)
        {
            sb.Append(' ').Append(Quote(a));
        }

        return sb.ToString();
    }

    /// <summary>CommandLineToArgvW-compatible quoting.</summary>
    public static string Quote(string arg, bool forceQuotes = false)
    {
        if (!forceQuotes && arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"']) < 0)
        {
            return arg;
        }

        var sb = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }
}

public enum ServiceRunState
{
    Stopped = 1,
    StartPending = 2,
    StopPending = 3,
    Running = 4,
    ContinuePending = 5,
    PausePending = 6,
    Paused = 7,
}

public sealed record ServiceStatus(ServiceRunState State, int ProcessId = 0, int Win32ExitCode = 0);

/// <summary>What the SCM currently holds for a service.</summary>
public sealed record ServiceConfigSnapshot
{
    public required string Name { get; init; }

    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>Raw ImagePath (the quoted command line).</summary>
    public required string BinaryPathName { get; init; }

    public ServiceStartMode StartMode { get; init; }

    public string? AccountName { get; init; }

    public IReadOnlyList<string> Dependencies { get; init; } = [];

    public int FailureRestartActions { get; init; }

    public ServiceSidType SidType { get; init; } = ServiceSidType.Unrestricted;
}

public enum ServiceControlError
{
    Unknown,
    AccessDenied,
    NotFound,
    AlreadyExists,
    MarkedForDelete,
    AlreadyRunning,
    NotRunning,
    InvalidAccount,
    DependentRunning,
}

/// <summary>Typed SCM failure; carries the Win32 code (never any credential).</summary>
public sealed class ServiceControlException(ServiceControlError error, int win32Code, string operation, string serviceName)
    : Exception($"Service Control Manager: {operation} '{serviceName}' failed ({error}, Win32 {win32Code}).")
{
    public ServiceControlError Error { get; } = error;

    public int Win32Code { get; } = win32Code;

    public static ServiceControlError Map(int code) => code switch
    {
        5 => ServiceControlError.AccessDenied,
        1060 => ServiceControlError.NotFound,
        1073 => ServiceControlError.AlreadyExists,
        1072 => ServiceControlError.MarkedForDelete,
        1056 => ServiceControlError.AlreadyRunning,
        1062 => ServiceControlError.NotRunning,
        1057 or 1069 => ServiceControlError.InvalidAccount,
        1051 => ServiceControlError.DependentRunning,
        _ => ServiceControlError.Unknown,
    };
}

/// <summary>The SCM surface the install logic needs. Real: <see cref="WindowsServiceControlManager"/>; test: <see cref="InMemoryServiceControlManager"/>.</summary>
public interface IServiceControlManager
{
    ServiceConfigSnapshot? GetConfig(string name);

    /// <summary>Create the service and apply description, failure actions, delayed start, SID type, privileges.</summary>
    void Create(ServiceInstallSpec spec, SecureString? password);

    /// <summary>Re-apply the whole spec to an existing service. A null password leaves it unchanged.</summary>
    void Reconfigure(ServiceInstallSpec spec, SecureString? password);

    void Delete(string name);

    void Start(string name);

    void Stop(string name);

    ServiceStatus QueryStatus(string name);
}

public interface IEventLogSourceRegistrar
{
    bool Exists(string source);

    void Register(string source);

    void Unregister(string source);
}
