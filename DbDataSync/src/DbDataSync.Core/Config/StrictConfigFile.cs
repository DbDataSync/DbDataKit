using System.Collections;
using System.Reflection;
using DbDataKit.Config.Yaml;
using YamlDotNet.Serialization.NamingConventions;

namespace DbDataSync.Core.Config;

/// <summary>A configuration file that cannot be read as written: a syntax error, a repeated key, or a key the file kind does not have. The message names the file, the position and the key's path.</summary>
public sealed class ConfigFileException(string file, IReadOnlyList<ConfigDiagnostic> diagnostics)
    : InvalidOperationException($"{Path.GetFileName(file)} cannot be read:{Environment.NewLine}{string.Join(Environment.NewLine, diagnostics.Select(d => "  " + d))}")
{
    public string File { get; } = file;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
}

/// <summary>
/// The check a configuration file passes before it is bound to its type: read with the shared strict reader (syntax, repeated keys, no anchors or tags), then every mapping compared with the
/// properties of the type it will bind to, so an unknown key is an error with its path instead of being dropped. Plan 13, A01.
/// </summary>
internal static class StrictConfigFile
{
    private static readonly YamlDotNet.Serialization.INamingConvention Naming = CamelCaseNamingConvention.Instance;

    public static void Check<T>(string text, string file)
    {
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text, file, diagnostics);
        if (root is not null && diagnostics.Count == 0) Walk(root, typeof(T), "", file, diagnostics);
        if (diagnostics.Count > 0) throw new ConfigFileException(file, diagnostics);
    }

    /// <summary>Checks the file, then binds it; a value of the wrong shape (a word where a number goes) is reported the same way, with its position.</summary>
    public static T Load<T>(string text, string file, Func<string, T> bind)
    {
        Check<T>(text, file);
        try { return bind(text); }
        catch (YamlDotNet.Core.YamlException ex)
        {
            throw new ConfigFileException(file, [DefaultDiagnosticCatalog.Instance.Create(SharedCodes.InvalidValue, file, (int)ex.Start.Line, (int)ex.Start.Column, ex.InnerException?.Message ?? ex.Message)]);
        }
    }

    private static void Walk(YamlNode node, Type type, string path, string file, List<ConfigDiagnostic> diagnostics)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string) || type.IsPrimitive || type.IsEnum || type.IsAbstract || type.IsInterface || type == typeof(object)) return;

        if (TryDictionaryValue(type, out var valueType))
        {
            if (node is YamlMapping dict)
                foreach (var e in dict.Entries) Walk(e.Value, valueType, Join(path, e.Key.Value), file, diagnostics);
            return;
        }
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type))
        {
            var itemType = type.IsArray ? type.GetElementType()! : type.IsGenericType ? type.GetGenericArguments()[0] : typeof(object);
            if (node is YamlSequence seq)
                for (var i = 0; i < seq.Items.Count; i++) Walk(seq.Items[i], itemType, $"{path}[{i}]", file, diagnostics);
            return;
        }
        if (node is not YamlMapping map || !type.IsClass) return;

        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanWrite && p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<YamlDotNet.Serialization.YamlIgnoreAttribute>() is null)
            .ToDictionary(p => p.GetCustomAttribute<YamlDotNet.Serialization.YamlMemberAttribute>()?.Alias ?? Naming.Apply(p.Name), p => p, StringComparer.Ordinal);
        foreach (var entry in map.Entries)
        {
            var keyPath = Join(path, entry.Key.Value);
            if (!properties.TryGetValue(entry.Key.Value, out var property))
            {
                var near = DidYouMean.Closest(entry.Key.Value, properties.Keys);
                diagnostics.Add(DefaultDiagnosticCatalog.Instance.Create(SharedCodes.UnknownKey, file, entry.Key.Line, entry.Key.Column,
                    $"`{keyPath}` is not a setting here.{(near is null ? "" : $" Did you mean `{Join(path, near)}`?")}", keyPath: keyPath));
                continue;
            }
            Walk(entry.Value, property.PropertyType, keyPath, file, diagnostics);
        }
    }

    private static string Join(string path, string key) => path.Length == 0 ? key : $"{path}.{key}";

    private static bool TryDictionaryValue(Type type, out Type valueType)
    {
        foreach (var t in type.GetInterfaces().Append(type))
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IDictionary<,>)) { valueType = t.GetGenericArguments()[1]; return true; }
        valueType = typeof(object);
        return false;
    }
}
