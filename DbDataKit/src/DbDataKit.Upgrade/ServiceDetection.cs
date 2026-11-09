using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DbDataKit.Upgrade;

/// <summary>
/// Whether this process is the main process of a service (a systemd unit, a Windows service), which is what lets the console upgrade it (plan 24a UC09). A process started by hand from a shell that itself lives under a unit
/// (a CI runner agent, an SSH session of a unit) inherits that unit's environment (<c>INVOCATION_ID</c>) and, on Windows, a non-interactive window station: neither says that <em>this</em> process is the service, so neither is used alone.
/// </summary>
public static class ServiceDetection
{
    /// <summary>
    /// systemd names the process it executed in <c>SYSTEMD_EXEC_PID</c> (systemd 248 and later): this process is the unit's when that is its own id. An older systemd sets none, and then the process is the unit's when it has an
    /// invocation id, lives in a <c>.service</c> control group and its parent is systemd itself (process 1). A child of anything else is a program started by hand.
    /// </summary>
    public static bool IsSystemdMainProcess(string? invocationId, string? execPid, int processId, int parentId, string? cgroup)
    {
        if (string.IsNullOrEmpty(invocationId)) return false;
        if (!string.IsNullOrEmpty(execPid)) return int.TryParse(execPid, out var exec) && exec == processId;
        return parentId == 1 && cgroup is not null && cgroup.Contains(".service", StringComparison.Ordinal);
    }

    /// <summary>The check for this process: systemd on Linux, the service control manager's process as parent on Windows, never on other systems.</summary>
    public static bool IsRunningAsService()
    {
        if (OperatingSystem.IsLinux())
        {
            string? cgroup = null; var parent = -1;
            try { cgroup = File.ReadAllText("/proc/self/cgroup"); } catch (IOException) { }
            try { parent = ParentOnLinux(File.ReadAllText("/proc/self/stat")); } catch (IOException) { }
            return IsSystemdMainProcess(Environment.GetEnvironmentVariable("INVOCATION_ID"), Environment.GetEnvironmentVariable("SYSTEMD_EXEC_PID"), Environment.ProcessId, parent, cgroup);
        }

        return OperatingSystem.IsWindows() && !Environment.UserInteractive && ParentIsServiceManager();
    }

    /// <summary>The parent id from <c>/proc/self/stat</c>: the field after the command name, which is in parentheses and may itself hold spaces.</summary>
    public static int ParentOnLinux(string stat)
    {
        var close = stat.LastIndexOf(')');
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 1 && int.TryParse(fields[1], out var ppid) ? ppid : -1;
    }

    private static bool ParentIsServiceManager()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var info = new ProcessBasicInformation();
            if (NtQueryInformationProcess(self.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return false;
            using var parent = Process.GetProcessById((int)info.InheritedFromUniqueProcessId);
            return parent.ProcessName.Equals("services", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr handle, int infoClass, ref ProcessBasicInformation info, int size, out int returned);
}
