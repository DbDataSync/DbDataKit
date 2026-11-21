using System.Security.Principal;
using Microsoft.Win32;

namespace DbDataKit.Security.Acl;

/// <summary>
/// The accounts of the Windows services that run on a data directory. A secret store's key files name them in their access list, so the service and the elevated
/// command line (the install, <c>instance add</c>, <c>secret set</c>) can both open the files. A service runs on the directory when its registered command line names it
/// (<c>server run --repo &lt;directory&gt;</c>, as the console, the Runner and the Workspace are registered), or when its command line names no folder and the folder its
/// product resolves from the service's environment is the directory (the Gateway is registered as <c>server run</c> and keeps <c>DBDATAKIT_DATA_GATEWAY</c>, else
/// <c>gateway</c> under <c>DBDATAKIT_DATA</c>, else <c>%ProgramData%\DbDataKit\gateway</c>, the order of <c>SuiteEnvironment</c>).
/// <para>The services are read from the registry one key at a time: a key the caller may not read (security software protects its own) is passed over, never the reason
/// that no account is found.</para>
/// </summary>
public static class WindowsServiceAccounts
{
    /// <summary>The executables of the suite and the folder each keeps under the data root (<c>SuiteEnvironment.Services</c>; the console's is <c>server</c>).</summary>
    private static readonly Dictionary<string, string> ProductFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dbdatakit"] = "server", ["dbdatagateway"] = "gateway", ["dbdatarunner"] = "runner", ["dbdataworkspace"] = "workspace",
        ["dbdatasync"] = "sync", ["dbdatabuild"] = "build", ["dbdatastore"] = "store",
    };

    /// <summary>The accounts (as identity strings <see cref="AclHelper.ResolveIdentity"/> reads) of the services registered on <paramref name="dataDirectory"/>; none on another system or when no service runs there.</summary>
    public static IReadOnlyList<string> On(string dataDirectory)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(dataDirectory)) return [];
        List<string> found;
        try { found = Find(dataDirectory); }
        catch (Exception e) when (Unreadable(e)) { found = []; }
        // the product's own service keeps its account on the files after it is removed (an uninstall keeps the data): its virtual account stays accepted on the folder named for it
        if (OwnServiceOf(dataDirectory) is { } service && !found.Contains(@"NT SERVICE\" + service, StringComparer.OrdinalIgnoreCase)) found.Add(VirtualAccountSid(service));
        return found;
    }

    /// <summary>The suite's services and the folder each keeps under the data root, as <c>SuiteServices</c> (DbDataKit.Hosting.Startup, which builds on this library) spells them.</summary>
    private static readonly Dictionary<string, string> FolderServices = new(StringComparer.OrdinalIgnoreCase)
    {
        ["server"] = "DbDataKit", ["gateway"] = "DbDataGateway", ["runner"] = "DbDataRunner", ["workspace"] = "DbDataWorkspace",
    };

    /// <summary>The name of the suite service whose folder is <paramref name="dataDirectory"/> by its name (<c>...\gateway</c> is the Gateway's), or null.</summary>
    internal static string? OwnServiceOf(string dataDirectory)
    {
        var leaf = dataDirectory.Replace('/', '\\').TrimEnd('\\');
        leaf = leaf[(leaf.LastIndexOf('\\') + 1)..];
        return FolderServices.TryGetValue(leaf, out var service) ? service : null;
    }

    /// <summary>
    /// The SID of the virtual account <c>NT SERVICE\&lt;name&gt;</c>, computed as Windows computes it (S-1-5-80 and the SHA-1 of the upper-case name in UTF-16, as five little-endian numbers),
    /// so it is known whether or not a service of that name is registered now.
    /// </summary>
    internal static string VirtualAccountSid(string serviceName)
    {
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        return "S-1-5-80-" + string.Join("-", Enumerable.Range(0, 5).Select(i => BitConverter.ToUInt32(hash, i * 4)));
    }

    private static bool Unreadable(Exception e) => e is System.Security.SecurityException or UnauthorizedAccessException or IOException;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static List<string> Find(string dataDirectory)
    {
        var found = new List<string>();
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return found;
        var directory = Normalize(Path.GetFullPath(dataDirectory));
        var machine = MachineEnvironment();
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var name in services.GetSubKeyNames())
        {
            string? image, account;
            string[]? own;
            try
            {
                using var key = services.OpenSubKey(name);
                image = key?.GetValue("ImagePath") as string;
                account = key?.GetValue("ObjectName") as string;
                own = key?.GetValue("Environment") as string[];
            }
            catch (Exception e) when (Unreadable(e)) { continue; }   // a key the caller may not read is not a service of the suite's: it is passed over, the others are still read
            if (image is null || account is null) continue;
            if (!RunsOn(image, directory, Merged(machine, own), programData)) continue;
            // LocalSystem is SYSTEM, already in every list; an account the database cannot resolve is left out rather than failing the open
            if (account.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase)) continue;
            try { AclHelper.ResolveIdentity(account); }
            catch (Exception e) when (e is IdentityNotMappedException or ArgumentException or SystemException) { continue; }
            if (!found.Contains(account, StringComparer.OrdinalIgnoreCase)) found.Add(account);
        }
        return found;
    }

    /// <summary>The machine's environment variables as the services get them (the registry's, not this process's: a command line run by hand may have set its own).</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static Dictionary<string, string> MachineEnvironment()
    {
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Environment");
            if (key is null) return variables;
            foreach (var name in key.GetValueNames())
                if (key.GetValue(name) is string value) variables[name] = value;
        }
        catch (Exception e) when (Unreadable(e)) { }
        return variables;
    }

    /// <summary>A service's own <c>Environment</c> value (<c>NAME=value</c> lines) over the machine's variables: the service sees both, its own winning.</summary>
    internal static Func<string, string?> Merged(IReadOnlyDictionary<string, string> machine, IEnumerable<string>? own)
    {
        var variables = new Dictionary<string, string>(machine, StringComparer.OrdinalIgnoreCase);
        foreach (var line in own ?? [])
        {
            var equals = line.IndexOf('=');
            if (equals > 0) variables[line[..equals]] = line[(equals + 1)..];
        }
        return name => variables.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>
    /// True when a service registered as <paramref name="commandLine"/> keeps its files in <paramref name="directory"/>: the command line names the directory, or it names no folder
    /// and is a suite product whose folder, resolved from <paramref name="environment"/> (the service's variables) the way the product resolves it, is the directory.
    /// </summary>
    internal static bool RunsOn(string commandLine, string directory, Func<string, string?> environment, string programData)
    {
        var line = Normalize(commandLine);
        directory = Normalize(directory).TrimEnd('\\');
        if (Names(line, directory)) return true;
        if (NamesAFolder(line) || ProductOf(line) is not { } folder) return false;
        var resolved = environment($"DBDATAKIT_DATA_{folder.ToUpperInvariant()}") is { } own && !string.IsNullOrWhiteSpace(own) ? own
            : environment("DBDATAKIT_DATA") is { } root && !string.IsNullOrWhiteSpace(root) ? Path.Combine(root.Trim(), folder)
            : Path.Combine(programData, "DbDataKit", folder);
        return string.Equals(Normalize(resolved).TrimEnd('\\'), directory, StringComparison.Ordinal);
    }

    /// <summary>The folder name of the suite product a command line runs (by its executable's name), or null for anything else.</summary>
    private static string? ProductOf(string commandLine)
    {
        var text = commandLine.TrimStart();
        string executable;
        if (text.StartsWith('"')) { var end = text.IndexOf('"', 1); executable = end > 0 ? text[1..end] : text[1..]; }
        else { var exe = text.IndexOf(".exe", StringComparison.Ordinal); executable = exe >= 0 ? text[..(exe + 4)] : text.Split(' ')[0]; }
        var file = Path.GetFileNameWithoutExtension(executable.Replace('\\', Path.DirectorySeparatorChar));
        return ProductFolders.TryGetValue(file, out var folder) ? folder : null;
    }

    /// <summary>True when the command line names a data folder of its own (<c>--repo</c>, <c>--data-dir</c>, <c>--Gateway:DataDirectory=</c>): then only <see cref="Names"/> decides.</summary>
    private static bool NamesAFolder(string commandLine) =>
        commandLine.Contains(" --repo", StringComparison.Ordinal) || commandLine.Contains(" --data-dir", StringComparison.Ordinal) || commandLine.Contains("datadirectory=", StringComparison.Ordinal);

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
