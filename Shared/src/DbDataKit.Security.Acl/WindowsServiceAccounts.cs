using System.Security.Principal;
using Microsoft.Win32;

namespace DbDataKit.Security.Acl;

/// <summary>
/// The accounts of the Windows services that run on a data directory: a service whose registered command line names the directory (the suite's services are
/// registered as <c>server run --repo &lt;directory&gt;</c>). A secret store's key files name them in their access list, so the service and the elevated
/// command line (the install, <c>instance add</c>, <c>secret set</c>) can both open the files.
/// </summary>
public static class WindowsServiceAccounts
{
    /// <summary>The accounts (as identity strings <see cref="AclHelper.ResolveIdentity"/> reads) of the services registered on <paramref name="dataDirectory"/>; none on another system or when no service runs there.</summary>
    public static IReadOnlyList<string> On(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(dataDirectory)) return [];
        try { return Find(dataDirectory); }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return []; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> Find(string dataDirectory)
    {
        var found = new List<string>();
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return found;
        var directory = Normalize(Path.GetFullPath(dataDirectory));
        foreach (var name in services.GetSubKeyNames())
        {
            using var key = services.OpenSubKey(name);
            if (key?.GetValue("ImagePath") is not string image || key.GetValue("ObjectName") is not string account) continue;
            if (!Names(Normalize(image), directory)) continue;
            // LocalSystem is SYSTEM, already in every list; an account the database cannot resolve is left out rather than failing the open
            if (account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) continue;
            try { AclHelper.ResolveIdentity(account); }
            catch (Exception e) when (e is IdentityNotMappedException or ArgumentException or SystemException) { continue; }
            if (!found.Contains(account, StringComparer.OrdinalIgnoreCase)) found.Add(account);
        }
        return found;
    }

    private static string Normalize(string path) => path.Replace('/', '\\').Trim().ToLowerInvariant();

    /// <summary>True when the command line contains the directory as a whole path (followed by the end, a space, a quote or a separator).</summary>
    internal static bool Names(string commandLine, string directory)
    {
        directory = directory.TrimEnd('\\');
        if (directory.Length == 0) return false;
        for (var at = commandLine.IndexOf(directory, StringComparison.Ordinal); at >= 0; at = commandLine.IndexOf(directory, at + 1, StringComparison.Ordinal))
        {
            var end = at + directory.Length;
            if (end == commandLine.Length || commandLine[end] is ' ' or '"' or '\\') return true;
        }
        return false;
    }
}
