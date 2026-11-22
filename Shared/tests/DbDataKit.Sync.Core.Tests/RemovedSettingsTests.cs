using DbDataSync.Core.Config;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Plan 13, X05 and plan 13a, X06 (clean break): the passkey settings, <c>App:AlternateUrls</c> (their one reader), Windows sign-in and network trust are refused with a message that names the setting, never ignored.
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
    [InlineData("DbDataSync:Auth:Windows:AdminGroup")]
    [InlineData("DbDataSync:Auth:Windows:ViewerGroup")]
    [InlineData("DbDataSync:Auth:Windows:Mode")]
    [InlineData("DbDataSync:Auth:Windows")]
    [InlineData("DbDataSync:Auth:Network:Admin")]
    [InlineData("dbdatasync:auth:network:viewer")]
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
            ["DbDataSync:App:Url", "DbDataSync:App:TrustedProxies", "DbDataSync:Auth:WindowsLike", "DbDataSync:Auth:NetworkLike", "DbDataSync:Auth:PasskeysLike", "DbDataSync:App:AlternateUrlsOther"], "anywhere"));

    [Fact]
    public void NothingSet_IsNotRefused() => Assert.Null(RemovedSettings.Refusal([], "anywhere"));
}
