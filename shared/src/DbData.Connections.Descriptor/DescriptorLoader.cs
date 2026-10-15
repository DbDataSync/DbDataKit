using System.Data.Common;
using DbData.Config;
using DbData.Config.Yaml;
using DbData.Dialect;

namespace DbData.Connections.Descriptor;

/// <summary>Turns a descriptor's <c>library</c> into a provider factory.</summary>
public interface IProviderFactoryResolver
{
    DbProviderFactory? Resolve(string library);
}

public sealed class DictionaryFactoryResolver(IReadOnlyDictionary<string, DbProviderFactory> factories) : IProviderFactoryResolver
{
    private readonly Dictionary<string, DbProviderFactory> _map = new(factories, StringComparer.OrdinalIgnoreCase);
    public DbProviderFactory? Resolve(string library) => _map.GetValueOrDefault(library);
}

/// <summary>Resolves an ADO.NET invariant name registered with <see cref="DbProviderFactories"/>.</summary>
public sealed class RegisteredFactoryResolver : IProviderFactoryResolver
{
    public DbProviderFactory? Resolve(string library) => DbProviderFactories.TryGetFactory(library, out var f) ? f : null;
}

public sealed class DescriptorLoadResult(EngineDescriptor? descriptor, IReadOnlyList<ConfigDiagnostic> diagnostics)
{
    public EngineDescriptor? Descriptor { get; } = descriptor;
    public IReadOnlyList<ConfigDiagnostic> Diagnostics { get; } = diagnostics;
    public bool Ok => Descriptor is not null && !Diagnostics.HasErrors();
}

/// <summary>Strict loader for <c>engine/1</c> descriptors plus the policy checks that keep catalog YAML from weakening a connection.</summary>
public static class DescriptorLoader
{
    public const string BadSchema = "DBD-ENG-001";
    public const string BadFixedProperty = "DBD-ENG-002";
    public const string UnknownDialect = "DBD-ENG-003";
    public const string BadOverridable = "DBD-ENG-004";
    public const string BadKeys = "DBD-ENG-005";
    public const string UnknownLibrary = "DBD-ENG-006";

    /// <summary>Fixed properties that name <em>which</em> driver to use are the point of the feature; every other denied key is refused.</summary>
    private static readonly HashSet<string> AllowedIdentityKeys = new(StringComparer.Ordinal) { "driver", "dsn" };

    public static DescriptorLoadResult Load(string yaml, string file = "engine.yaml", DialectCatalog? dialects = null, IProviderFactoryResolver? resolver = null)
    {
        var diags = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(yaml, file, diags);
        if (diags.HasErrors() || root is null) return new(null, diags);
        if (root is YamlMapping map)
        {
            var schema = map.Get("schema");
            if (schema is not YamlScalar { Value: EngineDescriptor.SchemaId })
            {
                var at = (YamlNode?)schema ?? map;
                diags.Add(new ConfigDiagnostic(BadSchema, ConfigSeverity.Error, file, at.Line, at.Column, "`schema` must be `engine/1`.", "Add `schema: engine/1`."));
                return new(null, diags);
            }
        }
        var bound = new ConfigBinder().Bind<EngineDescriptor>(root, file);
        diags.AddRange(bound.Diagnostics);
        if (bound.Value is null || diags.HasErrors()) return new(null, diags);

        Validate(bound.Value, file, bound.Sources, dialects ?? DialectCatalog.Builtin, resolver, diags);
        return new(bound.Value, diags);
    }

    private static void Validate(EngineDescriptor d, string file, IReadOnlyDictionary<string, SourceInfo> src, DialectCatalog dialects, IProviderFactoryResolver? resolver, List<ConfigDiagnostic> diags)
    {
        void Err(string code, string path, string message, string? fix = null)
        {
            var (line, col) = src.TryGetValue(path + "#key", out var k) ? (k.Line, k.Column) : src.TryGetValue(path, out var s) ? (s.Line, s.Column) : (0, 0);
            diags.Add(new ConfigDiagnostic(code, ConfigSeverity.Error, file, line, col, message, fix) { KeyPath = path });
        }

        if (!dialects.TryGet(new EngineId(d.Dialect), out _))
            Err(UnknownDialect, "dialect", $"The dialect `{d.Dialect}` is not known.", $"Known: {string.Join(", ", dialects.Ids)}. Add a dialect.yaml for new ones.");
        if (resolver is not null && resolver.Resolve(d.Library) is null)
            Err(UnknownLibrary, "library", $"The library `{d.Library}` did not resolve to a provider factory.");

        foreach (var (key, value) in d.FixedProperties)
        {
            string norm = CredentialPolicy.NormalizeKey(key);
            if (!AllowedIdentityKeys.Contains(norm) && CredentialPolicy.IsDeniedProperty(key))
                Err(BadFixedProperty, "fixed_properties." + key, $"`{key}` cannot be a fixed property: credential, host, identity, authentication and TLS keys belong to the connection, not to catalog YAML.",
                    "Use connection address, auth and tls fields. TLS trust is never set by a descriptor.");
            if (value.Any(char.IsControl)) Err(BadFixedProperty, "fixed_properties." + key, $"The value of `{key}` has control characters.");
        }

        for (int i = 0; i < d.OverridableProperties.Count; i++)
        {
            string name = d.OverridableProperties[i];
            if (!d.FixedProperties.Keys.Any(k => string.Equals(CredentialPolicy.NormalizeKey(k), CredentialPolicy.NormalizeKey(name), StringComparison.Ordinal)))
                Err(BadOverridable, $"overridable_properties[{i}]", $"`{name}` is not one of the fixed properties.");
            else if (AllowedIdentityKeys.Contains(CredentialPolicy.NormalizeKey(name)))
                Err(BadOverridable, $"overridable_properties[{i}]", $"`{name}` names the driver and cannot be made overridable.");
        }

        var k2 = d.ConnectionStringKeys;
        foreach (var (name, value) in new[] { ("host", k2.Host), ("database", k2.Database), ("username", k2.Username), ("password", k2.Password), ("connect_timeout", k2.ConnectTimeout) })
            if (string.IsNullOrWhiteSpace(value)) Err(BadKeys, "connection_string_keys." + name, $"`{name}` needs a key spelling.");
        foreach (var (name, value) in new[] { ("host", k2.Host), ("port", k2.Port), ("database", k2.Database), ("username", k2.Username), ("password", k2.Password), ("connect_timeout", k2.ConnectTimeout), ("command_timeout", k2.CommandTimeout), ("integrated_security", k2.IntegratedSecurity), ("encrypt_key", k2.EncryptKey), ("trust_key", k2.TrustKey) })
            if (value.AsSpan().IndexOfAny(";={}\0\r\n") >= 0) Err(BadKeys, "connection_string_keys." + name, $"`{name}` is not a plain key name.");

        foreach (var (key, _) in d.ReadOnly.Keys)
            if (key.AsSpan().IndexOfAny(";={}\0\r\n") >= 0) Err(BadKeys, "read_only.keys." + key, "Not a plain key name.");
    }
}
