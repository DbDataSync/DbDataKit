using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Drivers.Generic;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace DbDataKit.Sync.Drivers.Descriptor;

/// <summary>Parses <c>driver.yaml</c> and turns it into the spec of a <see cref="GenericDriver"/>, with no library loaded: the dialect, the type map and the
/// capabilities a descriptor declares can be read without its provider. Standing the driver up (its provider factory from an installed library, or a
/// <c>base</c> type found by name) is the engine's, in <c>DriverDescriptorReader.BuildDriver</c>.</summary>
public static class DriverDescriptors
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .WithTypeConverter(new TypeMapEntryYamlConverter())
        .Build();

    public static DriverDescriptorYaml Read(string path) => Deserialize(File.ReadAllText(path));

    public static DriverDescriptorYaml Deserialize(string yaml) => Deserializer.Deserialize<DriverDescriptorYaml>(yaml);

    /// <summary>Everything a <see cref="GenericDriver"/> needs except the library's factory itself —
    /// separate from <c>DriverDescriptorReader.BuildDriver</c> so a caller that already has a resolved factory (or wants
    /// to inspect the spec before constructing) still can.</summary>
    public static GenericDriverSpec ToSpec(DriverDescriptorYaml descriptor, System.Data.Common.DbProviderFactory factory)
    {
        var dialect = new DescriptorDialect(descriptor.Dialect, descriptor.TypeMap);
        var catalog = DescriptorCatalogResolution.Resolve(
            descriptor.Id, descriptor.Dialect.Catalog, descriptor.MetadataQueries?.TableQuery,
            descriptor.MetadataQueries?.ColumnQuery, @default: new InformationSchemaQueries(dialect));

        var keys = descriptor.Dialect.ConnectionStringKeys is { } k
            ? new GenericConnectionStringKeys(k.Host, k.Port, k.Database, k.Username, k.Password, k.ConnectTimeout, k.IntegratedSecurity)
            : new GenericConnectionStringKeys();

        return new GenericDriverSpec(
            descriptor.Id,
            dialect,
            factory,
            catalog,
            Readers: descriptor.Capabilities.Readers,
            Staging: descriptor.Capabilities.Staging,
            Writers: descriptor.Capabilities.Writers,
            ConnectionStringKeys: keys,
            DefaultDatabase: descriptor.Dialect.DefaultDatabase,
            DefaultPort: descriptor.Dialect.DefaultPort,
            DisplayName: descriptor.DisplayName,
            TestQuery: descriptor.TestQuery,
            FixedConnectionStringProperties: descriptor.Dialect.FixedProperties);
    }
}
