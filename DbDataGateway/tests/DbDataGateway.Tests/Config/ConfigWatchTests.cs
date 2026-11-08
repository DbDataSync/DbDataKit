using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Tests.Config;

/// <summary>
/// The configuration directory is watched, and on Linux each watcher is one of the 128 inotify instances a user has: a provider whose first read fails must not have started one (nothing can dispose it), and a disposed
/// configuration lets go of its own. Run apart from the rest, which open and close watchers of their own.
/// </summary>
[Collection(ProcessStateCollection.Name)]
public sealed class ConfigWatchTests
{
    /// <summary>The inotify instances this process holds. Hosts that earlier tests disposed can still be giving theirs up (a finalizer closes the handle), so it settles them first: a count taken in the middle of that drifts by one.</summary>
    private static int SettledWatches()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        return Watches();
    }

    private static int Watches() => new DirectoryInfo("/proc/self/fd").GetFileSystemInfos().Count(f => f.LinkTarget?.Contains("inotify", StringComparison.Ordinal) == true);

    [Fact]
    public void A_directory_that_does_not_parse_and_a_disposed_configuration_leave_no_watch_behind()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var broken = TempConfig.Create(("server.yaml", "listen: [not, a, mapping\n"));
        using var good = TempConfig.Create(("server.yaml", "listen: { address: 127.0.0.1 }\n"));
        var before = SettledWatches();
        for (var i = 0; i < 20; i++)
        {
            Assert.Throws<InvalidDataException>(() => new ConfigurationBuilder().Add(new GatewayConfigDirectorySource(broken.Directory)).Build());
            Assert.True(ConfigTestSupport.LoadDirectory(good.Directory).Ok);
        }
        var after = SettledWatches();
        Assert.True(after <= before, $"{after - before} inotify instances were left behind by 40 loads.");

        // and a configuration that is in use does watch
        using var root = (ConfigurationRoot)new ConfigurationBuilder().Add(new GatewayConfigDirectorySource(good.Directory)).Build();
        Assert.Equal(after + 1, Watches());
    }
}
