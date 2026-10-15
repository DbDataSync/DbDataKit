namespace DbData.Config;

public enum UnknownKeyPolicy
{
    /// <summary>Unknown keys are errors, with a did-you-mean suggestion.</summary>
    Strict,
    /// <summary>Unknown keys are kept in the <see cref="ConfigExtraAttribute"/> bag (if the type has one) and are never errors.</summary>
    Preserve,
}

public enum KeyNaming
{
    /// <summary><c>ConnectTimeout</c> is <c>connectTimeout</c>.</summary>
    CamelCase,
    /// <summary><c>ConnectTimeout</c> is <c>connect_timeout</c>.</summary>
    SnakeCase,
    /// <summary>The property name as written (<c>ConnectTimeout</c>), for appsettings-style files.</summary>
    Verbatim,
}

/// <summary>Marks a class as a config object and sets its per-schema policies. Optional: undecorated classes get Strict, camelCase, version 1.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ConfigSchemaAttribute(string? kind = null) : Attribute
{
    public string? Kind { get; } = kind;
    public int Version { get; set; } = 1;
    public UnknownKeyPolicy UnknownKeys { get; set; } = UnknownKeyPolicy.Strict;
    public KeyNaming Naming { get; set; } = KeyNaming.CamelCase;
    /// <summary>Top-level key that carries the schema version.</summary>
    public string VersionKey { get; set; } = "schemaVersion";
    public string? Description { get; set; }
}

/// <summary>Renames a property's key and adds constraints. Properties without it use the schema's naming policy and are optional.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigKeyAttribute(string? name = null) : Attribute
{
    public string? Name { get; } = name;
    public bool Required { get; set; }
    /// <summary>Value used when the key is absent (otherwise the property initializer stays).</summary>
    public object? Default { get; set; }
    public double Min { get; set; } = double.NaN;
    public double Max { get; set; } = double.NaN;
    /// <summary>Regular expression a string value must match entirely.</summary>
    public string? Pattern { get; set; }
    public string? Description { get; set; }
}

/// <summary>The value is a secret: masked in <c>show --origin</c> output and marked in the JSON Schema.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class SecretAttribute : Attribute;

[AttributeUsage(AttributeTargets.Property)]
public sealed class DeprecatedAttribute(string message) : Attribute
{
    public string Message { get; } = message;
    public string? Since { get; set; }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigIgnoreAttribute : Attribute;

/// <summary>On a <c>Dictionary&lt;string, YamlNode&gt;</c> property: receives unknown keys under <see cref="UnknownKeyPolicy.Preserve"/>.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ConfigExtraAttribute : Attribute;

/// <summary>On an abstract base class: a polymorphic node chosen by an explicit discriminator key. Cases are declared with <see cref="ConfigUnionCaseAttribute"/>.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, Inherited = false)]
public sealed class ConfigUnionAttribute(string discriminator) : Attribute
{
    public string Discriminator { get; } = discriminator;
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public sealed class ConfigUnionCaseAttribute(string name, Type type) : Attribute
{
    public string Name { get; } = name;
    public Type Type { get; } = type;
}
