namespace DbDataKit.Update.Tests;

/// <summary>The suite's releases are CalVer <c>YYYY.M.D.HHmm</c> (plan 24a UC08), four numbers, which the semantic parser reads as well as three.</summary>
public class CalVerTests
{
    [Theory]
    [InlineData("2026.11.9.1430", "2026.11.9.1430", Channel.Stable)]
    [InlineData("v2026.11.9.1430-beta", "2026.11.9.1430-beta", Channel.Beta)]
    [InlineData("2026.11.9.1430-snapshot.g1a2b3c4", "2026.11.9.1430-snapshot.g1a2b3c4", Channel.Snapshot)]
    [InlineData("1.2.3", "1.2.3", Channel.Stable)]
    public void A_calendar_version_parses_and_keeps_its_channel(string text, string shown, Channel channel)
    {
        var v = ReleaseVersion.Parse(text);
        Assert.Equal(shown, v.ToString());
        Assert.Equal(channel, v.Channel);
    }

    [Fact]
    public void The_fourth_number_orders_two_releases_of_one_day()
    {
        Assert.True(ReleaseVersion.Parse("2026.11.9.1430") > ReleaseVersion.Parse("2026.11.9.900"));
        Assert.True(ReleaseVersion.Parse("2026.11.10.1") > ReleaseVersion.Parse("2026.11.9.2359"));
        Assert.True(ReleaseVersion.Parse("2026.11.9.1430-beta") < ReleaseVersion.Parse("2026.11.9.1430"));
        Assert.Equal(ReleaseVersion.Parse("1.2.3"), ReleaseVersion.Parse("1.2.3.0"));
    }

    [Fact]
    public void A_manifest_for_a_calendar_version_validates()
    {
        var m = new ReleaseManifest { Product = "dbdatagateway", Version = "2026.11.9.1430", Channel = "stable" };
        m.Validate();
        Assert.Equal("2026.11.9.1430", ReleaseManifest.Parse(System.Text.Encoding.UTF8.GetBytes(m.ToJson())).Version);
    }
}
