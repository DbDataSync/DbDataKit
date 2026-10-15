using DbData.Config.Yaml;

namespace DbData.Config;

public interface IConfigValidationContext
{
    /// <summary>Reports a problem at the node bound for <paramref name="path"/> (or its nearest bound parent).</summary>
    void Error(string path, string message, string? fix = null);
    void Warning(string path, string message, string? fix = null);
}

/// <summary>Cross-reference and semantic checks that need the whole bound document (for example: every user's data source exists).</summary>
public interface IConfigValidator<in T>
{
    void Validate(T value, IConfigValidationContext context);
}

public sealed class ConfigValidationContext(IReadOnlyDictionary<string, SourceInfo> sources, string file, IDiagnosticCatalog? catalog = null) : IConfigValidationContext
{
    private readonly IDiagnosticCatalog _catalog = catalog ?? DefaultDiagnosticCatalog.Instance;
    private readonly List<ConfigDiagnostic> _diags = [];
    public IReadOnlyList<ConfigDiagnostic> Diagnostics => _diags;

    public void Error(string path, string message, string? fix = null) => Add(ConfigSeverity.Error, path, message, fix);
    public void Warning(string path, string message, string? fix = null) => Add(ConfigSeverity.Warning, path, message, fix);

    private void Add(ConfigSeverity severity, string path, string message, string? fix)
    {
        var info = Locate(path);
        var d = _catalog.Create(SharedCodes.ValidationFailed, info?.File ?? file, info?.Line ?? 0, info?.Column ?? 0, message, fix, path) with { Severity = severity };
        _diags.Add(d);
    }

    /// <summary>The node for the path, else its nearest bound ancestor.</summary>
    public SourceInfo? Locate(string path)
    {
        var p = path;
        while (true)
        {
            if (sources.TryGetValue(p, out var s)) return s;
            var cut = Math.Max(p.LastIndexOf('.'), p.LastIndexOf('['));
            if (cut < 0) return sources.GetValueOrDefault("");
            p = p[..cut];
        }
    }

    public static IReadOnlyList<ConfigDiagnostic> Run<T>(T value, IReadOnlyDictionary<string, SourceInfo> sources, string file, IEnumerable<IConfigValidator<T>> validators, IDiagnosticCatalog? catalog = null)
    {
        var cx = new ConfigValidationContext(sources, file, catalog);
        foreach (var v in validators) v.Validate(value, cx);
        return cx.Diagnostics;
    }
}
