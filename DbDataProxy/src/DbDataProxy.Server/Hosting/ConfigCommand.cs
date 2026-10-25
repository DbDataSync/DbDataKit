using DbDataKit.Config.Hosting;
using DbDataProxy.Server.Configuration;
using Microsoft.Extensions.Configuration;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// <c>DbDataProxy config check|show</c>. Exit codes: 0 valid (warnings are printed), 2 invalid or unreadable (the same code the service uses at startup).
/// Output never contains secret values.
/// </summary>
public static class ConfigCommand
{
    public static int Run(string[] args, TextWriter? output = null, TextWriter? error = null)
    {
        output ??= Console.Out;
        error ??= Console.Error;

        string? verb = args.Length > 0 ? args[0].ToLowerInvariant() : null;
        bool origin = false;
        bool allowUnknown = false;
        string? file = null;
        var configArgs = new List<string>();
        for (int i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--origin": origin = true; break;
                case "--allow-unknown-keys": allowUnknown = true; break;
                case "--file" when i + 1 < args.Length: file = args[++i]; break;
                default: configArgs.Add(args[i]); break;
            }
        }
        if (verb is not ("check" or "show"))
        {
            error.WriteLine("Usage: DbDataProxy config check [--file <server.yaml or config directory>] | config show [--origin] [--file <...>]");
            return ProxyConfigException.ExitCode;
        }
        if (verb == "check" && origin)
        {
            error.WriteLine("--origin applies to 'config show'.");
            return ProxyConfigException.ExitCode;
        }

        ProxyConfigLoad load;
        try
        {
            var root = file is null ? (IConfigurationRoot)ProxyHost.CreateBuilder(configArgs.ToArray()).Configuration : FromFile(file, configArgs);
            load = ProxyConfig.Load(root, new ProxyConfigLoadOptions { AllowUnknownKeys = allowUnknown });
        }
        catch (Exception ex) when (ex is ProxyConfigException or InvalidDataException or FormatException or FileNotFoundException or DirectoryNotFoundException)
        {
            error.WriteLine(ex is ProxyConfigException ? ex.Message : Describe(ex));
            return ProxyConfigException.ExitCode;
        }

        if (!load.Ok)
        {
            error.WriteLine(ProxyConfig.Format(load.Diagnostics));
            return ProxyConfigException.ExitCode;
        }

        if (verb == "check")
        {
            if (load.Diagnostics.Count > 0) error.WriteLine(ProxyConfig.Format(load.Diagnostics));
            output.WriteLine($"The configuration is valid (schema version {load.Value!.SchemaVersion}).");
            return 0;
        }

        if (load.Diagnostics.Count > 0) error.WriteLine(ProxyConfig.Format(load.Diagnostics));
        foreach (var entry in ProxyConfig.Show(load))
            output.WriteLine(origin ? entry.ToString() : $"{ProxyOptions.SectionName}:{entry.Path.Replace("].", ":").Replace('[', ':').Replace("]", "").Replace('.', ':')} = {entry.Value}");
        return 0;
    }

    /// <summary>Reads a configuration directory other than the installed one: the path of its <c>server.yaml</c>, or the directory itself.</summary>
    private static IConfigurationRoot FromFile(string path, List<string> args)
    {
        string full = Path.GetFullPath(path);
        if (full.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new ProxyConfigException($"'{full}' is a JSON settings file, which is no longer read. Write the settings as server.yaml in a config directory (config.example/ beside the executable shows each file).", []);
        string directory = Directory.Exists(full) ? full : Path.GetDirectoryName(full)!;
        if (!Directory.Exists(directory) || (!Directory.Exists(full) && !File.Exists(full)))
            throw new FileNotFoundException($"The configuration file '{full}' was not found.");
        return new ConfigurationBuilder()
            .Add(new ProxyConfigDirectorySource(directory))
            .AddCommandLine(args.ToArray())
            .Build();
    }

    private static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        return "The configuration could not be read: " + string.Join(" ", parts.Distinct());
    }
}
