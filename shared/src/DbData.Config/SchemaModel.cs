using System.Collections.Concurrent;
using System.Reflection;
using DbData.Config.Yaml;

namespace DbData.Config;

/// <summary>Reflection-built description of a config type. One model drives the binder, the JSON Schema and the origin report, so they cannot disagree.</summary>
public abstract class TypeSchema
{
    public required Type ClrType { get; init; }
}

public sealed class ScalarSchema : TypeSchema
{
    public bool IsNullable { get; init; }
    public Type Underlying { get; init; } = typeof(string);
    public bool IsEnum => Underlying.IsEnum;
}

public sealed class ListSchema : TypeSchema
{
    public required TypeSchema Item { get; init; }
}

public sealed class MapSchema : TypeSchema
{
    public required TypeSchema Value { get; init; }
}

public sealed class UnionSchema : TypeSchema
{
    public required string Discriminator { get; init; }
    public required IReadOnlyDictionary<string, ObjectSchema> Cases { get; init; }
}

public sealed class PropertySchema
{
    public required string Key { get; init; }
    public required PropertyInfo Property { get; init; }
    public required TypeSchema Type { get; set; }
    public bool Required { get; init; }
    public object? Default { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public string? Pattern { get; init; }
    public string? Description { get; init; }
    public bool Secret { get; init; }
    public string? DeprecatedMessage { get; init; }
    public string? DeprecatedSince { get; init; }
}

public sealed class ObjectSchema : TypeSchema
{
    public string? Kind { get; init; }
    public int Version { get; init; } = 1;
    public string VersionKey { get; init; } = "schemaVersion";
    public bool HasVersion { get; init; }
    public UnknownKeyPolicy UnknownKeys { get; init; }
    public string? Description { get; init; }
    public PropertyInfo? Extra { get; init; }
    public List<PropertySchema> Properties { get; } = [];

    public PropertySchema? Find(string key) => Properties.FirstOrDefault(p => p.Key == key);

    /// <summary>Keys the binder accepts at this level (excluding a union discriminator).</summary>
    public IEnumerable<string> AllowedKeys(bool includeVersionKey) =>
        includeVersionKey && HasVersion ? Properties.Select(p => p.Key).Append(VersionKey).Concat(AcceptsHeader ? [SchemaHeader.Key] : []) : Properties.Select(p => p.Key);

    /// <summary>True when the <c>schema: kind/int</c> header form is accepted at the top level (no clashing property or version key).</summary>
    public bool AcceptsHeader => HasVersion && VersionKey != SchemaHeader.Key && Find(SchemaHeader.Key) == null;
}

public static class SchemaModel
{
    private static readonly ConcurrentDictionary<Type, TypeSchema> Cache = new();

    public static ObjectSchema ForObject(Type type) => (ObjectSchema)For(type);
    public static ObjectSchema ForObject<T>() => ForObject(typeof(T));

    public static TypeSchema For(Type type)
    {
        if (Cache.TryGetValue(type, out var hit)) return hit;
        lock (Cache)
        {
            if (Cache.TryGetValue(type, out hit)) return hit;
            return Build(type);
        }
    }

    private static TypeSchema Build(Type type)
    {
        var under = Nullable.GetUnderlyingType(type);
        if (IsScalar(under ?? type))
            return Cache[type] = new ScalarSchema { ClrType = type, Underlying = under ?? type, IsNullable = under != null || !(under ?? type).IsValueType };

        if (TryDictionary(type, out var valueType))
            return Cache[type] = new MapSchema { ClrType = type, Value = For(valueType) };

        if (TryList(type, out var itemType))
            return Cache[type] = new ListSchema { ClrType = type, Item = For(itemType) };

        var union = type.GetCustomAttribute<ConfigUnionAttribute>(inherit: false);
        if (union != null)
        {
            var cases = new Dictionary<string, ObjectSchema>(StringComparer.Ordinal);
            var schema = new UnionSchema { ClrType = type, Discriminator = union.Discriminator, Cases = cases };
            Cache[type] = schema;
            foreach (var c in type.GetCustomAttributes<ConfigUnionCaseAttribute>(inherit: false))
                cases[c.Name] = (ObjectSchema)For(c.Type);
            return schema;
        }

        if (type.IsAbstract || type.IsInterface)
            throw new InvalidOperationException($"{type} is abstract; declare it with [ConfigUnion] and [ConfigUnionCase].");
        if (type.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, Type.EmptyTypes) == null && !type.IsValueType)
            throw new InvalidOperationException($"{type} needs a parameterless constructor (settable or init properties) to be a config type.");

        var attr = type.GetCustomAttribute<ConfigSchemaAttribute>(inherit: false);
        var naming = attr?.Naming ?? KeyNaming.CamelCase;
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.GetCustomAttribute<ConfigIgnoreAttribute>() == null).ToList();
        var extra = props.FirstOrDefault(p => p.GetCustomAttribute<ConfigExtraAttribute>() != null);
        var obj = new ObjectSchema
        {
            ClrType = type, Kind = attr?.Kind, Version = attr?.Version ?? 1, HasVersion = attr != null,
            VersionKey = attr?.VersionKey ?? "schemaVersion", UnknownKeys = attr?.UnknownKeys ?? UnknownKeyPolicy.Strict,
            Description = attr?.Description, Extra = extra,
        };
        Cache[type] = obj;
        foreach (var p in props)
        {
            if (p == extra || !p.CanWrite || p.SetMethod is null || !p.SetMethod.IsPublic) continue;
            var key = p.GetCustomAttribute<ConfigKeyAttribute>();
            var dep = p.GetCustomAttribute<DeprecatedAttribute>();
            obj.Properties.Add(new PropertySchema
            {
                Key = key?.Name ?? ApplyNaming(p.Name, naming),
                Property = p,
                Type = For(p.PropertyType),
                Required = key?.Required ?? false,
                Default = key?.Default,
                Min = key != null && !double.IsNaN(key.Min) ? key.Min : null,
                Max = key != null && !double.IsNaN(key.Max) ? key.Max : null,
                Pattern = key?.Pattern,
                Description = key?.Description,
                Secret = p.GetCustomAttribute<SecretAttribute>() != null,
                DeprecatedMessage = dep?.Message,
                DeprecatedSince = dep?.Since,
            });
        }
        var dup = obj.Properties.GroupBy(p => p.Key).FirstOrDefault(g => g.Count() > 1);
        if (dup != null) throw new InvalidOperationException($"{type} declares the key `{dup.Key}` twice.");
        return obj;
    }

    public static string ApplyNaming(string name, KeyNaming naming) => naming switch
    {
        KeyNaming.Verbatim => name,
        KeyNaming.CamelCase => name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..],
        _ => string.Concat(name.Select((c, i) => char.IsUpper(c) ? (i > 0 ? "_" : "") + char.ToLowerInvariant(c) : c.ToString())),
    };

    internal static bool IsScalar(Type t) =>
        t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(decimal) || t == typeof(TimeSpan) || t == typeof(Guid) || t == typeof(Uri) || t == typeof(DateTimeOffset) || t == typeof(DateTime);

    private static bool TryDictionary(Type type, out Type valueType)
    {
        valueType = null!;
        foreach (var t in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (!t.IsGenericType) continue;
            var d = t.GetGenericTypeDefinition();
            if ((d == typeof(IDictionary<,>) || d == typeof(IReadOnlyDictionary<,>) || d == typeof(Dictionary<,>)) && t.GetGenericArguments()[0] == typeof(string))
            { valueType = t.GetGenericArguments()[1]; return true; }
        }
        return false;
    }

    private static bool TryList(Type type, out Type itemType)
    {
        itemType = null!;
        if (type == typeof(string)) return false;
        if (type.IsArray) { itemType = type.GetElementType()!; return true; }
        foreach (var t in new[] { type }.Concat(type.GetInterfaces()))
        {
            if (!t.IsGenericType) continue;
            var d = t.GetGenericTypeDefinition();
            if (d == typeof(IList<>) || d == typeof(IReadOnlyList<>) || d == typeof(List<>) || d == typeof(IEnumerable<>) || d == typeof(ICollection<>) || d == typeof(IReadOnlyCollection<>))
            { itemType = t.GetGenericArguments()[0]; return true; }
        }
        return false;
    }
}
