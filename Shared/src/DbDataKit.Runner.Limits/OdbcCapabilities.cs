using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DbDataKit.Runner.Limits;

/// <summary>
/// The ODBC drivers a machine has, as capabilities a runner reports (plan 28c QE12): one capability per installed driver, named <c>odbc:&lt;id&gt;</c> with the driver's own name as the value. An ODBC driver is installed on the
/// machine and cannot be packaged, so a task that needs one says <c>requires: [odbc:sqlserver18]</c> and only a runner that reports it is given the task (`RoutingCheck` in DbDataKit.State); the report is read-only, like
/// <c>limit_mechanism</c>. The ids: <c>ODBC Driver 18 for SQL Server</c> is <c>sqlserver18</c>; any other name is lower-cased with every run of other characters than letters and digits made one <c>_</c> (<c>PostgreSQL Unicode</c> is
/// <c>postgresql_unicode</c>).
/// </summary>
public static class OdbcCapabilities
{
    public const string Prefix = "odbc:";

    private static readonly Regex SqlServerDriver = new(@"^ODBC Driver (\d+) for SQL Server$", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    /// <summary>The capability name of a driver: <c>odbc:sqlserver18</c>. Null for a name with no letter or digit.</summary>
    public static string? NameFor(string driver)
    {
        if (string.IsNullOrWhiteSpace(driver)) return null;
        var trimmed = driver.Trim();
        if (SqlServerDriver.Match(trimmed) is { Success: true } sql) return Prefix + "sqlserver" + sql.Groups[1].Value;
        var id = Regex.Replace(trimmed.ToLowerInvariant(), "[^a-z0-9]+", "_").Trim('_');
        return id.Length == 0 ? null : Prefix + id;
    }

    /// <summary>The capabilities for these driver names: <c>odbc:&lt;id&gt;</c> to the name. Two names with one id keep the first.</summary>
    public static IReadOnlyDictionary<string, string> From(IEnumerable<string> drivers)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var driver in drivers)
            if (NameFor(driver) is { } name) result.TryAdd(name, driver.Trim());
        return result;
    }

    /// <summary>The driver names in the text of <c>odbcinst -q -d</c> (<c>[Driver Name]</c> per line) or of an <c>odbcinst.ini</c> (a section per driver; the <c>[ODBC]</c> section is the manager's own, not a driver).</summary>
    public static IReadOnlyList<string> ParseDriverList(string text) =>
        [.. text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 2 && l[0] == '[' && l[^1] == ']').Select(l => l[1..^1].Trim())
            .Where(n => n.Length > 0 && !n.Equals("ODBC", StringComparison.OrdinalIgnoreCase) && !n.Equals("ODBC Drivers", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// The drivers this machine has: on Windows the registered driver names (both the 64 and the 32 bit registry views are read: the runner starts 64 bit tasks, and a 32 bit driver shows only for a 32 bit one, so only the 64 bit view is
    /// reported), elsewhere what <c>odbcinst -q -d</c> lists, or, when unixODBC's tool is not installed, the sections of <c>odbcinst.ini</c> (<c>ODBCSYSINI</c> or <c>/etc</c>). A machine with no ODBC manager reports none.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Detect() => From(OperatingSystem.IsWindows() ? WindowsDrivers() : UnixDrivers());

    private static IEnumerable<string> WindowsDrivers()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64).OpenSubKey(@"SOFTWARE\ODBC\ODBCINST.INI\ODBC Drivers");
            return key?.GetValueNames() ?? [];
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return [];
        }
    }

    private static IEnumerable<string> UnixDrivers()
    {
        try
        {
            var info = new ProcessStartInfo("odbcinst", "-q -d") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(info);
            if (process is not null)
            {
                var output = process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEndAsync();
                if (process.WaitForExit(TimeSpan.FromSeconds(5)) && process.ExitCode == 0) return ParseDriverList(output.GetAwaiter().GetResult());
                try { process.Kill(); } catch (InvalidOperationException) { /* it ended */ }
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // the unixODBC tool is not installed: read the file it would read
        }

        var directory = Environment.GetEnvironmentVariable("ODBCSYSINI") is { Length: > 0 } given ? given : "/etc";
        var ini = Path.Combine(directory, "odbcinst.ini");
        try { return File.Exists(ini) ? ParseDriverList(File.ReadAllText(ini)) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }
}
