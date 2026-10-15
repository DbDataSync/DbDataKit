using System.Globalization;
using System.Text;
using DbData.Config.Yaml;

namespace DbData.Config;

public sealed class ConfigException(IReadOnlyList<ConfigDiagnostic> diagnostics) : Exception(Format(diagnostics))
{
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    public static string Format(IEnumerable<ConfigDiagnostic> diags)
    {
        var sb = new StringBuilder();
        var list = diags.ToList();
        sb.Append("Configuration is not valid (").Append(list.Count(d => d.Severity == ConfigSeverity.Error)).Append(" error(s)):");
        foreach (var d in list) sb.Append('\n').Append("  ").Append(d);
        return sb.ToString();
    }
}

public sealed class ConfigResult<T>(T? value, IReadOnlyList<ConfigDiagnostic> diagnostics, MergedYaml merged, IReadOnlyDictionary<string, SourceInfo> sources, ObjectSchema schema) where T : class
{
    public T? Value { get; } = value;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public MergedYaml Merged { get; } = merged;
    public IReadOnlyDictionary<string, SourceInfo> Sources { get; } = sources;
    public ObjectSchema Schema { get; } = schema;
    public bool Ok => Value != null && !Diagnostics.HasErrors();

    /// <returns>The value, or throws one <see cref="ConfigException"/> listing every diagnostic with its position.</returns>
    public T ValueOrThrow() => Ok ? Value! : throw new ConfigException(Diagnostics);
}

/// <summary>
/// Layers a typed config. Precedence, lowest to highest: built-in defaults, file layers in the order added (shared/folder layers first, the project file last),
/// environment, command line. File layers merge with the Build merge operators (<c>key=</c>, <c>key-</c>, <c>key+</c>); environment and command line override single values.
/// </summary>
public sealed class ConfigBuilder<T>(IDiagnosticCatalog? catalog = null) where T : class
{
    private readonly IDiagnosticCatalog _catalog = catalog ?? DefaultDiagnosticCatalog.Instance;
    private readonly List<(string Name, string? Text, bool Optional)> _layers = [];
    private readonly List<OverrideEntry> _env = [];
    private readonly List<OverrideEntry> _cli = [];
    private readonly List<IConfigValidator<T>> _validators = [];
    private string? _defaults;
    private string _defaultsName = "defaults";

    public ConfigBuilder<T> AddDefaults(string yaml, string name = "defaults") { _defaults = yaml; _defaultsName = name; return this; }
    public ConfigBuilder<T> AddText(string name, string yaml) { _layers.Add((name, yaml, false)); return this; }
    public ConfigBuilder<T> AddFile(string path, bool optional = false) { _layers.Add((path, null, optional)); return this; }
    public ConfigBuilder<T> AddEnvironment(string app, IEnumerable<EnvAlias>? legacy = null, IReadOnlyDictionary<string, string?>? env = null) { _env.AddRange(EnvironmentSource.Read(app, legacy, env)); return this; }
    public ConfigBuilder<T> AddCommandLine(IEnumerable<string> args) { _cli.AddRange(CommandLineSource.Read(args).Overrides); return this; }
    public ConfigBuilder<T> AddOverrides(IEnumerable<OverrideEntry> entries) { _cli.AddRange(entries); return this; }
    public ConfigBuilder<T> AddValidator(IConfigValidator<T> validator) { _validators.Add(validator); return this; }

    public ConfigResult<T> Build()
    {
        var schema = SchemaModel.ForObject<T>();
        var diags = new List<ConfigDiagnostic>();
        var layers = new List<YamlLayer>();
        void Load(string name, string text)
        {
            if (JsonInput.ReadByExtension(text, name, diags, _catalog) is not { } node) return;
            if (node is YamlMapping m) layers.Add(new YamlLayer(name, m));
            else diags.Add(_catalog.Create(SharedCodes.InvalidValue, name, node.Line, node.Column, "The document must be a mapping of keys to values."));
        }
        if (_defaults != null) Load(_defaultsName, _defaults);
        foreach (var (name, text, optional) in _layers)
        {
            if (text != null) { Load(name, text); continue; }
            if (!File.Exists(name))
            {
                if (!optional) diags.Add(_catalog.Create(SharedCodes.FileNotFound, name, 0, 0, $"File `{name}` was not found."));
                continue;
            }
            try { Load(name, File.ReadAllText(name)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { diags.Add(_catalog.Create(SharedCodes.FileNotFound, name, 0, 0, $"File `{name}` could not be read: {ex.Message}")); }
        }

        var layered = layers.SelectMany(l => l.Root.Entries).Select(e => StripOp(e.Key.Value)).ToHashSet(StringComparer.Ordinal);
        var merged = YamlMerge.Merge(layers, layered, diags, _catalog);
        merged = ConfigOverlay.Apply(merged, [.. _env, .. _cli], schema, diags, _catalog);

        // refuse files from the future before binding, so the message is the upgrade one and not a pile of unknown keys
        if (schema.HasVersion && merged.Root.Get(schema.VersionKey) is YamlScalar v)
        {
            if (!int.TryParse(v.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var ver) || ver < 1)
                diags.Add(_catalog.Create(SharedCodes.SchemaVersionInvalid, FileOf(merged, v), v.Line, v.Column, $"`{schema.VersionKey}` is `{v.Value}`; it must be a positive whole number."));
            else if (ver > schema.Version)
            {
                diags.Add(_catalog.Create(SharedCodes.SchemaVersionNewer, FileOf(merged, v), v.Line, v.Column, $"`{schema.VersionKey}` is {ver}, but this tool understands up to {schema.Version}.", "Upgrade the tool; this file was written by a newer version."));
                return new ConfigResult<T>(null, diags, merged, new Dictionary<string, SourceInfo>(), schema);
            }
        }

        if (schema.AcceptsHeader && merged.Root.Get(SchemaHeader.Key) is YamlScalar h)
        {
            if (!SchemaHeader.TryParse(h.Value, out var hk, out var hv) || (schema.Kind != null && hk != schema.Kind))
                diags.Add(_catalog.Create(SharedCodes.SchemaVersionInvalid, FileOf(merged, h), h.Line, h.Column, $"`schema` is `{h.Value}`; expected `{schema.Kind ?? "<kind>"}/<positive whole number>`."));
            else if (hv > schema.Version)
            {
                diags.Add(_catalog.Create(SharedCodes.SchemaVersionNewer, FileOf(merged, h), h.Line, h.Column, $"`schema` is {h.Value}, but this tool understands up to {schema.Version}.", "Upgrade the tool; this file was written by a newer version."));
                return new ConfigResult<T>(null, diags, merged, new Dictionary<string, SourceInfo>(), schema);
            }
        }

        var bound = new ConfigBinder(_catalog, merged.FileOf).Bind<T>(merged.Root, layers.Count > 0 ? layers[^1].File : "");
        diags.AddRange(bound.Diagnostics);
        if (bound.Value != null && !diags.HasErrors())
            diags.AddRange(ConfigValidationContext.Run(bound.Value, bound.Sources, layers.Count > 0 ? layers[^1].File : "", _validators, _catalog));
        return new ConfigResult<T>(bound.Value, diags, merged, bound.Sources, schema);
    }

    private static string FileOf(MergedYaml m, YamlNode n) => m.FileOf.GetValueOrDefault(n, "");

    private static string StripOp(string key) => key.Length > 1 && key[^1] is '=' or '-' or '+' ? key[..^1] : key;
}
