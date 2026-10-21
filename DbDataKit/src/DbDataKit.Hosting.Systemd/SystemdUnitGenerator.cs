using System.Text;

namespace DbDataKit.Hosting.Systemd;

/// <summary>The sandboxing directives a generated unit can carry. <see cref="All"/> is the default.</summary>
[Flags]
public enum SystemdHardening
{
    None = 0,
    NoNewPrivileges = 1,
    ProtectSystem = 1 << 1,
    ProtectHome = 1 << 2,
    PrivateTmp = 1 << 3,
    PrivateDevices = 1 << 4,
    ProtectKernel = 1 << 5,
    ProtectControlGroups = 1 << 6,
    RestrictSuidSgid = 1 << 7,
    RestrictNamespaces = 1 << 8,
    LockPersonality = 1 << 9,
    SystemCallArchitectures = 1 << 10,
    UMask = 1 << 11,
    DropCapabilities = 1 << 12,
    All = (1 << 13) - 1,
}

public sealed class SystemdUnitOptions
{
    /// <summary>Unit/service name without ".service"; also the default state/config directory name.</summary>
    public required string Name { get; init; }

    public string Description { get; init; } = "";

    /// <summary>Absolute path to the executable, followed by its arguments.</summary>
    public required string ExecStart { get; init; }

    public string? WorkingDirectory { get; init; }

    /// <summary>Default true: systemd allocates a transient user, no account to manage.</summary>
    public bool DynamicUser { get; init; } = true;

    /// <summary>Dedicated user (used when <see cref="DynamicUser"/> is false).</summary>
    public string? User { get; init; }

    public string? Group { get; init; }

    /// <summary>Relative directory names under /var/lib, /etc, /var/log, /var/cache (created and owned by systemd).</summary>
    public IReadOnlyList<string> StateDirectories { get; init; } = [];

    public IReadOnlyList<string> ConfigurationDirectories { get; init; } = [];

    public IReadOnlyList<string> LogsDirectories { get; init; } = [];

    /// <summary>Extra absolute paths the service may write (ProtectSystem=strict makes the rest read-only).</summary>
    public IReadOnlyList<string> ReadWritePaths { get; init; } = [];

    /// <summary>Secrets delivered as credentials: id to absolute source path. The service reads $CREDENTIALS_DIRECTORY/id.</summary>
    public IReadOnlyDictionary<string, string> LoadCredentials { get; init; } = new Dictionary<string, string>();

    /// <summary>Non-secret environment variables.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Type=notify matches <c>AddSystemd()</c> (READY=1). Use "simple" for hosts without it.</summary>
    public string Type { get; init; } = "notify";

    public string Restart { get; init; } = "on-failure";

    public TimeSpan RestartSec { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Only set when the host actually pings the watchdog (WATCHDOG=1); otherwise systemd kills a healthy service.</summary>
    public TimeSpan? WatchdogSec { get; init; }

    public TimeSpan TimeoutStopSec { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>Capabilities to keep (default none). E.g. CAP_NET_BIND_SERVICE for port 443.</summary>
    public IReadOnlyList<string> Capabilities { get; init; } = [];

    public IReadOnlyList<string> After { get; init; } = ["network-online.target"];

    public IReadOnlyList<string> Wants { get; init; } = ["network-online.target"];

    public string WantedBy { get; init; } = "multi-user.target";

    /// <summary>Which sandboxing directives to emit; everything by default. A product with a reason (an existing deployment whose files must stay readable to other users, say) asks for less.</summary>
    public SystemdHardening Hardening { get; init; } = SystemdHardening.All;
}

/// <summary>
/// Emits a hardened unit file (extends Sync's SystemdService.cs). Deterministic LF output so it can be golden-tested.
/// Secrets never go in <c>Environment=</c>; use <see cref="SystemdUnitOptions.LoadCredentials"/>.
/// </summary>
public static class SystemdUnitGenerator
{
    public static string Generate(SystemdUnitOptions o)
    {
        Validate(o);
        var sb = new StringBuilder();

        void Line(string text) => sb.Append(text).Append('\n');
        void Kv(string key, string value) => Line($"{key}={value}");
        void Opt(string key, IReadOnlyList<string> values)
        {
            if (values.Count > 0) Kv(key, string.Join(' ', values));
        }

        Line("[Unit]");
        Kv("Description", o.Description.Length > 0 ? o.Description : o.Name);
        Opt("After", o.After);
        Opt("Wants", o.Wants);
        Line("");

        Line("[Service]");
        Kv("Type", o.Type);
        Kv("ExecStart", o.ExecStart);
        if (o.WorkingDirectory is not null) Kv("WorkingDirectory", o.WorkingDirectory);
        Kv("Restart", o.Restart);
        Kv("RestartSec", Seconds(o.RestartSec));
        Kv("TimeoutStopSec", Seconds(o.TimeoutStopSec));
        if (o.WatchdogSec is { } wd) Kv("WatchdogSec", Seconds(wd));
        Line("");

        Line("# Identity");
        if (o.DynamicUser)
        {
            Kv("DynamicUser", "yes");
        }
        else
        {
            Kv("User", o.User!);
            Kv("Group", o.Group ?? o.User!);
        }

        Opt("StateDirectory", o.StateDirectories);
        Opt("ConfigurationDirectory", o.ConfigurationDirectories);
        Opt("LogsDirectory", o.LogsDirectories);
        foreach (var (id, path) in o.LoadCredentials.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Kv("LoadCredential", $"{id}:{path}");
        }

        foreach (var (key, value) in o.Environment.OrderBy(k => k.Key, StringComparer.Ordinal))
        {
            Kv("Environment", $"\"{key}={value.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
        }

        Line("");
        var h = o.Hardening;
        if (h != SystemdHardening.None || o.ReadWritePaths.Count > 0)
        {
            Line("# Hardening");
            void Flag(SystemdHardening flag, string key, string value)
            {
                if (h.HasFlag(flag)) Kv(key, value);
            }

            Flag(SystemdHardening.NoNewPrivileges, "NoNewPrivileges", "yes");
            Flag(SystemdHardening.ProtectSystem, "ProtectSystem", "strict");
            Flag(SystemdHardening.ProtectHome, "ProtectHome", "yes");
            Flag(SystemdHardening.PrivateTmp, "PrivateTmp", "yes");
            Flag(SystemdHardening.PrivateDevices, "PrivateDevices", "yes");
            Flag(SystemdHardening.ProtectKernel, "ProtectKernelTunables", "yes");
            Flag(SystemdHardening.ProtectKernel, "ProtectKernelModules", "yes");
            Flag(SystemdHardening.ProtectControlGroups, "ProtectControlGroups", "yes");
            Flag(SystemdHardening.RestrictSuidSgid, "RestrictSUIDSGID", "yes");
            Flag(SystemdHardening.RestrictNamespaces, "RestrictNamespaces", "yes");
            Flag(SystemdHardening.LockPersonality, "LockPersonality", "yes");
            Flag(SystemdHardening.SystemCallArchitectures, "SystemCallArchitectures", "native");
            Flag(SystemdHardening.UMask, "UMask", "0077");
            if (h.HasFlag(SystemdHardening.DropCapabilities))
            {
                Kv("CapabilityBoundingSet", string.Join(' ', o.Capabilities));
                Kv("AmbientCapabilities", string.Join(' ', o.Capabilities));
            }

            Opt("ReadWritePaths", o.ReadWritePaths);
            Line("");
        }

        Line("[Install]");
        Kv("WantedBy", o.WantedBy);
        return sb.ToString();
    }

    private static string Seconds(TimeSpan t) => ((long)Math.Ceiling(t.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static void Validate(SystemdUnitOptions o)
    {
        Check(o.Name, nameof(o.Name));
        Check(o.Description, nameof(o.Description));
        Check(o.ExecStart, nameof(o.ExecStart));
        // The executable may be quoted (a path with spaces); the path itself must be absolute.
        if (!o.ExecStart.TrimStart('"').StartsWith('/')) throw new ArgumentException("ExecStart must start with an absolute path.");
        if (!o.DynamicUser && string.IsNullOrWhiteSpace(o.User)) throw new ArgumentException("A dedicated user is required when DynamicUser is false.");
        if (o.User is not null) Check(o.User, nameof(o.User));
        foreach (var p in o.ReadWritePaths.Concat(o.LoadCredentials.Values))
        {
            Check(p, "path");
            if (!p.StartsWith('/')) throw new ArgumentException($"'{p}' must be an absolute path.");
        }

        foreach (var d in o.StateDirectories.Concat(o.ConfigurationDirectories).Concat(o.LogsDirectories))
        {
            Check(d, "directory");
            if (d.StartsWith('/') || d.Contains("..")) throw new ArgumentException($"'{d}' must be a relative directory name.");
        }

        foreach (var (k, v) in o.Environment)
        {
            Check(k, "environment name");
            Check(v, "environment value");
            if (k.Contains('=') || k.Contains(' ')) throw new ArgumentException($"Invalid environment name '{k}'.");
        }

        foreach (var id in o.LoadCredentials.Keys)
        {
            Check(id, "credential id");
            if (id.Contains(':') || id.Contains('/')) throw new ArgumentException($"Invalid credential id '{id}'.");
        }

        if (o.WatchdogSec is { } w && w <= TimeSpan.Zero) throw new ArgumentException("WatchdogSec must be positive.");
    }

    /// <summary>Unit files are line-based; a newline in any value would inject directives.</summary>
    private static void Check(string value, string what)
    {
        if (value.Contains('\n') || value.Contains('\r') || value.Contains('\0'))
        {
            throw new ArgumentException($"{what} must not contain line breaks.");
        }
    }
}
