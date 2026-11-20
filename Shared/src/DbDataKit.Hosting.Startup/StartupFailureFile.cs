using System.Globalization;
using System.Text;
using DbDataKit.Security.Acl;

namespace DbDataKit.Hosting.Startup;

/// <summary>
/// <c>startup-failure-&lt;program&gt;.txt</c> in the shared logs folder, and the service's own logs subfolder beside it. While the server starts, the
/// file holds one line, rewritten as each step begins ("starting since 2026-11-17T09:12:03Z, step: certificate, not finished"), so a process that is
/// killed says how far it got. A start that succeeds deletes it; a start that fails writes the full reason. Only the service's account, SYSTEM and
/// Administrators can open it (on Linux the file is mode 0600), so one service never reads another's.
/// A file or subfolder another account owns is never written: the reason goes to <see cref="StartupOptions.Unreportable"/> instead.
/// Design: docs/install.md#logs-and-the-startup-failure-file
/// </summary>
public sealed class StartupFailureFile
{
    /// <summary>Who may open the file and the service's logs subfolder on Windows; on Linux only the owner may (0600 and 0700).</summary>
    internal static readonly string[] Readers = ["CurrentUser", "SYSTEM", "Administrators"];

    private readonly Action<string> _unreportable;
    private bool _usable;

    public StartupFailureFile(string logsRoot, SuiteService service, Action<string> unreportable)
    {
        LogsRoot = logsRoot;
        Service = service;
        _unreportable = unreportable;
        FilePath = Path.Combine(logsRoot, service.FailureFileName);
        LogFolder = Path.Combine(logsRoot, service.LogFolder);
    }

    public string LogsRoot { get; }

    public SuiteService Service { get; }

    /// <summary>The failure file: <c>C:\ProgramData\DbDataKit\logs\startup-failure-dbdatagateway.txt</c>.</summary>
    public string FilePath { get; }

    /// <summary>The service's own logs subfolder: <c>C:\ProgramData\DbDataKit\logs\dbdatagateway</c>.</summary>
    public string LogFolder { get; }

    /// <summary>Whether this process's account owns a path; replaced in tests, which cannot make a file another account owns.</summary>
    internal Func<string, bool> IsOwned { get; init; } = FileOwnership.IsOwnedByCurrentAccount;

    /// <summary>True after <see cref="Prepare"/> found the file can be written.</summary>
    public bool IsUsable => _usable;

    /// <summary>The line the file holds while a step runs.</summary>
    public static string ProgressLine(DateTimeOffset since, StartupStep step) =>
        $"starting since {since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)}, step: {step.Name()}, not finished";

    /// <summary>
    /// Makes the shared logs folder when it is missing, then the service's own subfolder, and checks that the subfolder and an earlier failure file are
    /// this account's. Returns false, after saying why through <see cref="StartupOptions.Unreportable"/>, when the file cannot be written.
    /// </summary>
    public bool Prepare()
    {
        _usable = false;
        try
        {
            SharedLogsFolder.Ensure(LogsRoot);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _unreportable($"{Service.Program}: the logs folder '{LogsRoot}' does not exist and {StartupAccount.Current()} cannot make it ({e.Message}), so no startup-failure file is written.");
            return false;
        }
        if (Directory.Exists(LogFolder) && !IsOwned(LogFolder))
        {
            _unreportable(NotOwned(LogFolder));
            return false;
        }
        if (File.Exists(FilePath) && !IsOwned(FilePath))
        {
            _unreportable(NotOwned(FilePath));
            return false;
        }
        try
        {
            AclHelper.EnsureRestrictedDirectory(LogFolder, Readers);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _unreportable($"{Service.Program}: the logs folder '{LogFolder}' cannot be made by {StartupAccount.Current()} ({e.Message}), so no startup-failure file is written.");
            return false;
        }
        _usable = true;
        return true;
    }

    /// <summary>
    /// What a person who checks the service reads about its last start, from the failure file in <paramref name="logsRoot"/>: that there is none (the
    /// last start finished, or the service has not started here), the line of a start that is going on or stopped without saying why, or the reason a
    /// start failed. The file's lines are indented under a heading that names it. Never throws for a file it cannot read: it says so.
    /// </summary>
    public static string DescribeLastStart(string logsRoot, SuiteService service)
    {
        var path = Path.Combine(logsRoot, service.FailureFileName);
        if (!File.Exists(path)) return $"Startup-failure file: none ({path}): the last start finished, or the service has not started on this data folder.";
        string text;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"Startup-failure file: {path} is there, and {StartupAccount.Current()} cannot read it ({e.Message}). Only the service's account, SYSTEM and Administrators can: read it as an administrator.";
        }
        var heading = text.StartsWith("starting since ", StringComparison.Ordinal)
            ? $"Startup-failure file: {path} (a start is going on, or the last one stopped without saying why):"
            : $"Startup-failure file: {path} (the last start failed):";
        var lines = text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Select(line => line.Length == 0 ? "" : "  " + line);
        return heading + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    /// <summary>Rewrites the file to say the step has begun.</summary>
    public void WriteProgress(DateTimeOffset since, StartupStep step) => Write(ProgressLine(since, step) + Environment.NewLine);

    /// <summary>Writes the full reason of a failed start.</summary>
    public void WriteFailure(string report) => Write(report);

    /// <summary>Deletes the file after a start that succeeded.</summary>
    public void Delete()
    {
        if (!_usable) return;
        try { File.Delete(FilePath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _unreportable($"{Service.Program} started, but '{FilePath}' could not be deleted ({e.Message}); it does not describe this start.");
        }
    }

    private void Write(string text)
    {
        if (!_usable) return;
        try
        {
            AclHelper.WriteAllBytesRestricted(FilePath, Encoding.UTF8.GetBytes(text), Readers);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _unreportable($"{Service.Program}: '{FilePath}' could not be written ({e.Message}). What it would have said:{Environment.NewLine}{text}");
        }
    }

    private string NotOwned(string path) =>
        $"{Service.Program}: '{path}' is owned by {FileOwnership.DescribeOwner(path) ?? "another account"}, and the service runs as {StartupAccount.Current()}, so nothing is written there. " +
        "Remove it as an administrator; the service makes it again at its next start.";
}
