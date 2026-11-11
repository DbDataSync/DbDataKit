using DbDataKit.Server;
using DbDataKit.Web.Trust;

namespace DbDataKit.Server.Tests;

/// <summary>The rename DbDataProxy to DbDataGateway is a clean break (plan 20, O14): the old product name in an announcement is refused with a message naming both.</summary>
public sealed class RenamedProductTests
{
    private static Announcement Announced(string product) => new(product, "abc-123", "edge", 8816, null, "https://edge.example:8816", "ABCDE-FGHIJ", DateTimeOffset.UtcNow.AddMinutes(5));

    [Fact]
    public void An_announcement_as_the_old_product_is_refused_with_both_names()
    {
        var path = Path.Combine(Path.GetTempPath(), "ddk-renamed-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        var pending = new PendingInstances(path);
        var why = pending.Announce(Announced("dbdataproxy"), null);
        Assert.NotNull(why);
        Assert.Contains("DbDataProxy is now DbDataGateway", why);
        Assert.Contains("dbdatagateway", why);
        Assert.Null(pending.Announce(Announced("dbdatagateway"), null));
        File.Delete(path);
    }

    [Fact]
    public void Only_the_old_name_has_a_message() => Assert.Null(RenamedProducts.Message("dbdatagateway"));
}
