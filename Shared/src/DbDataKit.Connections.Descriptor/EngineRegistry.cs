using System.Reflection;
using DbDataKit.Config.Yaml;
using DbDataKit.Dialect;

namespace DbDataKit.Connections.Descriptor;

/// <summary>The registry's documents did not load: each diagnostic names its file and line.</summary>
public sealed class EngineRegistryException(IReadOnlyList<ConfigDiagnostic> diagnostics)
    : Exception("The engine registry is invalid: " + string.Join("; ", diagnostics.Select(d => d.ToString())))
{
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// The suite's one list of engines (plan 14, R5): an <c>engine/1</c> descriptor for each, with the dialect, the provider and the catalog
/// reader that serve it and how far it is supported in each role (<see cref="EngineRoles"/>). The built-in list is embedded
/// (<c>engines/*.engine.yaml</c>); the support tables in the documentation are generated from it (<see cref="EngineSupportTable"/>), so a claim about
/// engine support is a fact in a file that a test checks.
/// </summary>
public sealed class EngineRegistry
{
    private static readonly Lazy<EngineRegistry> BuiltinLazy = new(LoadBuiltin);
    private readonly Dictionary<string, EngineDescriptor> _byName = new(StringComparer.OrdinalIgnoreCase);

    private EngineRegistry(IReadOnlyList<EngineDescriptor> engines)
    {
        Engines = engines;
        foreach (var e in engines)
        {
            _byName[e.Id] = e;
            foreach (var alias in e.Aliases) _byName[alias] = e;
        }
    }

    /// <summary>The engines the suite ships with.</summary>
    public static EngineRegistry Builtin => BuiltinLazy.Value;

    /// <summary>Every engine, by id.</summary>
    public IReadOnlyList<EngineDescriptor> Engines { get; }

    /// <summary>The engine with this id or alias (<c>odbc:name</c> is <c>odbc</c>), or null.</summary>
    public EngineDescriptor? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        id = id.Trim();
        if (_byName.TryGetValue(id, out var found)) return found;
        int colon = id.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && _byName.TryGetValue(id[..colon], out found) ? found : null;
    }

    /// <summary>The engines supported in the role at least as far as <paramref name="atLeast"/> (<see cref="EngineSupport.Unverified"/> and <see cref="EngineSupport.Supported"/> count as built).</summary>
    public IEnumerable<EngineDescriptor> Supporting(string role, EngineSupport atLeast = EngineSupport.Unverified) =>
        Engines.Where(e => e.Role(role).Status >= atLeast);

    /// <summary>
    /// Reads registry documents (<c>name</c>, <c>yaml</c>) and checks them as a set: every one is a valid descriptor, ids and aliases are unique,
    /// and <paramref name="dialects"/> (the built-in catalog by default) has a descriptor for each of its dialects and no descriptor names a dialect it lacks.
    /// </summary>
    public static EngineRegistry Load(IEnumerable<(string File, string Yaml)> documents, DialectCatalog? dialects = null)
    {
        dialects ??= DialectCatalog.Builtin;
        var diagnostics = new List<ConfigDiagnostic>();
        var engines = new List<(EngineDescriptor Engine, string File)>();
        foreach (var (file, yaml) in documents)
        {
            var result = DescriptorLoader.Load(yaml, file, dialects);
            diagnostics.AddRange(result.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error));
            if (result.Ok) engines.Add((result.Descriptor!, file));
        }

        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (engine, file) in engines)
            foreach (var name in engine.Aliases.Prepend(engine.Id))
                if (!names.TryAdd(name, file))
                    diagnostics.Add(new ConfigDiagnostic("DBD-ENG-009", ConfigSeverity.Error, file, 0, 0, $"`{name}` is also the id or an alias of an engine in {names[name]}.", "An id or alias names one engine."));
        foreach (var dialect in dialects.Ids)
            if (engines.All(e => !string.Equals(e.Engine.Id, dialect, StringComparison.OrdinalIgnoreCase)))
                diagnostics.Add(new ConfigDiagnostic("DBD-ENG-010", ConfigSeverity.Error, "engines", 0, 0, $"The dialect `{dialect}` has no descriptor.", $"Add engines/{dialect}.engine.yaml."));

        if (diagnostics.Count > 0) throw new EngineRegistryException(diagnostics);
        return new EngineRegistry(engines.Select(e => e.Engine).OrderBy(e => e.Id, StringComparer.Ordinal).ToList());
    }

    private static EngineRegistry LoadBuiltin()
    {
        var assembly = typeof(EngineRegistry).Assembly;
        var documents = new List<(string, string)>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.EndsWith(".engine.yaml", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream);
            documents.Add((resource["DbDataKit.Connections.Descriptor.".Length..], reader.ReadToEnd()));
        }
        return Load(documents);
    }
}
