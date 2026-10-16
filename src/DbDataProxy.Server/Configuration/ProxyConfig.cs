using System.Text;
using DbDataKit.Config;
using DbDataKit.Config.Hosting;
using DbDataKit.Config.Yaml;
using DbDataKit.Secrets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Primitives;

namespace DbDataProxy.Server.Configuration;

/// <summary>Raised when the effective configuration is not valid. The message lists every problem with its key path and source, never a secret value.</summary>
public sealed class ProxyConfigException(string message, IReadOnlyList<ConfigDiagnostic> diagnostics) : Exception(message)
{
    /// <summary>Process exit code for an invalid configuration (decision D3: stable and documented).</summary>
    public const int ExitCode = 2;

    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>Options for <see cref="ProxyConfig.Load"/>.</summary>
public sealed class ProxyConfigLoadOptions
{
    /// <summary>Downgrades unknown keys to warnings for this load (the <c>Proxy:AllowUnknownKeys</c> setting does the same from configuration).</summary>
    public bool AllowUnknownKeys { get; init; }
}

/// <summary>The result of reading, binding and validating the "Proxy" section.</summary>
public sealed class ProxyConfigLoad(ProxyOptions? value, IReadOnlyList<ConfigDiagnostic> diagnostics, ConfigResult<ProxyOptions> detail)
{
    public ProxyOptions? Value { get; } = value;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;

    /// <summary>The shared result type, for <see cref="ShowOrigin"/>.</summary>
    public ConfigResult<ProxyOptions> Detail { get; } = detail;

    public bool Ok => Value is not null && !Diagnostics.HasErrors();

    public ProxyOptions ValueOrThrow() => Ok
        ? Value!
        : throw new ProxyConfigException(ProxyConfig.Format(Diagnostics, includeWarnings: false), Diagnostics);
}

/// <summary>
/// Strict loading of the "Proxy" configuration section (plan 09, C4). The stock configuration system (appsettings.json, environment variables,
/// command line) stays the source of values, so every existing file and override keeps working; the effective values are then bound with the shared
/// strict <see cref="ConfigBinder"/> (unknown keys with a suggestion, typed errors with the key path) and checked by <see cref="ProxyConfigValidator"/>.
/// Keys are matched case-insensitively and written PascalCase, as before.
/// </summary>
public static class ProxyConfig
{
    public const string Kind = "dbdataproxy";

    /// <summary>The schema version this build writes and understands. Must equal the highest migration target (a test checks it).</summary>
    public const int LatestSchemaVersion = 2;

    /// <summary>Product diagnostic codes: the shared <c>DBD-</c> codes under the <c>DBP-</c> prefix (DBP-104 is an unknown key).</summary>
    public static readonly IDiagnosticCatalog Catalog = new PrefixedCatalog("DBP-");

    /// <summary>Migrations of the configuration schema. 1 to 2 changes defaults only (see <see cref="ProxyConfigMigrations"/>).</summary>
    public static MigrationEngine Migrations { get; } = ProxyConfigMigrations.CreateEngine();

    /// <summary>
    /// Binds and validates the section. Never throws for a bad configuration; check <see cref="ProxyConfigLoad.Ok"/>.
    /// </summary>
    public static ProxyConfigLoad Load(IConfigurationRoot configuration, ProxyConfigLoadOptions? options = null, string section = ProxyOptions.SectionName)
    {
        var schema = SchemaModel.ForObject<ProxyOptions>();
        var diags = new List<ConfigDiagnostic>();
        var tree = new TreeBuilder(configuration, diags);
        var root = tree.Build(configuration.GetSection(section), schema);
        var merged = new MergedYaml(root, tree.FileOf);

        ConfigResult<ProxyOptions> Result(ProxyOptions? value, IReadOnlyDictionary<string, SourceInfo>? sources = null) =>
            new(value, diags, merged, sources ?? new Dictionary<string, SourceInfo>(), schema);

        // Version first: a file from a newer release is refused with an upgrade message instead of a pile of unknown keys.
        int version = 1;
        if (root.Get(schema.VersionKey) is YamlScalar v)
        {
            if (!int.TryParse(v.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out version) || version < 1)
            {
                diags.Add(Catalog.Create(SharedCodes.SchemaVersionInvalid, tree.FileOf.GetValueOrDefault(v, ""), v.Line, v.Column,
                    $"`SchemaVersion` is `{v.Value}`; it must be a positive whole number.", keyPath: schema.VersionKey));
                return new ProxyConfigLoad(null, diags, Result(null));
            }
            if (version > LatestSchemaVersion)
            {
                diags.Add(Catalog.Create(SharedCodes.SchemaVersionNewer, tree.FileOf.GetValueOrDefault(v, ""), v.Line, v.Column,
                    $"`SchemaVersion` is {version}, but this version of DbDataProxy understands up to {LatestSchemaVersion}.",
                    "Upgrade DbDataProxy; this file was written by a newer release.", schema.VersionKey));
                return new ProxyConfigLoad(null, diags, Result(null));
            }
        }

        var bound = new ConfigBinder(Catalog, tree.FileOf).Bind<ProxyOptions>(root, section);
        var value = bound.Value;
        diags.AddRange(bound.Diagnostics);

        if (value is not null)
        {
            value.SchemaVersion = version;
            // The binder replaces collections; keep the case-insensitive data source lookup the rest of the server relies on.
            value.DataSources = new Dictionary<string, DataSourceOptions>(value.DataSources, StringComparer.OrdinalIgnoreCase);

            // Schema 2 changes defaults only: a source without its own Classifier is strict. Files without SchemaVersion keep warn.
            var implicitClassifier = value.DataSources.Keys.Where(name => !bound.Sources.ContainsKey($"DataSources.{name}.Classifier")).ToList();
            if (version >= 2)
            {
                foreach (var name in implicitClassifier) value.DataSources[name].Classifier = ClassifierMode.Strict;
                implicitClassifier.Clear();
            }

            if (value.AllowUnknownKeys || options?.AllowUnknownKeys == true)
            {
                for (int i = 0; i < diags.Count; i++)
                    if (diags[i].Code == Catalog.Create(SharedCodes.UnknownKey, "", 0, 0, "").Code)
                        diags[i] = diags[i] with { Severity = ConfigSeverity.Warning, Fix = "Unknown keys are allowed by Proxy:AllowUnknownKeys; remove the key to silence this warning." };
            }

            if (!diags.HasErrors())
                diags.AddRange(ConfigValidationContext.Run(value, bound.Sources, section, [new ProxyConfigValidator(implicitClassifier.Where(n => !value.DataSources[n].AllowWrites).ToList())], Catalog));
        }

        return new ProxyConfigLoad(value, diags, Result(value, bound.Sources));
    }

    /// <summary>Loads and throws <see cref="ProxyConfigException"/> when the configuration is not valid.</summary>
    public static ProxyConfigLoad LoadOrThrow(IConfigurationRoot configuration, ProxyConfigLoadOptions? options = null)
    {
        var load = Load(configuration, options);
        if (!load.Ok) throw new ProxyConfigException(Format(load.Diagnostics, includeWarnings: false), load.Diagnostics);
        return load;
    }

    /// <summary>One line per problem: <c>error DBP-104 Proxy:Listen:Prot (appsettings.json:7:15): message (how to fix)</c>. Contains keys and positions, never values of secrets.</summary>
    public static string Format(IEnumerable<ConfigDiagnostic> diagnostics, bool includeWarnings = true)
    {
        var list = diagnostics.Where(d => includeWarnings || d.Severity == ConfigSeverity.Error).ToList();
        var sb = new StringBuilder();
        int errors = list.Count(d => d.Severity == ConfigSeverity.Error);
        sb.Append(errors > 0 ? $"The configuration is not valid ({errors} error(s)):" : "Configuration warnings:");
        foreach (var d in list)
        {
            sb.Append("\n  ").Append(d.Severity.ToString().ToLowerInvariant()).Append(' ').Append(d.Code);
            if (d.KeyPath is { Length: > 0 }) sb.Append(' ').Append(DisplayKey(d.KeyPath));
            if (d.Path.Length > 0) sb.Append(" (").Append(d.Line > 0 ? $"{d.Path}:{d.Line}:{d.Column}" : d.Path).Append(')');
            sb.Append(": ").Append(d.Message);
            if (d.Fix is { Length: > 0 }) sb.Append(" (").Append(d.Fix).Append(')');
        }
        return sb.ToString();
    }

    /// <summary>The environment-style key for a dotted path: <c>DataSources.erp.Provider</c> becomes <c>Proxy:DataSources:erp:Provider</c>.</summary>
    public static string DisplayKey(string keyPath) =>
        ProxyOptions.SectionName + ":" + keyPath.Replace("].", ":").Replace('[', ':').Replace("]", "").Replace('.', ':');

    /// <summary>
    /// The effective values with their sources for <c>config show</c>. Secrets never appear: a secret reference is shown as the reference, anything
    /// else in a secret key is masked, and connection strings keep their keys with literal credential values masked.
    /// </summary>
    public static IReadOnlyList<OriginEntry> Show(ProxyConfigLoad load)
    {
        var raw = load.Detail.Merged.Origins().ToDictionary(o => o.Path, o => o.Value, StringComparer.Ordinal);
        var entries = new List<OriginEntry>();
        foreach (var e in ShowOrigin.From(load.Detail))
        {
            string last = e.Path[(e.Path.LastIndexOf('.') + 1)..];
            if (e.Value is "True" or "False") entries.Add(e with { Value = e.Value.ToLowerInvariant() });
            else if (e.Secret && raw.TryGetValue(e.Path, out var secretText) && SecretRef.TryParse(secretText, out var reference))
                entries.Add(e with { Value = reference.ToAuditString() });
            else if (!e.Secret && last == nameof(DataSourceOptions.ConnectionString))
                entries.Add(e with { Value = MaskConnectionString(e.Value), Secret = false });
            else
                entries.Add(e);
        }
        if (!entries.Any(e => e.Path == "SchemaVersion"))
            entries.Add(new OriginEntry("SchemaVersion", load.Value!.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), "default", OriginKind.Default, 0, false));
        return entries.OrderBy(e => e.Path, StringComparer.Ordinal).ToList();
    }

    /// <summary>Masks the literal credential values (<c>Password=...</c>) of a connection string; references (<c>env:NAME</c>) stay visible.</summary>
    public static string MaskConnectionString(string connectionString)
    {
        if (ConnectionStringReferences.IsWholeReference(connectionString, out var whole)) return whole!.ToAuditString();
        // The shared Redactor would also mask `Password=env:NAME` (it masks every credential key); a reference is not a secret, so mask only literal values.
        string masked = connectionString;
        foreach (var credential in ConnectionStringReferences.CredentialValues(connectionString))
            foreach (var opening in new[] { "=", "={", "=\"", "='" })
                masked = masked.Replace(opening + credential, opening + Redactor.Replacement, StringComparison.Ordinal);
        return masked;
    }

    /// <summary>
    /// Turns the effective configuration section into the node tree the shared binder reads. Keys are matched to the schema ignoring case (so
    /// <c>listen:port</c> from an environment variable meets <c>Listen:Port</c> from the file); JSON booleans (<c>True</c> in the stock provider) are
    /// written lowercase; keys with a null value are treated as absent. Every node remembers its source (file with line, env variable, command line).
    /// </summary>
    private sealed class TreeBuilder(IConfigurationRoot configuration, List<ConfigDiagnostic> diags)
    {
        public Dictionary<YamlNode, string> FileOf { get; } = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<string, FileIndex?> _files = new(StringComparer.OrdinalIgnoreCase);

        public YamlMapping Build(IConfigurationSection section, ObjectSchema schema)
        {
            var node = (YamlMapping?)Node(section, schema, "", isRoot: true)?.Node ?? new YamlMapping([], 0, 0);
            if (!FileOf.ContainsKey(node)) FileOf[node] = "configuration";
            return node;
        }

        private readonly record struct Built(YamlNode Node, Origin Origin);

        private Built? Node(IConfigurationSection section, TypeSchema? schema, string path, bool isRoot = false)
        {
            var children = section.GetChildren().ToList();
            if (children.Count == 0)
            {
                if (section.Value is null) return null; // JSON null or an empty container: same as absent
                if (section.Value.Length == 0 && schema is ListSchema or MapSchema or ObjectSchema) return null; // `[]` reads as an empty string
                var origin = OriginOf(section.Path);
                var (_, _, line, col) = origin.Position(section.Path);
                var scalar = new YamlScalar(CanonicalScalar(schema, section.Value), line, col);
                FileOf[scalar] = origin.Label;
                return new Built(scalar, origin);
            }

            bool asList = schema switch
            {
                ListSchema => true,
                ScalarSchema or ObjectSchema or MapSchema => false,
                _ => children.All(c => int.TryParse(c.Key, out _)),
            };
            if (asList && children.All(c => int.TryParse(c.Key, out var n) && n >= 0))
            {
                var items = new List<YamlNode>();
                Origin? first = null;
                foreach (var child in children.OrderBy(c => int.Parse(c.Key)))
                {
                    var built = Node(child, (schema as ListSchema)?.Item, path + "[" + child.Key + "]");
                    if (built is null) continue;
                    first ??= built.Value.Origin;
                    items.Add(built.Value.Node);
                }
                if (items.Count == 0) return null;
                var (_, _, l, c0) = first!.Value.Position(children[0].Path);
                var seq = new YamlSequence(items, l, c0);
                FileOf[seq] = first.Value.Label;
                return new Built(seq, first.Value);
            }

            var entries = new List<YamlEntry>();
            Origin? firstOrigin = null;
            foreach (var child in children)
            {
                var (name, childSchema) = Canonical(schema, child.Key, isRoot);
                var built = Node(child, childSchema, path.Length == 0 ? name : path + "." + name);
                if (built is null) continue;
                firstOrigin ??= built.Value.Origin;
                var (kl, kc, _, _) = built.Value.Origin.Position(child.Path);
                var key = new YamlScalar(name, kl, kc);
                FileOf[key] = built.Value.Origin.Label;
                entries.Add(new YamlEntry(key, built.Value.Node));
            }
            if (entries.Count == 0) return null;
            var (_, _, ml, mc) = firstOrigin!.Value.Position(children[0].Path);
            var map = new YamlMapping(entries, ml, mc);
            FileOf[map] = firstOrigin.Value.Label;
            return new Built(map, firstOrigin.Value);
        }

        private static (string Name, TypeSchema? Schema) Canonical(TypeSchema? parent, string key, bool isRoot)
        {
            switch (parent)
            {
                case ObjectSchema o:
                    if (o.Properties.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) is { } p) return (p.Key, p.Type);
                    if (isRoot && o.HasVersion && string.Equals(o.VersionKey, key, StringComparison.OrdinalIgnoreCase)) return (o.VersionKey, null);
                    return (key, null);
                case MapSchema m:
                    return (key, m.Value);
                default:
                    return (key, null);
            }
        }

        private static string CanonicalScalar(TypeSchema? schema, string value) =>
            schema is ScalarSchema { Underlying: var t } && t == typeof(bool) && bool.TryParse(value.Trim(), out var b) ? (b ? "true" : "false") : value;

        // ---- origins ------------------------------------------------------------------------------------------------------------------

        /// <summary>Where a value came from: a label for display and, for JSON files, an index for key and value positions.</summary>
        private readonly record struct Origin(string Label, FileIndex? File)
        {
            public (int KeyLine, int KeyColumn, int ValueLine, int ValueColumn) Position(string path) =>
                File is not null && File.Find(path) is { } p ? p : (0, 0, 0, 0);
        }

        private Origin OriginOf(string key)
        {
            foreach (var provider in configuration.Providers.Reverse())
            {
                if (!provider.TryGet(key, out var value)) continue;
                switch (provider)
                {
                    case FileConfigurationProvider file:
                        return FileOrigin(file);
                    case EnvironmentVariablesConfigurationProvider:
                        return new Origin("env:" + EnvironmentVariableName(key, value) , null);
                    case MemoryConfigurationProvider:
                        return new Origin("memory", null);
                    default:
                        var name = provider.GetType().Name;
                        return new Origin(name.Contains("CommandLine", StringComparison.Ordinal) ? "cli:args" : name, null);
                }
            }
            return new Origin("configuration", null);
        }

        private static string EnvironmentVariableName(string key, string? value)
        {
            // The stock provider hides the prefix; find the variable by its two usual spellings (the DBPROXY_ prefix is added last, so it wins).
            string flat = key.Replace(":", "__");
            foreach (var candidate in new[] { "DBPROXY_" + flat, flat })
            {
                foreach (System.Collections.DictionaryEntry e in Environment.GetEnvironmentVariables())
                    if (e.Key is string name && string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase) && string.Equals(e.Value as string, value, StringComparison.Ordinal))
                        return name;
            }
            return flat;
        }

        private Origin FileOrigin(FileConfigurationProvider provider)
        {
            provider.Source.ResolveFileProvider();
            var info = provider.Source.FileProvider?.GetFileInfo(provider.Source.Path ?? "");
            string path = info?.PhysicalPath ?? provider.Source.Path ?? "configuration";
            if (!_files.TryGetValue(path, out var index))
            {
                index = path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && info is { Exists: true } ? FileIndex.Read(info, path, diags) : null;
                _files[path] = index;
            }
            return new Origin(path, index);
        }
    }

    /// <summary>Key and value positions of every path in one JSON file (via the shared JSON input, so comments and trailing commas are accepted). Syntax errors become diagnostics.</summary>
    private sealed class FileIndex
    {
        private readonly Dictionary<string, (int, int, int, int)> _paths = new(StringComparer.OrdinalIgnoreCase);

        public static FileIndex? Read(Microsoft.Extensions.FileProviders.IFileInfo info, string path, List<ConfigDiagnostic> diags)
        {
            string text;
            try
            {
                using var stream = info.CreateReadStream();
                using var reader = new StreamReader(stream);
                text = reader.ReadToEnd();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diags.Add(Catalog.Create(SharedCodes.FileNotFound, path, 0, 0, $"The file could not be read: {ex.Message}"));
                return null;
            }
            var local = new List<ConfigDiagnostic>();
            var root = JsonInput.Read(text, path, local, Catalog);
            diags.AddRange(local);
            if (root is null || local.HasErrors()) return null;
            var index = new FileIndex();
            index.Walk(root, "");
            return index;
        }

        public (int, int, int, int)? Find(string path) => _paths.TryGetValue(path, out var p) ? p : null;

        private void Walk(YamlNode node, string path)
        {
            switch (node)
            {
                case YamlMapping m:
                    foreach (var e in m.Entries)
                    {
                        var child = path.Length == 0 ? e.Key.Value : path + ":" + e.Key.Value;
                        _paths[child] = (e.Key.Line, e.Key.Column, e.Value.Line, e.Value.Column);
                        Walk(e.Value, child);
                    }
                    break;
                case YamlSequence q:
                    for (int i = 0; i < q.Items.Count; i++)
                    {
                        var child = path + ":" + i;
                        _paths[child] = (q.Items[i].Line, q.Items[i].Column, q.Items[i].Line, q.Items[i].Column);
                        Walk(q.Items[i], child);
                    }
                    break;
            }
        }
    }
}

/// <summary>
/// Keeps a configuration file that fails to parse during a reload from crashing the process: the stock provider rethrows on a change-token thread unless
/// <c>OnLoadException</c> handles it. The guard swallows the exception, remembers it, and <see cref="ProxyOptionsMonitor"/> then keeps the last good configuration.
/// </summary>
public sealed class ProxyConfigLoadGuard
{
    private string? _failure;

    public static ProxyConfigLoadGuard Install(IConfigurationBuilder builder)
    {
        var guard = new ProxyConfigLoadGuard();
        foreach (var source in builder.Sources.OfType<FileConfigurationSource>())
        {
            var previous = source.OnLoadException;
            source.OnLoadException = context =>
            {
                guard._failure = string.Join(" ", Chain(context.Exception).Distinct());
                context.Ignore = true;
                previous?.Invoke(context);
            };
        }
        return guard;
    }

    /// <summary>The failure recorded since the last call (and clears it), or null.</summary>
    public string? TakeFailure() => Interlocked.Exchange(ref _failure, null);

    private static IEnumerable<string> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException) yield return ex.Message;
    }
}

/// <summary>
/// The <see cref="IOptionsMonitor{T}"/> behind every <c>ProxyOptions</c> consumer. The first value is validated at startup (the host refuses to start
/// otherwise). A reload that fails validation is logged and the last good configuration stays in effect: a running service never crashes on a bad edit.
/// </summary>
public sealed class ProxyOptionsMonitor : Microsoft.Extensions.Options.IOptionsMonitor<ProxyOptions>, IDisposable
{
    private readonly IConfigurationRoot _configuration;
    private readonly ILogger _log;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly List<Action<ProxyOptions, string?>> _listeners = [];
    private volatile ProxyOptions _current;

    private readonly ProxyConfigLoadGuard? _guard;

    public ProxyOptionsMonitor(IConfigurationRoot configuration, ProxyOptions initial, ILogger log, ProxyConfigLoadGuard? guard = null)
    {
        _guard = guard;
        _configuration = configuration;
        _current = initial;
        _log = log;
        _subscription = ChangeToken.OnChange(configuration.GetReloadToken, Reload);
    }

    public ProxyOptions CurrentValue => _current;

    public ProxyOptions Get(string? name) => _current;

    public IDisposable OnChange(Action<ProxyOptions, string?> listener)
    {
        lock (_gate) _listeners.Add(listener);
        return new Registration(this, listener);
    }

    private void Reload()
    {
        try
        {
            // A file the stock provider failed to parse leaves it empty; that must not look like a valid, empty configuration.
            if (_guard?.TakeFailure() is { } failure)
            {
                _log.LogError("The changed configuration file could not be read and the last good configuration stays in effect. {Details}", failure);
                return;
            }
            var load = ProxyConfig.Load(_configuration);
            if (!load.Ok)
            {
                _log.LogError("The changed configuration was rejected and the last good configuration stays in effect. {Details}", ProxyConfig.Format(load.Diagnostics, includeWarnings: false));
                return;
            }
            _current = load.Value!;
            if (load.Diagnostics.Count > 0) _log.LogWarning("{Details}", ProxyConfig.Format(load.Diagnostics));
            _log.LogInformation("Configuration reloaded.");
            Action<ProxyOptions, string?>[] listeners;
            lock (_gate) listeners = [.. _listeners];
            foreach (var listener in listeners)
            {
                try { listener(_current, null); }
                catch (Exception ex) { _log.LogError(ex, "A configuration change listener failed."); }
            }
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "The changed configuration could not be read and the last good configuration stays in effect.");
        }
    }

    public void Dispose() => _subscription.Dispose();

    private sealed class Registration(ProxyOptionsMonitor owner, Action<ProxyOptions, string?> listener) : IDisposable
    {
        public void Dispose()
        {
            lock (owner._gate) owner._listeners.Remove(listener);
        }
    }
}
