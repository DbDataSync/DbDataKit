using System.Text.RegularExpressions;
using DbDataGateway.Server.Configuration;
using DbDataKit.Hosting.WindowsService;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// The clean break of the rename DbDataProxy to DbDataGateway (plan 20, O14). The new program does not read what the old one wrote under the old names and does not accept the old names as aliases: it
/// stops, once, with one message per leftover that names the old name, the new one and what to do. A message names the file or variable it found. Nothing is migrated silently and nothing is accepted in both spellings.
/// What it looks for: <c>--Proxy:</c> command-line settings (the environment variables of the old product are refused by <c>SuiteEnvironment</c>, which every command runs first), the old default data directory when the new one has no settings, a <c>config/proxy</c> folder, <c>extensions.proxy</c>
/// in a connection file, and (on Windows, at <c>service install</c>) the service registered as <c>dbdataproxy</c>. Secret names are not looked for: every secret name of the suite changed again (plan 33 SC31c, <c>dbdatakit:&lt;service&gt;:...</c>), and a store written under an earlier name simply no longer finds its secrets.
/// </summary>
public static class RenamedFrom
{
    public const string OldProduct = "DbDataProxy";
    public const string OldProgram = "dbdataproxy";

    private const string Why = "DbDataProxy is now DbDataGateway (a clean break: the old names are refused, not read). ";

    private static readonly Regex ExtensionKey = new(@"(?m)^[ \t]+proxy:", RegexOptions.CultureInvariant);

    /// <summary>Every leftover of the old name for this command line and machine; empty when there is none.</summary>
    public static IReadOnlyList<string> Problems(string[] args, string? dataDirectory = null, string? commonDataDirectory = null)
    {
        var problems = new List<string>();

        foreach (var arg in args)
        {
            var bare = arg.TrimStart('-', '/');
            if (bare.StartsWith("Proxy:", StringComparison.OrdinalIgnoreCase) || bare.StartsWith("Proxy__", StringComparison.OrdinalIgnoreCase))
                problems.Add($"{Why}The setting '{arg}' is the old name of '--Gateway{bare["Proxy".Length..]}': the settings section is now 'Gateway'.");
        }

        dataDirectory ??= GatewayConfigFile.ResolveDataDirectory(args);
        var common = commonDataDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (!string.IsNullOrEmpty(common))
        {
            var oldDirectory = Path.Combine(common, OldProduct);
            if (Directory.Exists(oldDirectory) && !SamePath(oldDirectory, dataDirectory) && !File.Exists(GatewayConfigFile.PathIn(dataDirectory)))
                problems.Add($"{Why}'{oldDirectory}' is the data directory of {OldProduct}; {GatewayHost.ServiceName} reads '{dataDirectory}' and has no settings there. Stop the old service, then move the folder " +
                             $"to '{dataDirectory}' (or point the new one at it with --Gateway:DataDirectory=<folder>) and fix what the other messages name.");
        }

        var config = GatewayConfigFile.DirectoryIn(dataDirectory);
        var oldFolder = Path.Combine(config, "proxy");
        if (Directory.Exists(oldFolder))
            problems.Add($"{Why}'{oldFolder}' is the old folder of the client authentication ({OldProduct}); the folder is now '{Path.Combine(config, "gateway")}'. Rename it.");

        if (Directory.Exists(config))
        {
            foreach (var file in Files(config, "*.yaml").Concat(Files(config, "*.yml")))
            {
                var text = Read(file);
                if (text is null) continue;
                if (Path.GetFileName(Path.GetDirectoryName(file)) == "connections" && text.Contains("extensions:", StringComparison.Ordinal) && ExtensionKey.IsMatch(text))
                    problems.Add($"{Why}'{file}' has 'extensions: proxy:'; the key is now 'extensions: gateway:'. Rename it.");
            }
        }

        return problems;
    }

    /// <summary>The message for a service still registered under the old name, or null. Used by <c>service install</c> on Windows.</summary>
    public static string? OldService(IServiceControlManager scm)
    {
        try
        {
            if (scm.GetConfig(OldProgram) is null) return null;
        }
        catch (ServiceControlException)
        {
            return null;
        }
        return $"{Why}The Windows service '{OldProgram}' is still installed. Remove it with the old program ('{OldProgram} service uninstall'), then install this one ('{GatewayHost.RegistrationName}'); it does not take over the old service.";
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar), Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static IEnumerable<string> Files(string directory, string pattern)
    {
        try { return Directory.EnumerateFiles(directory, pattern, SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private static string? Read(string path)
    {
        try { return File.Exists(path) && new FileInfo(path).Length <= 4_000_000 ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
