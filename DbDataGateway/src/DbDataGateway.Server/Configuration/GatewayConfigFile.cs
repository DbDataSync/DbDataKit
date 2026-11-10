using DbDataGateway.Server.Hosting;
using Microsoft.Extensions.Configuration;

namespace DbDataGateway.Server.Configuration;

/// <summary>
/// Where the gateway's own settings live (plan 13, P01): the <c>config</c> directory inside the data directory (<c>%ProgramData%\DbDataKit\gateway</c> by default), so replacing the
/// program files never touches them, and a git repository can track it. <c>server.yaml</c> holds the server's settings, <c>gateway/</c> the client authentication, <c>connections/</c> one
/// data source per file (see <see cref="GatewayConfigDirectorySource"/>). Environment variables and the command line override the files. Nothing is shipped in the data directory: defaults
/// are in <see cref="GatewayOptions"/>, and <c>config.example/</c> beside the executable shows every option. An <c>appsettings.json</c> from an earlier release is refused, never read.
/// </summary>
public static class GatewayConfigFile
{
    /// <summary>The prefix of the environment variables that are the gateway's settings (plan 33 section 6c): <c>DBDATAKIT_Gateway__Management__Port</c> is <c>Gateway:Management:Port</c>. The other <c>DBDATAKIT_*</c> variables (<c>DBDATAKIT_DATA</c>, <c>DBDATAKIT_STATE_*</c>, <c>DBDATAKIT_URL</c>) become top-level keys that are not under <c>Gateway</c>, which the strict binder never sees.</summary>
    public const string EnvironmentPrefix = "DBDATAKIT_";

    public const string DirectoryName = "config";
    public const string ServerFileName = "server.yaml";

    /// <summary>The settings file of earlier releases; finding one stops the server with a message that names it.</summary>
    public const string LegacyFileName = "appsettings.json";

    /// <summary>What <c>service install</c> writes when the data directory has no settings file: only the opt-in to the current defaults.</summary>
    public const string Starter = "schemaVersion: 2\n";

    public static string DirectoryIn(string dataDirectory) => Path.Combine(dataDirectory, DirectoryName);

    /// <summary>The path of <c>server.yaml</c> for this data directory.</summary>
    public static string PathIn(string dataDirectory) => Path.Combine(DirectoryIn(dataDirectory), ServerFileName);

    /// <summary>
    /// The data directory, known before any file is read (the files live in it, so a file cannot say where it is): <c>Gateway:DataDirectory</c> from
    /// the command line or <c>DBDATAKIT_</c>/plain environment variables, else the default.
    /// </summary>
    public static string ResolveDataDirectory(IEnumerable<string>? args = null)
    {
        string? configured = null;
        try
        {
            var early = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddCommandLine((args ?? []).ToArray())
                .AddEnvironmentVariables(EnvironmentPrefix)
                .Build();
            configured = early["Gateway:DataDirectory"];
        }
        catch (FormatException)
        {
            // An unreadable command line is reported, with the rest of the configuration problems, by the real builder.
        }

        return GatewayHost.ResolveDataDirectory(new GatewayOptions { DataDirectory = configured });
    }

    /// <summary>
    /// The message for an <c>appsettings.json</c> left from an earlier release (in the data directory, its <c>config</c> directory, or beside the executable), or null when there is none.
    /// This is a clean break: the file is not read and not converted, so the server stops until its settings are written as <c>server.yaml</c>.
    /// </summary>
    public static string? LegacyFileMessage(string dataDirectory, string? baseDirectory = null)
    {
        foreach (var directory in new[] { dataDirectory, DirectoryIn(dataDirectory), baseDirectory ?? AppContext.BaseDirectory })
        {
            var path = Path.Combine(directory, LegacyFileName);
            if (File.Exists(path))
                return $"'{path}' is the settings file of an earlier release and is no longer read. Write its settings as '{PathIn(dataDirectory)}' " +
                       "(camelCase keys, YAML; data sources in connections/<name>.yaml; config.example/ beside the executable shows each file), then delete the old file.";
        }
        return null;
    }

    /// <summary>
    /// Adds the configuration directory to a builder that already has the host's sources, then the environment variables and the command line again so they still win.
    /// </summary>
    public static void AddDataDirectory(IConfigurationBuilder configuration, string dataDirectory, string[] args)
    {
        configuration.Add(new GatewayConfigDirectorySource(DirectoryIn(dataDirectory)));
        // the state database the install's variables name (DBDATAKIT_STATE_GATEWAY_*, else DBDATAKIT_STATE_*) is above the file and below the Gateway's own spellings
        configuration.Add(new SuiteStateConfigurationSource());
        // lowest first, as the README says: the files, the plain variables, the DBDATAKIT_ ones, the command line (plan 29b C07: the flag wins)
        configuration.AddEnvironmentVariables();
        configuration.AddEnvironmentVariables(EnvironmentPrefix);
        configuration.AddCommandLine(args);
    }

    /// <summary>Writes <see cref="Starter"/> when the data directory has no <c>server.yaml</c> yet. True when it wrote one.</summary>
    public static bool EnsureStarter(string dataDirectory)
    {
        string path = PathIn(dataDirectory);
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(DirectoryIn(dataDirectory));
        File.WriteAllText(path, Starter);
        return true;
    }
}
