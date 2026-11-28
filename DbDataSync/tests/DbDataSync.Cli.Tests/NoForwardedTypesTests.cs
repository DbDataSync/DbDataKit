using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DbDataSync.Cli.Tests;

/// <summary>Sync's assemblies hold their own types only: the shared libraries are referenced by name (<c>DbDataKit.Sync.*</c>), and no assembly of
/// Sync forwards a type to one.</summary>
public sealed class NoForwardedTypesTests
{
    [Fact]
    public void No_assembly_of_DbDataSync_forwards_a_type()
    {
        var assemblies = Directory.GetFiles(AppContext.BaseDirectory, "DbDataSync.*.dll");
        Assert.Contains(assemblies, a => Path.GetFileName(a) == "DbDataSync.TaskRunner.dll");

        var forwards = new List<string>();
        foreach (var path in assemblies)
        {
            using var pe = new PEReader(File.OpenRead(path));
            if (!pe.HasMetadata) continue;
            var reader = pe.GetMetadataReader();
            foreach (var handle in reader.ExportedTypes)
            {
                var type = reader.GetExportedType(handle);
                if (type.IsForwarder)
                    forwards.Add($"{Path.GetFileName(path)}: {reader.GetString(type.Namespace)}.{reader.GetString(type.Name)}");
            }
        }

        Assert.Empty(forwards);
    }
}
