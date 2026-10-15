using System.Runtime.Versioning;
using System.Text;

namespace DbData.Odbc.Metadata;

/// <summary>The word size a driver was installed for. A 64-bit process loads only 64-bit drivers and a 32-bit process only 32-bit ones.</summary>
public enum OdbcBitness { Unknown = 0, X86 = 32, X64 = 64 }

/// <summary>Where a DSN is defined.</summary>
public enum OdbcDsnScope { User, System }

/// <summary>An installed driver: <paramref name="Name"/> is what <c>Driver={...}</c> takes; <paramref name="Library"/> is the path of its library when known.</summary>
public sealed record OdbcDriverEntry(string Name, string? Library, OdbcBitness Bitness);

/// <summary>A configured data source name and the driver it names (a driver name, or on Unix sometimes a library path).</summary>
public sealed record OdbcDsnEntry(string Name, string? Driver, OdbcDsnScope Scope, OdbcBitness Bitness);

/// <summary>The drivers and DSNs installed on this machine, per bitness.</summary>
public sealed record OdbcInventory(IReadOnlyList<OdbcDriverEntry> Drivers, IReadOnlyList<OdbcDsnEntry> Dsns)
{
    /// <summary>The bitnesses a driver is installed for (empty when it is not installed). Names compare ignoring case.</summary>
    public IReadOnlyList<OdbcBitness> DriverBitnesses(string name) =>
        Drivers.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).Select(d => d.Bitness).Distinct().ToList();

    /// <summary>The bitnesses a DSN is defined for (empty when it is not defined).</summary>
    public IReadOnlyList<OdbcBitness> DsnBitnesses(string name) =>
        Dsns.Where(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase)).Select(d => d.Bitness).Distinct().ToList();

    /// <summary>
    /// True when a process of <paramref name="processBitness"/> can use the driver. A driver with unknown bitness counts as usable.
    /// False means it is not installed, or only for the other word size: route the work to a worker of the other bitness.
    /// </summary>
    public bool CanLoadDriver(string name, OdbcBitness processBitness) =>
        DriverBitnesses(name) is { Count: > 0 } found && found.Any(b => b == processBitness || b == OdbcBitness.Unknown);
}

/// <summary>The hives <see cref="OdbcDriverInventory"/> reads on Windows.</summary>
public enum OdbcRegistryHive { LocalMachine, CurrentUser }

/// <summary>Registry access for <see cref="OdbcDriverInventory"/>, so tests need no registry. The view argument is the 32-bit (WOW6432Node) or 64-bit registry view.</summary>
public interface IOdbcRegistryReader
{
    /// <summary>The value names under the key, empty when the key does not exist.</summary>
    IReadOnlyList<string> GetValueNames(OdbcRegistryHive hive, OdbcBitness view, string subKey);

    /// <summary>A string value, or null when the key or value does not exist.</summary>
    string? GetValue(OdbcRegistryHive hive, OdbcBitness view, string subKey, string name);
}

/// <summary>File and environment access for <see cref="OdbcDriverInventory"/> on Unix, so tests need no files.</summary>
public interface IOdbcFileReader
{
    /// <summary>The text of the file, or null when it does not exist or cannot be read.</summary>
    string? ReadAllText(string path);

    string? GetEnvironmentVariable(string name);
}

/// <summary>Lists installed ODBC drivers and DSNs with their bitness, so a consumer can see "this driver is 32-bit only" instead of finding out by a failed connect.</summary>
public static class OdbcDriverInventory
{
    private const string DriversKey = @"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers";
    private const string DriverKeyFormat = @"SOFTWARE\ODBC\ODBCINST.INI\{0}";
    private const string DataSourcesKey = @"SOFTWARE\ODBC\ODBC.INI\ODBC Data Sources";

    /// <summary>Reads this machine: the registry (both views, machine and user hives) on Windows, the <c>odbcinst.ini</c> and <c>odbc.ini</c> files elsewhere.</summary>
    public static OdbcInventory Read() =>
        OperatingSystem.IsWindows() ? FromWindowsRegistry(new WindowsRegistryReader(), Environment.Is64BitOperatingSystem) : FromUnixFiles(new PhysicalFileReader());

    /// <summary>The Windows layout. Drivers are listed under <c>ODBCINST.INI\ODBC Drivers</c> and data sources under <c>ODBC.INI\ODBC Data Sources</c>.</summary>
    /// <param name="registry">Where the keys are read from.</param>
    /// <param name="is64BitOperatingSystem">False on 32-bit Windows, which has only the one (32-bit) view.</param>
    public static OdbcInventory FromWindowsRegistry(IOdbcRegistryReader registry, bool is64BitOperatingSystem = true)
    {
        ArgumentNullException.ThrowIfNull(registry);
        var drivers = new List<OdbcDriverEntry>();
        var dsns = new List<OdbcDsnEntry>();
        OdbcBitness[] views = is64BitOperatingSystem ? [OdbcBitness.X64, OdbcBitness.X86] : [OdbcBitness.X86];

        foreach (var view in views)
        {
            // Driver registrations are machine-wide; the user hive can add data sources only.
            foreach (string name in registry.GetValueNames(OdbcRegistryHive.LocalMachine, view, DriversKey))
            {
                if (registry.GetValue(OdbcRegistryHive.LocalMachine, view, DriversKey, name) is not "Installed") continue;
                string? library = registry.GetValue(OdbcRegistryHive.LocalMachine, view, string.Format(System.Globalization.CultureInfo.InvariantCulture, DriverKeyFormat, name), "Driver");
                drivers.Add(new OdbcDriverEntry(name, library, view));
            }
            foreach (var (hive, scope) in new[] { (OdbcRegistryHive.LocalMachine, OdbcDsnScope.System), (OdbcRegistryHive.CurrentUser, OdbcDsnScope.User) })
                foreach (string name in registry.GetValueNames(hive, view, DataSourcesKey))
                    dsns.Add(new OdbcDsnEntry(name, registry.GetValue(hive, view, DataSourcesKey, name), scope, view));
        }
        return new OdbcInventory(drivers, dsns);
    }

    /// <summary>
    /// The unixODBC layout: <c>odbcinst.ini</c> sections are drivers (<c>Driver = library</c>), <c>odbc.ini</c> sections are DSNs. The files are searched
    /// where unixODBC looks (<c>ODBCSYSINI</c>, <c>/etc</c>, <c>/usr/local/etc</c>, <c>~/.odbc.ini</c>). A process cannot load a library of another word size,
    /// so bitness is taken from the library path (<c>lib64</c>, <c>x86_64</c>, <c>i386</c>...) and is <see cref="OdbcBitness.Unknown"/> when the path says nothing.
    /// </summary>
    public static OdbcInventory FromUnixFiles(IOdbcFileReader files)
    {
        ArgumentNullException.ThrowIfNull(files);
        string? sysDir = files.GetEnvironmentVariable("ODBCSYSINI");
        string? home = files.GetEnvironmentVariable("HOME");
        string[] systemDirs = sysDir is { Length: > 0 } ? [sysDir] : ["/etc", "/usr/local/etc"];

        var drivers = new List<OdbcDriverEntry>();
        foreach (string dir in systemDirs)
            foreach (var (name, values) in ParseIni(files.ReadAllText(dir.TrimEnd('/') + "/odbcinst.ini")))
                if (name != "ODBC" && name != "ODBC Drivers" && !drivers.Any(d => d.Name == name))
                {
                    values.TryGetValue("driver", out string? library);
                    drivers.Add(new OdbcDriverEntry(name, library, BitnessOf(library)));
                }

        var dsns = new List<OdbcDsnEntry>();
        var dsnFiles = systemDirs.Select(d => (Path: d.TrimEnd('/') + "/odbc.ini", Scope: OdbcDsnScope.System)).ToList();
        if (home is not null) dsnFiles.Add((home.TrimEnd('/') + "/.odbc.ini", OdbcDsnScope.User));
        foreach (var (path, scope) in dsnFiles)
            foreach (var (name, values) in ParseIni(files.ReadAllText(path)))
                if (name != "ODBC" && name != "ODBC Data Sources" && !dsns.Any(d => d.Name == name && d.Scope == scope))
                {
                    values.TryGetValue("driver", out string? driver);
                    var named = drivers.FirstOrDefault(d => string.Equals(d.Name, driver, StringComparison.OrdinalIgnoreCase));
                    dsns.Add(new OdbcDsnEntry(name, driver, scope, named?.Bitness ?? BitnessOf(driver)));
                }
        return new OdbcInventory(drivers, dsns);
    }

    internal static OdbcBitness BitnessOf(string? library)
    {
        if (library is null) return OdbcBitness.Unknown;
        string l = library.ToLowerInvariant();
        if (l.Contains("lib64", StringComparison.Ordinal) || l.Contains("x86_64", StringComparison.Ordinal) || l.Contains("amd64", StringComparison.Ordinal) || l.Contains("aarch64", StringComparison.Ordinal)) return OdbcBitness.X64;
        if (l.Contains("lib32", StringComparison.Ordinal) || l.Contains("i386", StringComparison.Ordinal) || l.Contains("i686", StringComparison.Ordinal)) return OdbcBitness.X86;
        return OdbcBitness.Unknown;
    }

    // [Section] then key = value lines; ; and # start a comment. Keys are lower case.
    internal static List<(string Name, Dictionary<string, string> Values)> ParseIni(string? text)
    {
        var sections = new List<(string, Dictionary<string, string>)>();
        if (text is null) return sections;
        Dictionary<string, string>? current = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#') continue;
            if (line[0] == '[' && line[^1] == ']')
            {
                current = new Dictionary<string, string>();
                sections.Add((line[1..^1].Trim(), current));
            }
            else if (current is not null && line.IndexOf('=') is var eq and > 0)
                current[line[..eq].Trim().ToLowerInvariant()] = line[(eq + 1)..].Trim();
        }
        return sections;
    }

    [SupportedOSPlatform("windows")]
    private sealed class WindowsRegistryReader : IOdbcRegistryReader
    {
        private static Microsoft.Win32.RegistryKey? Open(OdbcRegistryHive hive, OdbcBitness view, string subKey)
        {
            try
            {
                using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(
                    hive == OdbcRegistryHive.LocalMachine ? Microsoft.Win32.RegistryHive.LocalMachine : Microsoft.Win32.RegistryHive.CurrentUser,
                    view == OdbcBitness.X86 ? Microsoft.Win32.RegistryView.Registry32 : Microsoft.Win32.RegistryView.Registry64);
                return root.OpenSubKey(subKey);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ArgumentException) { return null; }
        }

        public IReadOnlyList<string> GetValueNames(OdbcRegistryHive hive, OdbcBitness view, string subKey)
        {
            using var key = Open(hive, view, subKey);
            return key?.GetValueNames() ?? [];
        }

        public string? GetValue(OdbcRegistryHive hive, OdbcBitness view, string subKey, string name)
        {
            using var key = Open(hive, view, subKey);
            return key?.GetValue(name) as string;
        }
    }

    private sealed class PhysicalFileReader : IOdbcFileReader
    {
        public string? ReadAllText(string path)
        {
            try { return File.Exists(path) ? File.ReadAllText(path, Encoding.UTF8) : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
        }

        public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);
    }
}
