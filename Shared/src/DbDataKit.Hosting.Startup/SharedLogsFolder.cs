using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using DbDataKit.Security.Acl;

namespace DbDataKit.Hosting.Startup;

/// <summary>
/// The logs folder the services share (<c>C:\ProgramData\DbDataKit\logs</c>, <c>/var/log/dbdatakit</c>). Each service makes its own subfolder and its
/// own failure file in it and owns them; none can read another's.
/// <list type="bullet">
/// <item>Windows: Administrators and SYSTEM have full control; each service account may create files and folders in that folder only; CREATOR OWNER has
/// full control, so a service has full control of what it made there and nothing else. Inheritance from the parent is cut.</item>
/// <item>Linux: mode <c>1770</c> (owner and group may create, the sticky bit stops one user removing another's file); the install makes it
/// <c>root:dbdatakit</c>.</item>
/// </list>
/// Design: docs/install.md#logs-and-the-startup-failure-file
/// </summary>
public static class SharedLogsFolder
{
    /// <summary>The Linux mode: <c>1770</c>.</summary>
    public const UnixFileMode LinuxMode =
        UnixFileMode.StickyBit | UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute;

    /// <summary>
    /// The accounts that may create their files there: the four services' and, when it is none of those, this process's (a server started from a
    /// terminal). An account that does not resolve (a service that is not registered on this machine) is left out.
    /// </summary>
    public static IReadOnlyList<string> DefaultWriters() => [.. SuiteServices.All.Select(s => s.WindowsAccount), "CurrentUser"];

    /// <summary>Makes the folder when it is missing and gives it its protection. An existing folder is left as it is.</summary>
    /// <returns>True when the folder was made.</returns>
    public static bool Ensure(string path, IEnumerable<string>? writers = null)
    {
        if (Directory.Exists(path)) return false;
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            Protect(path, writers ?? DefaultWriters());
        }
        else
        {
            Directory.CreateDirectory(path, LinuxMode);
            File.SetUnixFileMode(path, LinuxMode); // the umask may have cleared bits of the mode asked for
        }
        return true;
    }

    /// <summary>Gives an existing folder the protection the install gives it.</summary>
    public static void Protect(string path, IEnumerable<string> writers)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, LinuxMode);
            return;
        }
        ProtectWindows(path, writers);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectWindows(string path, IEnumerable<string> writers)
    {
        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        foreach (var writer in writers)
        {
            SecurityIdentifier sid;
            try { sid = AclHelper.ResolveIdentity(writer); }
            catch (SystemException) { continue; } // IdentityNotMappedException among others: the service is not registered here
            security.AddAccessRule(new FileSystemAccessRule(sid,
                FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories | FileSystemRights.ReadAttributes | FileSystemRights.Synchronize,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        }
        new DirectoryInfo(path).SetAccessControl(security);
    }
}
