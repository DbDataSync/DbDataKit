using System.Collections;
using System.Text.RegularExpressions;
using DbDataGateway.Server.Configuration;
using DbDataKit.Hosting.WindowsService;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// The clean break of the rename DbDataProxy to DbDataGateway (plan 20, O14). The new program does not read what the old one wrote under the old names and does not accept the old names as aliases: it
/// stops, once, with one message per leftover that names the old name, the new one and what to do. A message names the file or variable it found. Nothing is migrated silently and nothing is accepted in both spellings.
/// What it looks for: <c>DBPROXY_</c> environment variables, <c>--Proxy:</c> command-line settings, the old default data directory when the new one has no settings, a <c>config/proxy</c> folder, <c>extensions.proxy</c>
/// or <c>dbdata:proxy:</c> in a settings or connection file, secrets stored as <c>dbdata:proxy:*</c>, and (on Windows, at <c>service install</c>) the service registered as <c>dbdataproxy</c>.
/// </summary>
public static class RenamedFrom
{
    public const string OldProduct = "DbDataProxy";
    public const string OldProgram = "dbdataproxy";
    public const string OldEnvironmentPrefix = "DBPROXY_";
    public const string NewEnvironmentPrefix = "DBGATEWAY_";
    public const string OldSecretPrefix = "dbdata:proxy:";
    public const string NewSecretPrefix = "dbdata:gateway:";

    private const string Why = "DbDataProxy is now DbDataGateway (a clean break: the old names are refused, not read). ";

    private static readonly Regex ExtensionKey = new(@"(?m)^[ \t]+proxy:", RegexOptions.CultureInvariant);

    /// <summary>Every leftover of the old name for this command line and machine; empty when there is none.</summary>
    public static IReadOnlyList<string> Problems(string[] args, IReadOnlyDictionary<string, string?>? environment = null, string? dataDirectory = null, string? commonDataDirectory = null)
    {
        environment ??= CurrentEnvironment();
        var problems = new List<string>();

        foreach (var name in environment.Keys.OrderBy(k => k, StringComparer.Ordinal))
            if (name.StartsWith(OldEnvironmentPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = name[OldEnvironmentPrefix.Length..];
                if (rest.StartsWith("Proxy__", StringComparison.OrdinalIgnoreCase)) rest = "Gateway__" + rest["Proxy__".Length..];
                problems.Add($"{Why}The environment variable {name} is the old name of {NewEnvironmentPrefix}{rest}; rename it (the settings section is now 'Gateway').");
            }

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
                if (text.Contains(OldSecretPrefix, StringComparison.Ordinal))
                    problems.Add($"{Why}'{file}' refers to a secret named {OldSecretPrefix}...; the names are now {NewSecretPrefix}... (rename the secret and the reference).");
                if (Path.GetFileName(Path.GetDirectoryName(file)) == "connections" && text.Contains("extensions:", StringComparison.Ordinal) && ExtensionKey.IsMatch(text))
                    problems.Add($"{Why}'{file}' has 'extensions: proxy:'; the key is now 'extensions: gateway:'. Rename it.");
            }
        }

        var secrets = Path.Combine(dataDirectory, "secrets", "secrets.yaml");
        if (Read(secrets) is { } store && store.Contains(OldSecretPrefix, StringComparison.Ordinal))
            problems.Add($"{Why}The secret store '{secrets}' holds secrets named {OldSecretPrefix}*; the program looks for {NewSecretPrefix}*. Set each under its new name ('secret set') and remove the old one ('secret delete').");

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

    private static IReadOnlyDictionary<string, string?> CurrentEnvironment()
    {
        // not ToDictionary: a Linux environment may hold two names that differ only in case (http_proxy and HTTP_PROXY)
        var all = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) all[(string)e.Key] = (string?)e.Value;
        return all;
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
