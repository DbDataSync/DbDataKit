using Xunit;

namespace DbDataKit.Secrets.Tests;

public class SecretsOverviewTests
{
    private static SecretHeld Held(string name, DateTimeOffset? at = null) => new(SecretName.Parse(name), at);
    private static ReferenceUse Use(string by, string reference) => new(by, SecretRef.Parse(reference));
    private static string[] Strings(System.Text.Json.Nodes.JsonNode? array) => [.. array!.AsArray().Select(n => (string)n!)];

    [Fact]
    public void A_reference_with_no_secret_is_missing_a_secret_nobody_names_is_unused_and_one_outside_is_neither()
    {
        var overview = SecretsOverview.Build("dbdatagateway", "store",
            [Held("dbdatakit:gateway:ds:erp", new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero)), Held("dbdatakit:gateway:ds:old")],
            [Use("erp", "secret:dbdatakit:gateway:ds:erp"), Use("crm", "secret:dbdatakit:gateway:ds:crm"), Use("hr", "env:HR_PASSWORD"), Use("erp-read", "secret:dbdatakit:gateway:ds:erp")]);

        Assert.Equal(["secret:dbdatakit:gateway:ds:crm"], Strings(overview["missing"]));
        Assert.Equal(["dbdatakit:gateway:ds:old"], Strings(overview["unused"]));
        var statuses = overview["references"]!.AsArray().ToDictionary(r => (string)r!["reference"]!, r => (string)r!["status"]!);
        Assert.Equal("present", statuses["secret:dbdatakit:gateway:ds:erp"]);
        Assert.Equal("outside", statuses["env:HR_PASSWORD"]);
        var erp = overview["secrets"]!.AsArray().Single(s => (string)s!["name"]! == "dbdatakit:gateway:ds:erp")!;
        Assert.Equal(["erp", "erp-read"], Strings(erp["used_by"]));
        Assert.StartsWith("2026-11-01", (string)erp["updated"]!);
    }

    [Fact]
    public void Nothing_held_and_nothing_named_is_empty()
    {
        var overview = SecretsOverview.Build("p", "store", [], []);
        Assert.Empty(overview["secrets"]!.AsArray());
        Assert.Empty(overview["missing"]!.AsArray());
        Assert.Empty(overview["unused"]!.AsArray());
    }
}
