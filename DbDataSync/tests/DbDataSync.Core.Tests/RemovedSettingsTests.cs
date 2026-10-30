using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Plan 13, X05 (clean break): the passkey settings and <c>App:AlternateUrls</c> (their one reader) are refused with a message that names the setting, never ignored.
/// </summary>
public sealed class RemovedSettingsTests
{
    [Theory]
    [InlineData("DbDataSync:Auth:Passkeys:RelyingPartyId")]
    [InlineData("DbDataSync:Auth:Passkeys:Mode")]
    [InlineData("DbDataSync:Auth:Passkeys:Origins:0")]
    [InlineData("DbDataSync:Auth:Passkeys")]
    [InlineData("dbdatasync:auth:passkeys:relyingpartyname")]
    [InlineData("DbDataSync:App:AlternateUrls")]
    public void ARemovedSetting_IsRefused_NamingIt(string key)
    {
        var refusal = RemovedSettings.Refusal(["DbDataSync:App:Url", key], "in 'server.yaml'");

        Assert.NotNull(refusal);
        Assert.Contains($"'{key}'", refusal);
        Assert.Contains("in 'server.yaml'", refusal);
        Assert.Contains("dbdatakit user invite", refusal);
    }

    [Fact]
    public void TheSettingsThatStay_AreNotRefused() =>
        Assert.Null(RemovedSettings.Refusal(
            ["DbDataSync:App:Url", "DbDataSync:App:TrustedProxies", "DbDataSync:Auth:Windows:AdminGroup", "DbDataSync:Auth:Network:Admin", "DbDataSync:Auth:PasskeysLike", "DbDataSync:App:AlternateUrlsOther"], "anywhere"));

    [Fact]
    public void NothingSet_IsNotRefused() => Assert.Null(RemovedSettings.Refusal([], "anywhere"));
}
