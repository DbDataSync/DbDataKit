using DbDataKit.Dialect;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// DbDataBuild reaches the shared DbDataKit libraries through <c>DbDataKitReference</c> (Directory.Build.targets), the way Proxy and
/// Sync do. This project references DbDataKit.Dialect.Abstractions that way, and these tests fail to build if the reference breaks.
/// </summary>
public sealed class SharedKitReferenceTests
{
    [Fact]
    public void ASharedLibraryTypeIsUsable()
    {
        Assert.True(new EngineVersion(2, 0).CompareTo(new EngineVersion(1, 5, 6)) > 0);
        Assert.Equal(new EngineVersion(10), new EngineVersion(10, 0, 0));
    }
}
