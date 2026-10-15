using System.Reflection;
using DbData.Config.Yaml;
using Microsoft.Extensions.Configuration;

namespace DbData.Config.Hosting;

/// <summary>Where a flattened configuration key was read from.</summary>
public sealed record KeyOrigin(string File, int Line, int Column);

/// <summary>
/// Configuration provider that reads YAML with the strict reader. Any diagnostic makes the load throw one <see cref="InvalidDataException"/> that lists all of them
/// with file and line. With a schema type the document is also bound strictly (unknown keys, types, ranges, required keys).
/// </summary>
public sealed class DbDataYamlConfigurationProvider : ConfigurationProvider, IDisposable
{
    private readonly string _path;
    private readonly bool _optional;
    private readonly Type? _schema;
    private readonly IDiagnosticCatalog? _catalog;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;

    public DbDataYamlConfigurationProvider(string path, bool optional, Type? schema, bool reloadOnChange, IDiagnosticCatalog? catalog = null)
    {
        _path = Path.GetFullPath(path);
        _optional = optional;
        _schema = schema;
        _catalog = catalog;
        if (reloadOnChange && Path.GetDirectoryName(_path) is { } dir && Directory.Exists(dir))
        {
            _watcher = new FileSystemWatcher(dir, Path.GetFileName(_path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            _debounce = new Timer(_ => { try { Load(); } catch (InvalidDataException) { /* keep the previous good data; the next valid edit reloads */ } OnReload(); }, null, Timeout.Infinite, Timeout.Infinite);
            _watcher.Changed += (_, _) => _debounce.Change(250, Timeout.Infinite);
            _watcher.Created += (_, _) => _debounce.Change(250, Timeout.Infinite);
            _watcher.Renamed += (_, _) => _debounce.Change(250, Timeout.Infinite);
            _watcher.EnableRaisingEvents = true;
        }
    }

    public IReadOnlyDictionary<string, KeyOrigin> Origins { get; private set; } = new Dictionary<string, KeyOrigin>(StringComparer.OrdinalIgnoreCase);

    public override void Load()
    {
        if (!File.Exists(_path))
        {
            if (_optional) { Data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase); return; }
            throw new FileNotFoundException($"Configuration file `{_path}` was not found.", _path);
        }
        string text;
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sr = new StreamReader(fs)) text = sr.ReadToEnd();

        var diags = new List<ConfigDiagnostic>();
        var root = JsonInput.ReadByExtension(text, _path, diags, _catalog);
        if (_schema != null && !diags.HasErrors())
        {
            var bind = typeof(ConfigBinder).GetMethod(nameof(ConfigBinder.Bind))!.MakeGenericMethod(_schema);
            var result = bind.Invoke(new ConfigBinder(_catalog), [root, _path])!;
            diags.AddRange((IEnumerable<ConfigDiagnostic>)result.GetType().GetProperty("Diagnostics")!.GetValue(result)!);
        }
        if (diags.HasErrors()) throw new InvalidDataException(ConfigException.Format(diags));

        var data = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var origins = new Dictionary<string, KeyOrigin>(StringComparer.OrdinalIgnoreCase);
        if (root != null) Flatten(root, "", data, origins);
        Data = data;
        Origins = origins;
    }

    private void Flatten(YamlNode node, string prefix, Dictionary<string, string?> data, Dictionary<string, KeyOrigin> origins)
    {
        switch (node)
        {
            case YamlScalar s:
                if (prefix.Length == 0) return;
                data[prefix] = !s.Quoted && s.Value.Length == 0 ? null : s.Value;
                origins[prefix] = new KeyOrigin(_path, s.Line, s.Column);
                break;
            case YamlSequence q:
                for (var i = 0; i < q.Items.Count; i++) Flatten(q.Items[i], prefix.Length == 0 ? i.ToString() : $"{prefix}:{i}", data, origins);
                break;
            case YamlMapping m:
                foreach (var e in m.Entries) Flatten(e.Value, prefix.Length == 0 ? e.Key.Value : $"{prefix}:{e.Key.Value}", data, origins);
                break;
        }
    }

    public void Dispose() { _watcher?.Dispose(); _debounce?.Dispose(); }
}

public sealed class DbDataYamlConfigurationSource(string path, bool optional, Type? schema, bool reloadOnChange, IDiagnosticCatalog? catalog) : IConfigurationSource
{
    public IConfigurationProvider Build(IConfigurationBuilder builder) => new DbDataYamlConfigurationProvider(path, optional, schema, reloadOnChange, catalog);
}

public static class DbDataConfigurationExtensions
{
    /// <summary>Adds a YAML file read with the strict reader. Pass <paramref name="schema"/> to also validate it against a schema type (unknown keys are errors).</summary>
    public static IConfigurationBuilder AddDbDataYaml(this IConfigurationBuilder builder, string path, Type? schema = null, bool optional = false, bool reloadOnChange = false, IDiagnosticCatalog? catalog = null) =>
        builder.Add(new DbDataYamlConfigurationSource(path, optional, schema, reloadOnChange, catalog));

    public static IConfigurationBuilder AddDbDataYaml<T>(this IConfigurationBuilder builder, string path, bool optional = false, bool reloadOnChange = false, IDiagnosticCatalog? catalog = null) where T : class =>
        builder.AddDbDataYaml(path, typeof(T), optional, reloadOnChange, catalog);

    /// <summary>Origins (file and line) of every key any DbData YAML provider in the root loaded; later providers win.</summary>
    public static IReadOnlyDictionary<string, KeyOrigin> GetOrigins(this IConfiguration configuration)
    {
        var result = new Dictionary<string, KeyOrigin>(StringComparer.OrdinalIgnoreCase);
        if (configuration is IConfigurationRoot root)
            foreach (var p in root.Providers.OfType<DbDataYamlConfigurationProvider>())
                foreach (var (k, v) in p.Origins) result[k] = v;
        return result;
    }
}
