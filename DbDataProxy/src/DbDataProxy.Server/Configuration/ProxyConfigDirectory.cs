using DbDataKit.Config;
using DbDataKit.Config.Hosting;
using DbDataKit.Config.Yaml;
using Microsoft.Extensions.Configuration;

namespace DbDataProxy.Server.Configuration;

/// <summary>
/// The proxy's configuration directory (plan 13, P01), read as one configuration source under the <c>Proxy</c> section:
/// <code>
/// config/
///   server.yaml            the server's own settings (listen, tls, secrets, compression, hosting, worker, logging switches)
///   proxy/auth.yaml        how clients prove who they are (sessions, lockout, pass-through, JWT and its mappings)
///   proxy/users.yaml       the local client users: a <c>users:</c> list
///   connections/*.yaml     one data source per file, named by the file (<c>erp.yaml</c> is the data source <c>erp</c>)
/// </code>
/// Every file is read with the strict YAML reader, so a syntax error names the file and line. Keys are camelCase in the files and matched to the options
/// case-insensitively, so the environment variables and command line that override them are unchanged. The directory is watched: a change is read again
/// and a file that no longer parses keeps the last good data (the failure is passed to <see cref="ProxyConfigLoadGuard"/> for the options monitor to log).
/// </summary>
public sealed class ProxyConfigDirectorySource(string directory) : IConfigurationSource
{
    public string Directory { get; } = Path.GetFullPath(directory);

    /// <summary>Set by <see cref="ProxyConfigLoadGuard.Install"/>; receives the message of a reload that failed.</summary>
    public ProxyConfigLoadGuard? Guard { get; set; }

    public IConfigurationProvider Build(IConfigurationBuilder builder) => new ProxyConfigDirectoryProvider(this);
}

public sealed class ProxyConfigDirectoryProvider : ConfigurationProvider, IDisposable
{
    private readonly ProxyConfigDirectorySource _source;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public ProxyConfigDirectoryProvider(ProxyConfigDirectorySource source)
    {
        _source = source;
        if (System.IO.Directory.Exists(source.Directory))
        {
            _watcher = new FileSystemWatcher(source.Directory) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.DirectoryName };
            _debounce = new Timer(_ => ReloadNow(), null, Timeout.Infinite, Timeout.Infinite);
            void Changed(object? s, FileSystemEventArgs e) => _debounce.Change(250, Timeout.Infinite);
            _watcher.Changed += Changed;
            _watcher.Created += Changed;
            _watcher.Deleted += Changed;
            _watcher.Renamed += (s, e) => Changed(s, e);
            _watcher.EnableRaisingEvents = true;
        }
    }

    /// <summary>The file and position of every flattened key, for the origin shown in diagnostics.</summary>
    public IReadOnlyDictionary<string, ProxyKeyOrigin> Origins { get; private set; } = new Dictionary<string, ProxyKeyOrigin>(StringComparer.OrdinalIgnoreCase);

    public override void Load()
    {
        var (data, origins) = Read(_source.Directory);
        Data = data;
        Origins = origins;
    }

    /// <summary>Reads the directory again now and tells the options monitor; the file watcher calls this after a change settles.</summary>
    public void ReloadNow()
    {
        try
        {
            var (data, origins) = Read(_source.Directory);
            Data = data;
            Origins = origins;
        }
        catch (InvalidDataException ex)
        {
            _source.Guard?.Report(ex.Message);
        }
        OnReload();
    }

    /// <summary>Reads every file of the layout. Throws one <see cref="InvalidDataException"/> listing every problem, each with file and line.</summary>
    internal static (Dictionary<string, string?> Data, Dictionary<string, ProxyKeyOrigin> Origins) Read(string directory)
    {
        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var origins = new Dictionary<string, ProxyKeyOrigin>(StringComparer.OrdinalIgnoreCase);
        var diags = new List<ConfigDiagnostic>();
        string section = ProxyOptions.SectionName;

        void Add(string path, string prefix, Func<YamlMapping, YamlNode?>? select = null)
        {
            if (!File.Exists(path)) return;
            string text;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();
            var before = diags.Count;
            var root = StrictYamlReader.Read(text, path, diags, ProxyConfig.Catalog);
            if (diags.Count > before && diags.Skip(before).Any(d => d.Severity == ConfigSeverity.Error)) return;
            if (root is not YamlMapping map)
            {
                if (root is not null) diags.Add(ProxyConfig.Catalog.Create(SharedCodes.InvalidValue, path, root.Line, root.Column, "The file must be a mapping of keys to values."));
                return;
            }
            if (select is not null)
            {
                foreach (var extra in map.Entries.Where(e => e.Key.Value != "users"))
                    diags.Add(ProxyConfig.Catalog.Create(SharedCodes.UnknownKey, path, extra.Key.Line, extra.Key.Column, $"`{extra.Key.Value}` is not a key of this file; it holds only `users`.", keyPath: extra.Key.Value));
            }
            Flatten(select is null ? map : select(map) ?? new YamlMapping([], 0, 0), prefix, path, data, origins);
        }

        Add(Path.Combine(directory, ProxyConfigFile.ServerFileName), section);
        Add(Path.Combine(directory, "proxy", "auth.yaml"), $"{section}:Auth");
        Add(Path.Combine(directory, "proxy", "users.yaml"), $"{section}:Users", map => map.Get("users"));

        var connections = Path.Combine(directory, "connections");
        if (System.IO.Directory.Exists(connections))
            foreach (var file in System.IO.Directory.EnumerateFiles(connections, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
                Add(file, $"{section}:DataSources:{Path.GetFileNameWithoutExtension(file)}");

        if (diags.HasErrors()) throw new InvalidDataException(ConfigException.Format(diags));
        return (data, origins);
    }

    private static void Flatten(YamlNode node, string prefix, string file, Dictionary<string, string?> data, Dictionary<string, ProxyKeyOrigin> origins)
    {
        switch (node)
        {
            case YamlScalar s:
                data[prefix] = !s.Quoted && s.Value.Length == 0 ? null : s.Value;
                if (origins.TryGetValue(prefix, out var own)) origins[prefix] = own with { ValueLine = s.Line, ValueColumn = s.Column };
                else origins[prefix] = new ProxyKeyOrigin(file, s.Line, s.Column, s.Line, s.Column);
                break;
            case YamlSequence q:
                for (var i = 0; i < q.Items.Count; i++) Flatten(q.Items[i], $"{prefix}:{i}", file, data, origins);
                break;
            case YamlMapping m:
                foreach (var e in m.Entries)
                {
                    var key = $"{prefix}:{e.Key.Value}";
                    origins[key] = new ProxyKeyOrigin(file, e.Key.Line, e.Key.Column, e.Value.Line, e.Value.Column);
                    Flatten(e.Value, key, file, data, origins);
                }
                break;
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}

/// <summary>Where a flattened configuration key was written: the file, and the position of the key and of its value.</summary>
public sealed record ProxyKeyOrigin(string File, int KeyLine, int KeyColumn, int ValueLine, int ValueColumn);
