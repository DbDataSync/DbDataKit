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

    /// <summary>Where a warning of the start goes, such as a skipped folder permissions check: standard error by default.</summary>
    public Action<string> Warning { get; init; } = StartupReporting.Warning;

    /// <summary>Whether the process runs under a service manager; by default true for a Windows service (a process without a desktop).</summary>
    public Func<bool> RunsAsService { get; init; } = StartupReporting.RunsAsService;

    /// <summary>
    /// What else hears a warning when the process runs under a service manager (<see cref="RunsAsService"/>): by default the Application event log, as a
    /// warning, so a service that has no console still says it. On Linux standard error is the service's journal.
    /// </summary>
    public Action<string> ServiceWarning { get; init; } = StartupReporting.EventLogWarning;

    /// <summary>The folder step's check of the data folder: what is wrong with a folder, one sentence each (<see cref="FolderPermissions.Problems"/>).</summary>
    public Func<string, IReadOnlyList<string>> FolderProblems { get; init; } = FolderPermissions.Problems;
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

    /// <summary>
    /// The folder step's check of <paramref name="folder"/>, the server's data folder (<see cref="FolderPermissions"/>). With the check on, a folder whose
    /// owner or access list lets another account change it fails the step, naming the switch that skips the check. With it skipped, the start goes on and
    /// says so: a warning on standard error at every start (and, for a service, in the event log) naming what asked for it and what the check would find.
    /// <code>
    /// .Step(StartupStep.Folder, step => step.CheckFolderPermissions(directory, SkipFolderPermissions.Read(flagGiven, settingsFile), settingsFile))
    /// </code>
    /// Design: docs/install.md#skipping-the-folder-permissions-check
    /// </summary>
    /// <param name="folder">The data folder; a folder that is not there passes (the server makes it as its own account).</param>
    /// <param name="skip">Whether the check is skipped, and by what.</param>
    /// <param name="settingsFile">The server's settings file, named in the failure as the place of the setting.</param>
    public void CheckFolderPermissions(string folder, SkipFolderPermissions skip, string settingsFile)
    {
        var problems = Options.FolderProblems(folder);
        if (!skip.IsOn)
        {
            if (problems.Count > 0) throw StartupStepException.FolderPermissions(folder, problems, settingsFile);
            return;
        }

        var warning = $"Warning: {Service.Program} skips the folder permissions check ({skip.Source}): the data folder '{folder}' is used whatever its "
                      + "owner and access list. "
                      + (problems.Count > 0
                          ? $"The check would refuse it: {string.Join(" ", problems)}"
                          : "The folder passes the check, so the switch can be removed.");
        Options.Warning(warning);
        if (Options.RunsAsService()) Options.ServiceWarning(warning);
    }
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

    /// <summary>Standard error.</summary>
    public static void Warning(string text)
    {
        try { Console.Error.WriteLine(text); }
        catch (IOException) { }
    }

    /// <summary>True for a Windows service: a process without a desktop.</summary>
    public static bool RunsAsService() => OperatingSystem.IsWindows() && !Environment.UserInteractive;

    /// <summary>The Application event log, as a warning; nothing on another system.</summary>
    public static void EventLogWarning(string text)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            System.Diagnostics.EventLog.WriteEntry(".NET Runtime", text.Length > 30000 ? text[..30000] : text, System.Diagnostics.EventLogEntryType.Warning, 1000);
        }
        catch (SystemException) { }
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
