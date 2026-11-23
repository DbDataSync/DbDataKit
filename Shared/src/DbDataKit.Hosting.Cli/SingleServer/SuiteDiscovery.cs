using System.Net.Http;
using System.Security.Cryptography;
using DbDataKit.Hosting.Startup;
using YamlDotNet.RepresentationModel;

namespace DbDataKit.Hosting.Cli;

/// <summary>
/// The suite's programs installed beside <c>dbdatakit</c>. Every package puts each program in a folder of its own next to the console's: the zips and
/// the Scoop package in a folder named for the service (<c>...\gateway\dbdatagateway.exe</c> beside <c>...\console\dbdatakit.exe</c>), <c>install.sh</c>
/// in a folder named for the program (<c>/opt/dbdatagateway/dbdatagateway</c> beside <c>/opt/dbdatakit/dbdatakit</c>). A program in the console's own
/// folder is found too.
/// Design: docs/install.md#setting-up-one-machine-or-inviting-another
/// </summary>
public static class SuiteDiscovery
{
    /// <summary>The file name of <paramref name="service"/>'s program on this operating system: <c>dbdatagateway.exe</c> on Windows.</summary>
    public static string ProgramFile(SuiteService service) => OperatingSystem.IsWindows() ? service.Program + ".exe" : service.Program;

    /// <summary>Where the program of <paramref name="service"/> is looked for, beside the console's program <paramref name="consoleBinary"/>, in that order.</summary>
    public static IReadOnlyList<string> Candidates(SuiteService service, string consoleBinary)
    {
        var consoleFolder = Path.GetDirectoryName(Path.GetFullPath(consoleBinary))!;
        var parent = Path.GetDirectoryName(consoleFolder);
        var file = ProgramFile(service);
        var found = new List<string>();
        if (parent is not null)
        {
            found.Add(Path.Combine(parent, service.DataFolder, file));
            found.Add(Path.Combine(parent, service.Program, file));
        }
        found.Add(Path.Combine(consoleFolder, file));
        return [.. found.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)];
    }

    /// <summary>
    /// The services installed on this machine, in start order: the console (<paramref name="consoleBinary"/>) and each other service whose program is
    /// found beside it, each on the data folder <paramref name="dataFolder"/> gives it; and the services that are not installed, with where they were
    /// looked for.
    /// </summary>
    public static (IReadOnlyList<SetupMember> Found, IReadOnlyList<(SuiteService Service, IReadOnlyList<string> LookedIn)> Missing) Find(
        string consoleBinary, Func<SuiteService, string> dataFolder, Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var found = new List<SetupMember>();
        var missing = new List<(SuiteService, IReadOnlyList<string>)>();
        foreach (var service in SuiteServices.All.OrderBy(s => s.StartOrder))
        {
            if (service == SuiteServices.Console)
            {
                found.Add(new SetupMember(service, Path.GetFullPath(consoleBinary), dataFolder(service)));
                continue;
            }
            var candidates = Candidates(service, consoleBinary);
            if (candidates.FirstOrDefault(exists) is { } program) found.Add(new SetupMember(service, program, dataFolder(service)));
            else missing.Add((service, candidates));
        }
        return (found, missing);
    }
}

/// <summary>Where a service of the suite listens, read from its own settings file, and whether it answers there.</summary>
public static class SuiteListen
{
    /// <summary>
    /// The address <paramref name="service"/> reports to the console, from the settings in <paramref name="dataFolder"/>: the <c>listen:</c> line of the
    /// console's, the Runner's or the Workspace's file, the Gateway's <c>management.listen</c>. Null when the file or the setting is not there yet (the
    /// server writes it at its first start) or cannot be read.
    /// </summary>
    public static Uri? Address(SuiteService service, string dataFolder)
    {
        var file = Path.Combine(dataFolder, service.SettingsFile);
        try
        {
            return service == SuiteServices.Gateway ? GatewayManagement(file) : ListenPorts.ReadPinned(file);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or YamlDotNet.Core.YamlException or UriFormatException)
        {
            return null;
        }
    }

    // management.listen of the Gateway's server.yaml: one address or a list (the first one is reported); the Gateway matches its keys without case
    private static Uri? GatewayManagement(string file)
    {
        if (!File.Exists(file)) return null;
        var stream = new YamlStream();
        using (var reader = new StreamReader(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))) stream.Load(reader);
        if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode root) return null;
        if (Child(root, "management") is not YamlMappingNode management) return null;
        var value = Child(management, "listen") switch
        {
            YamlScalarNode scalar => scalar.Value,
            YamlSequenceNode list => list.Children.OfType<YamlScalarNode>().FirstOrDefault()?.Value,
            _ => null,
        };
        return string.IsNullOrWhiteSpace(value) ? null : new Uri(value.Trim());

        static YamlNode? Child(YamlMappingNode map, string key) =>
            map.Children.FirstOrDefault(e => e.Key is YamlScalarNode { Value: { } k } && string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>The address to ask on this machine for a listener on <paramref name="listen"/>: a listener on every address is asked on the loopback.</summary>
    public static Uri Local(Uri listen) => listen.Host switch
    {
        "0.0.0.0" or "*" or "+" => new UriBuilder(listen) { Host = "127.0.0.1", Path = "" }.Uri,
        "[::]" or "::" => new UriBuilder(listen) { Host = "[::1]", Path = "" }.Uri,
        _ => new Uri(listen.GetLeftPart(UriPartial.Authority)),
    };

    /// <summary>
    /// Asks <c>/health/live</c> at <paramref name="listen"/> and returns the SHA-256 of the certificate the server presented (lower-case hex, as the console
    /// pins it), or null when it does not answer. The certificate is not checked here: the answer only says the server listens, and the fingerprint is
    /// compared with the one the console pinned.
    /// </summary>
    public static async Task<string?> ProbeAsync(Uri listen, CancellationToken ct)
    {
        string? fingerprint = null;
        using var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, certificate, _, _) =>
            {
                if (certificate is not null) fingerprint = Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())).ToLowerInvariant();
                return true;
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var answer = await http.GetAsync(new Uri(Local(listen), "/health/live"), ct).ConfigureAwait(false);
            return answer.IsSuccessStatusCode ? fingerprint : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }
}
