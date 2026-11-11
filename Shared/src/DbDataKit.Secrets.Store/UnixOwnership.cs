using System.Diagnostics;

namespace DbDataKit.Secrets.Store;

/// <summary>
/// Whether a file is owned by the user this process runs as. .NET has no call for a file's owner, so the system's own <c>stat</c> and <c>id</c> are asked (once per key file); where they are
/// not there (a minimal container) the answer is "unknown" and the mode check alone stands.
/// </summary>
public static class UnixOwnership
{
    /// <summary>True: the process's effective user owns the path. False: someone else does. Null: it could not be determined.</summary>
    public static bool? IsOwnedByCurrentUser(string path)
    {
        if (OperatingSystem.IsWindows()) return null;
        var me = Run("id", "-u");
        var owner = OperatingSystem.IsMacOS() ? Run("stat", "-f", "%u", path) : Run("stat", "-c", "%u", path);
        return me is null || owner is null ? null : me == owner;
    }

    private static string? Run(string tool, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd().Trim();
            process.StandardError.ReadToEnd();
            if (!process.WaitForExit(5000)) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
            return process.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException) { return null; }
    }
}
