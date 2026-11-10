using System.Runtime.InteropServices;

namespace DbDataKit.Runner;

/// <summary>
/// Hard links, the one way the artifact cache's views refer to its objects (plan 28a R-links). A hard link is another name of the same file, so a program started from a view sees an
/// ordinary file at its own path: the .NET host that resolves a symbolic link to the object and looks for <c>*.runtimeconfig.json</c> beside <i>that</i> (exit code 131, plan 28c E12e)
/// finds the neighbours in the view. It needs no privilege (<c>CreateHardLinkW</c> on Windows, where a symbolic link needs <c>SeCreateSymbolicLinkPrivilege</c>, which <c>NT SERVICE\DbDataRunner</c>
/// lacks; <c>link(2)</c> elsewhere) and costs no disk. It is not always possible: another volume, a file system without links (FAT, exFAT, some network shares) and the limit on the links to
/// one file (NTFS 1023, ext4 65000) all refuse, which is why callers fall back to a copy.
/// </summary>
public static class FileLinks
{
    // a path longer than this needs the \\?\ form for the native call (the .NET file APIs add it themselves, a P/Invoke does not get it)
    private const int LongPathStart = 240;

    /// <summary>Makes <paramref name="link"/> another name of the file <paramref name="existing"/>. False, with nothing created, when the system refuses for any reason (the caller copies).</summary>
    public static bool TryCreateHardLink(string link, string existing)
    {
        ArgumentException.ThrowIfNullOrEmpty(link);
        ArgumentException.ThrowIfNullOrEmpty(existing);
        try
        {
            if (OperatingSystem.IsWindows()) return CreateHardLinkW(WindowsPath(link), WindowsPath(existing), IntPtr.Zero);
            return HardLinkLibc(existing, link) == 0;
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

    /// <summary>
    /// Deletes a file of a view on Windows without clearing its read-only attribute. The attribute belongs to the file, not to the name, so clearing it on a hard link would make the cached object writable
    /// for the next view; the delete asks the system to ignore the attribute instead (Windows 10 1709 and later, NTFS). False when that is not available: the caller clears the attribute and deletes.
    /// </summary>
    public static bool TryDeleteIgnoringReadOnly(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            const uint Delete = 0x00010000;
            const uint ShareAll = 0x7;
            const uint OpenExisting = 3;
            const uint OpenReparsePoint = 0x00200000;
            const int FileDispositionInfoEx = 21;
            const uint DeleteFlag = 0x1, PosixSemantics = 0x2, IgnoreReadOnly = 0x10;

            using var handle = CreateFileW(WindowsPath(path), Delete, ShareAll, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid) return false;
            var flags = DeleteFlag | PosixSemantics | IgnoreReadOnly;
            return SetFileInformationByHandle(handle, FileDispositionInfoEx, ref flags, sizeof(uint));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or MarshalDirectiveException)
        {
            return false;
        }
    }

    internal static string WindowsPath(string path)
    {
        if (path.Length < LongPathStart || path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(string fileName, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int informationClass, ref uint information, uint size);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int HardLinkLibc([MarshalAs(UnmanagedType.LPUTF8Str)] string existing, [MarshalAs(UnmanagedType.LPUTF8Str)] string link);
}
