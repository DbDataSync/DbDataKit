namespace DbDataKit.Apps.Host.Tests;

/// <summary>The path the apps host is reached under: the host's own routes are <c>/apps/...</c>, and a browser may use another prefix (<c>apps.path</c>).</summary>
public sealed class AppsPathsTests
{
    [Theory]
    [InlineData("/programs", "/programs/acme/", "/apps/acme/")]
    [InlineData("/programs", "/programs", "/apps")]
    [InlineData("/data/apps", "/data/apps/-/callback", "/apps/-/callback")]
    [InlineData("/apps", "/apps/acme/", "/apps/acme/")]
    public void A_path_under_the_prefix_is_the_hosts_own_path(string prefix, string browser, string own) =>
        Assert.Equal(own, AppsPaths.ToInternal(prefix, browser));

    [Theory]
    [InlineData("/programs", "/apps/acme")]
    [InlineData("/programs", "/programsx/acme")]
    [InlineData("/programs", "/")]
    public void A_path_outside_the_prefix_is_not_the_hosts(string prefix, string browser) =>
        Assert.Null(AppsPaths.ToInternal(prefix, browser));

    [Fact]
    public async Task The_prefix_of_a_request_is_what_the_hosts_addresses_use_and_does_not_leak_to_another_request()
    {
        Assert.Equal("/apps/", AppSite.BasePath);
        await Task.Run(() =>
        {
            AppsPaths.Use("/programs");
            Assert.Equal("/programs/", AppSite.BasePath);
            Assert.Equal("/programs/acme/?launch", AppsEndpoints.LaunchUrl("acme"));
            Assert.Equal("/programs/acme/", AppsPaths.ToPublic("/apps/acme/"));
            Assert.Equal("/other", AppsPaths.ToPublic("/other"));
        });
        Assert.Equal("/apps/", AppSite.BasePath);
    }
}
