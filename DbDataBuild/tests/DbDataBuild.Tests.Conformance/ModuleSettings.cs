using System.Runtime.CompilerServices;

namespace DbDataBuild.Tests.Conformance;

/// <summary>The test engines open their own Npgsql connections before the tool does, so the tool's driver settings are applied when this assembly loads.</summary>
#pragma warning disable CA2255   // a test assembly is application code for this purpose
internal static class ModuleSettings
{
    [ModuleInitializer]
    internal static void Apply() => DbDataBuild.Execution.DriverSettings.Apply();
}
