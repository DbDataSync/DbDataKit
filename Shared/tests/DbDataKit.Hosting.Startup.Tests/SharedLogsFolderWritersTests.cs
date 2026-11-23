using DbDataKit.Security.Acl;

namespace DbDataKit.Hosting.Startup.Tests;

/// <summary>
/// The shared logs folder lets every service of the suite make its own folder in it, whichever service made it first and whichever services were
/// registered then: each service's account is named by the SID Windows gives <c>NT SERVICE\&lt;name&gt;</c>, which needs no registration to be known.
/// </summary>
public sealed class SharedLogsFolderWritersTests
{
    [Fact]
    public void Every_suite_service_is_a_writer_by_its_computed_SID_and_this_process_too()
    {
        var writers = SharedLogsFolder.DefaultWriters();

        Assert.Equal([.. SuiteServices.All.Select(s => WindowsServiceAccounts.VirtualAccountSid(s.WindowsName)), "CurrentUser"], writers);
        Assert.All(writers.Take(4), w => Assert.StartsWith("S-1-5-80-", w, StringComparison.Ordinal));
        // the SID Windows gives NT SERVICE\TrustedInstaller, as a check of the computation
        Assert.Equal("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464", WindowsServiceAccounts.VirtualAccountSid("TrustedInstaller"));
    }
}
