namespace DbDataGateway.Tests.Support;

/// <summary>Removes the folder of a test run, which can hold read-only files (the key ring of a started Gateway), where a plain recursive delete fails on Windows.</summary>
internal static class TempTree
{
    public static void Delete(string path)
    {
        if (!Directory.Exists(path)) return;
        try
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).Append(path))
            {
                if (OperatingSystem.IsWindows()) File.SetAttributes(entry, FileAttributes.Normal);
                else if (Directory.Exists(entry)) File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                else File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // a process of the run still holds a file; the temp folder is left for the machine's cleanup
        }
    }
}
