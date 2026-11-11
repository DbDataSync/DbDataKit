namespace DbDataGateway.Tests.Config;

/// <summary>What is Windows-only stays so and says so (plan 33 SC02): on Linux a configuration that asks for it is refused at start, with the setting named.</summary>
public sealed class WindowsOnlySettingsTests
{
    private static string Refusal(string json)
    {
        var load = ConfigTestSupport.LoadJson(json);
        Assert.False(load.Ok, "expected the configuration to be refused");
        return ConfigTestSupport.Format(load);
    }

    [Fact]
    public void An_analysis_services_source_is_refused_on_linux_and_names_the_setting()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Analysis Services is refused off Windows; the Windows run accepts it");
        string text = Refusal("""{ "Gateway": { "Semantic": { "Models": [ { "Catalog": "semantic/sales.yaml", "Ssas": { "ssas_finance": "Data Source=ssas01;Catalog=Finance" } } ] } } }""");
        Assert.Contains("Gateway:Semantic:Models:0:Ssas:ssas_finance", text);
        Assert.Contains("only on Windows", text);
    }

    [Fact]
    public void Windows_password_sign_in_is_refused_on_linux_and_names_the_setting()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Windows sign-in is refused off Windows; the Windows run accepts it");
        string text = Refusal("""{ "Gateway": { "Auth": { "Windows": true } } }""");
        Assert.Contains("Gateway:Auth:Windows", text);
        Assert.Contains("only on Windows", text);
    }

    [Fact]
    public void A_32_bit_odbc_source_is_refused_on_linux_and_names_the_setting()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "The 32-bit worker exists on Windows only");
        string text = Refusal("""{ "Gateway": { "DataSources": { "legacy": { "Provider": "odbc", "Bitness": "x86", "ConnectionString": "DSN=old" } } } }""");
        Assert.Contains("Gateway:DataSources:legacy:Bitness", text);
        Assert.Contains("only on Windows", text);
    }
}
