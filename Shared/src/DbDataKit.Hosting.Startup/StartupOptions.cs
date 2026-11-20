namespace DbDataKit.Hosting.Startup;

/// <summary>What a server tells its startup sequence about itself.</summary>
public sealed class StartupOptions
{
    /// <summary>The service that starts.</summary>
    public required SuiteService Service { get; init; }

    /// <summary>The service's data folder, named in a failure; null when the server has not resolved one.</summary>
    public string? DataFolder { get; init; }

    /// <summary>The shared logs folder that holds the failure file. An install's by default.</summary>
    public string LogsRoot { get; init; } = SuiteServices.MachineLogsRoot;

    /// <summary>More folders a failure names, as label and path ("Configuration repository", ...).</summary>
    public IReadOnlyList<StartupFact> Folders { get; init; } = [];

    /// <summary>The program's version, named in a failure.</summary>
    public string? Version { get; init; }

    public TimeProvider Time { get; init; } = TimeProvider.System;

    /// <summary>
    /// Where a reason goes when the failure file cannot hold it (its folder cannot be made, or another account owns it). By default standard error and,
    /// for a Windows service, the Application event log.
    /// </summary>
    public Action<string> Unreportable { get; init; } = StartupReporting.Default;

    /// <summary>
    /// What else hears of a failed start once the failure file holds its reason. By default the Application event log, for a Windows service only, so
    /// the service manager's "Cannot start service" has its reason next to it.
    /// </summary>
    public Action<string> Failed { get; init; } = StartupReporting.EventLog;

    /// <summary>Windows' excluded port ranges, named when a listener could not be bound.</summary>
    public Func<IReadOnlyList<PortRange>> ExcludedPorts { get; init; } = ExcludedPortRanges.Read;
}

/// <summary>What a step sees of the start.</summary>
public sealed class StartupContext
{
    internal StartupContext(StartupOptions options, string logFolder)
    {
        Options = options;
        LogFolder = logFolder;
    }

    public StartupOptions Options { get; }

    public SuiteService Service => Options.Service;

    public string? DataFolder => Options.DataFolder;

    /// <summary>The service's own logs subfolder, made before the first step.</summary>
    public string LogFolder { get; }
}

/// <summary>How a start ended.</summary>
/// <param name="FailedStep">The step that failed, or null after a start that succeeded.</param>
/// <param name="Error">What the step threw.</param>
/// <param name="Report">The text of the failure file.</param>
/// <param name="FailureFile">The failure file's path.</param>
public sealed record StartupResult(StartupStep? FailedStep, Exception? Error, string? Report, string FailureFile)
{
    public bool Succeeded => FailedStep is null;

    /// <summary>The process's exit code: 0 after a start that succeeded, <see cref="StartupSequence.FailureExitCode"/> after one that failed.</summary>
    public int ExitCode => Succeeded ? 0 : StartupSequence.FailureExitCode;

    /// <summary>True when the failure file holds the reason of the failed start; false when it could not be written (the reason went to standard error).</summary>
    public bool FailureFileWritten { get; init; }

    /// <summary>
    /// What the server says on standard error after a failed start, for a person at a terminal: the step, what failed, and where the full reason is.
    /// "The start failed at the ports step: Port 7841 cannot be bound. The full reason is in C:\ProgramData\DbDataKit\logs\startup-failure-dbdatarunner.txt."
    /// </summary>
    public string Summary()
    {
        if (Succeeded) return "";
        var reason = Error is null ? null : StartupSequence.Find<StartupStepException>(Error)?.Message ?? Error.Message;
        return $"The start failed at the {FailedStep!.Value.Name()} step: {reason}"
               + (FailureFileWritten ? $" The full reason is in {FailureFile}." : "");
    }
}

/// <summary>Where a start's reason goes when the failure file cannot hold it.</summary>
public static class StartupReporting
{
    /// <summary>Standard error and, for a process without a desktop (a Windows service), the Application event log.</summary>
    public static void Default(string text)
    {
        try { Console.Error.WriteLine(text); }
        catch (IOException) { }
        EventLog(text);
    }

    /// <summary>The Application event log, for a process without a desktop (a Windows service); nothing elsewhere.</summary>
    public static void EventLog(string text)
    {
        if (!OperatingSystem.IsWindows() || Environment.UserInteractive) return;
        try
        {
            // ".NET Runtime" is a source every Windows machine has, so a service that was never registered as a source can still write.
            System.Diagnostics.EventLog.WriteEntry(".NET Runtime", text.Length > 30000 ? text[..30000] : text, System.Diagnostics.EventLogEntryType.Error, 1000);
        }
        catch (SystemException) { }
    }
}
