using System.Diagnostics;
using System.Globalization;

namespace DbDataKit.Hosting.Startup;

/// <summary>A range of ports, both ends included.</summary>
public sealed record PortRange(int Start, int End)
{
    public bool Contains(int port) => port >= Start && port <= End;

    public override string ToString() => Start == End ? Start.ToString(CultureInfo.InvariantCulture) : $"{Start}-{End}";
}

/// <summary>
/// Windows' excluded port ranges: Hyper-V, WSL, Docker and WinNAT reserve ranges of ports that move at each boot, and a listener inside one fails
/// with "access denied" although no program holds the port. Read from <c>netsh int ipv4 show excludedportrange protocol=tcp</c>.
/// </summary>
public static class ExcludedPortRanges
{
    /// <summary>The command that lists them, for a person to run.</summary>
    public const string Command = "netsh int ipv4 show excludedportrange protocol=tcp";

    /// <summary>The ranges on this machine; empty on other systems, and when <c>netsh</c> cannot be run.</summary>
    public static IReadOnlyList<PortRange> Read()
    {
        if (!OperatingSystem.IsWindows()) return [];
        try
        {
            using var netsh = Process.Start(new ProcessStartInfo("netsh", ["int", "ipv4", "show", "excludedportrange", "protocol=tcp"])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (netsh is null) return [];
            var output = netsh.StandardOutput.ReadToEnd();
            if (!netsh.WaitForExit(10_000)) { netsh.Kill(); return []; }
            return Parse(output);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return [];
        }
    }

    /// <summary>
    /// The ranges in <c>netsh</c>'s output: each line that starts with two numbers (start and end port; a trailing <c>*</c> marks a range an
    /// administrator excluded). The headings and the legend are skipped.
    /// </summary>
    public static IReadOnlyList<PortRange> Parse(string netshOutput)
    {
        var ranges = new List<PortRange>();
        foreach (var line in netshOutput.Split('\n'))
        {
            var parts = line.Split([' ', '\t', '\r'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2
                && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var start)
                && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var end)
                && start <= end && end <= 65535)
                ranges.Add(new PortRange(start, end));
        }
        return ranges;
    }

    /// <summary>The ranges for the failure file: "5357, 50000-50059", or that there are none.</summary>
    public static string Describe(IReadOnlyList<PortRange> ranges) =>
        ranges.Count == 0
            ? OperatingSystem.IsWindows() ? $"none (`{Command}`)" : "none on this system"
            : string.Join(", ", ranges) + (OperatingSystem.IsWindows() ? $" (`{Command}`)" : "");
}
