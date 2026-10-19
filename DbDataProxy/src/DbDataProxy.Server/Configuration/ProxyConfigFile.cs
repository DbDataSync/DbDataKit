using DbDataProxy.Server.Hosting;
using Microsoft.Extensions.Configuration;

namespace DbDataProxy.Server.Configuration;

/// <summary>
/// Where the proxy's own settings live: <c>appsettings.json</c> in the data directory (<c>%ProgramData%\DbDataProxy</c> by default), so replacing the
/// program files never touches them. A file of the same name beside the executable is still read, as the lowest layer (for a portable run from a
/// folder, or a container that mounts one); the data directory's file overrides it, and environment variables and the command line override both.
/// Nothing is shipped in either place: defaults are in <see cref="ProxyOptions"/>.
/// </summary>
public static class ProxyConfigFile
{
    public const string FileName = "appsettings.json";

    /// <summary>What <c>service install</c> writes when the data directory has no settings file: only the opt-in to the current defaults.</summary>
    public const string Starter = "{\n  \"Proxy\": {\n    \"SchemaVersion\": 2\n  }\n}\n";

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>
    /// The data directory, known before any file is read (the file lives in it, so a file cannot say where it is): <c>Proxy:DataDirectory</c> from
    /// the command line or <c>DBPROXY_</c>/plain environment variables, else the default.
    /// </summary>
    public static string ResolveDataDirectory(IEnumerable<string>? args = null)
    {
        string? configured = null;
        try
        {
            var early = new ConfigurationBuilder()
                .AddEnvironmentVariables()
                .AddCommandLine((args ?? []).ToArray())
                .AddEnvironmentVariables("DBPROXY_")
                .Build();
            configured = early["Proxy:DataDirectory"];
        }
        catch (FormatException)
        {
            // An unreadable command line is reported, with the rest of the configuration problems, by the real builder.
        }

        return ProxyHost.ResolveDataDirectory(new ProxyOptions { DataDirectory = configured });
    }

    /// <summary>
    /// Adds the data directory's settings file to a builder that already has the host's sources (the file beside the executable, environment
    /// variables, the command line). Those are added again after it so they still win.
    /// </summary>
    public static void AddDataDirectoryFile(IConfigurationBuilder configuration, string dataDirectory, string[] args)
    {
        configuration.AddJsonFile(PathIn(dataDirectory), optional: true, reloadOnChange: true);
        configuration.AddEnvironmentVariables();
        configuration.AddCommandLine(args);
        configuration.AddEnvironmentVariables("DBPROXY_");
    }

    /// <summary>Writes <see cref="Starter"/> when the data directory has no settings file yet. True when it wrote one.</summary>
    public static bool EnsureStarter(string dataDirectory)
    {
        string path = PathIn(dataDirectory);
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(dataDirectory);
        File.WriteAllText(path, Starter);
        return true;
    }
}
