using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DbDataKit.Security.Acl;

namespace DbDataKit.Hosting.Startup;

/// <summary>The account this process runs as, as a failure file names it.</summary>
public static class StartupAccount
{
    /// <summary><c>NT SERVICE\DbDataGateway (not elevated)</c> on Windows, the user name elsewhere.</summary>
    public static string Current()
    {
        try { return AclHelper.DescribeCurrentUser(); }
        catch (SystemException) { return Environment.UserName; }
    }
}

/// <summary>Who owns a file or folder, and whether it is this process's account.</summary>
public static class FileOwnership
{
    /// <summary>The owner's account name, or null when it cannot be read.</summary>
    public static string? DescribeOwner(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) return null;
        if (OperatingSystem.IsWindows())
        {
            var sid = WindowsOwner(path);
            if (sid is null) return null;
            try { return sid.Translate(typeof(NTAccount)).Value; }
            catch (IdentityNotMappedException) { return sid.Value; }
        }
        return UnixOwner(path);
    }

    /// <summary>
    /// True when this process's account owns the path. On Windows an elevated administrator also owns what the Administrators group owns, since on
    /// Windows Server that group, not the user, owns what an elevated administrator creates. A path whose owner cannot be read is not owned.
    /// </summary>
    public static bool IsOwnedByCurrentAccount(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var owner = WindowsOwner(path);
            if (owner is null) return false;
            var me = WindowsIdentity.GetCurrent().User;
            if (me is not null && owner.Equals(me)) return true;
            return owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) && AclHelper.IsElevatedAdministrator();
        }
        // Only the owner (or root) may change a file's mode, so setting the mode it already has answers the question without reading the owner.
        try
        {
            File.SetUnixFileMode(path, File.GetUnixFileMode(path));
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    private static SecurityIdentifier? WindowsOwner(string path)
    {
        try
        {
            FileSystemSecurity security = Directory.Exists(path)
                ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner)
                : new FileInfo(path).GetAccessControl(AccessControlSections.Owner);
            return security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or InvalidOperationException or PrivilegeNotHeldException)
        {
            return null;
        }
    }

    // .NET reads a file's mode but not its owner; `stat` (coreutils) prints the owner's name.
    private static string? UnixOwner(string path)
    {
        try
        {
            using var stat = Process.Start(new ProcessStartInfo("stat", ["-c", "%U", path])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            if (stat is null) return null;
            var output = stat.StandardOutput.ReadToEnd().Trim();
            if (!stat.WaitForExit(10_000)) { stat.Kill(); return null; }
            return stat.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
