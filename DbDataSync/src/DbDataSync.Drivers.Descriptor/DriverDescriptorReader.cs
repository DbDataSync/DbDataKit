using System.Reflection;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Drivers.Descriptor;
using DbDataKit.Sync.Drivers.Generic;
using DbDataSync.Libraries;

namespace DbDataSync.Drivers.Descriptor;

/// <summary>Parses <c>driver.yaml</c> and stands up the <see cref="IDriver"/> it describes. Reading a descriptor is <see cref="DriverDescriptors"/>, a
/// shared library, so a tool that reads only a descriptor's dialect, type map and capabilities loads no library; standing the driver up is here, beside
/// the library registry that holds its provider.</summary>
public static class DriverDescriptorReader
{
    public static DriverDescriptorYaml Read(string path) => DriverDescriptors.Read(path);

    public static DriverDescriptorYaml Deserialize(string yaml) => DriverDescriptors.Deserialize(yaml);

    /// <summary>
    /// Phase 168V. <see cref="DriverDescriptorYaml.Base"/> omitted (the common case) builds a
    /// <see cref="GenericDriver"/> the existing way, through <paramref name="libraries"/> and a
    /// <see cref="System.Data.Common.DbProviderFactory"/> — every pre-phase-168V <c>driver.yaml</c> keeps
    /// working unchanged. A named <c>base</c> is resolved via reflection to a public static
    /// <c>FromDescriptor(DriverDescriptorYaml, string)</c> on the type it names — the same
    /// assembly-qualified-name shape <c>library.json</c>'s own <c>factoryType</c> already uses — so a
    /// third kind never needs this method to change, only a new <c>driver.yaml</c> naming it.
    /// <para>
    /// Phase 169V: the second parameter is <paramref name="repoRoot"/> — added to the convention because
    /// <c>JdbcGenericDriver.FromDescriptor</c> needs it to resolve <c>jdbc.driverJarPaths</c> against
    /// <c>&lt;repo&gt;/files/</c> (<c>DbDataSync.Libraries.FilesPaths</c>). No back-compat cost: it is
    /// still the only <c>base</c> kind that exists, so this changes one call site
    /// (<c>JdbcGenericDriver.FromDescriptor</c>'s own signature), not a convention already in use by two
    /// different implementations that would now disagree.
    /// </para>
    /// </summary>
    public static IDriver BuildDriver(DriverDescriptorYaml descriptor, LibraryRegistry libraries, string repoRoot)
    {
        if (descriptor.Base is null)
            return new GenericDriver(DriverDescriptors.ToSpec(descriptor, libraries.GetFactory(descriptor.Library)));

        var type = Type.GetType(descriptor.Base)
            ?? throw new NotSupportedException(
                $"Driver '{descriptor.Id}': base '{descriptor.Base}' could not be resolved to a type. " +
                "Use an assembly-qualified name, e.g. 'DbDataSync.Drivers.Jdbc.JdbcGenericDriver, DbDataSync.Drivers.Jdbc'.");

        var method = type.GetMethod(
                "FromDescriptor", BindingFlags.Public | BindingFlags.Static, [typeof(DriverDescriptorYaml), typeof(string)])
            ?? throw new NotSupportedException(
                $"Driver '{descriptor.Id}': base '{descriptor.Base}' has no public static " +
                "FromDescriptor(DriverDescriptorYaml, string) method.");

        return (IDriver)method.Invoke(null, [descriptor, repoRoot])!;
    }

    /// <inheritdoc cref="DriverDescriptors.ToSpec"/>
    public static GenericDriverSpec ToSpec(DriverDescriptorYaml descriptor, System.Data.Common.DbProviderFactory factory) =>
        DriverDescriptors.ToSpec(descriptor, factory);
}
