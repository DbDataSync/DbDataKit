namespace DbData.Hosting;

/// <summary>How the process is running.</summary>
public enum HostMode
{
    Console,
    WindowsService,
    Systemd,
}

/// <summary>Pure decision of the host mode so it can be tested without a real service manager.</summary>
public static class HostModeDetector
{
    public const string ConsoleFlag = "--console";

    /// <summary>
    /// An explicit <c>--console</c> always wins (foreground debugging of an installed service).
    /// Otherwise the environment probes decide; anything else is console.
    /// </summary>
    public static HostMode Detect(IEnumerable<string> args, bool isWindowsService, bool isSystemdService)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Any(a => string.Equals(a, ConsoleFlag, StringComparison.OrdinalIgnoreCase)))
        {
            return HostMode.Console;
        }

        if (isWindowsService)
        {
            return HostMode.WindowsService;
        }

        return isSystemdService ? HostMode.Systemd : HostMode.Console;
    }
}
