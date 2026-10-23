using DbDataKit.Hosting.Certificates.Windows;
using DbDataSync.Certificates;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// What <c>dbdatasync cert discover</c> decides to save and say. The directory query itself needs a domain
/// and is not covered here; every branch of the decision is.
/// </summary>
public sealed class CertDiscoveryTests
{
    private static DiscoveredCa Ca(string config, params string[] templates) => new(config, templates);

    private static CaDiscoveryResult Found(params DiscoveredCa[] cas) => new(cas, TemplateListReason.Available, null);

    private static string Text(DiscoveryOutcome outcome) => string.Join('\n', outcome.Lines);

    [Fact]
    public void OneCa_IsSaved()
    {
        var outcome = CertDiscovery.Decide(Found(Ca(@"ca01\Corp CA", "A", "B")), null, null);

        Assert.Equal(@"ca01\Corp CA", outcome.WriteCaConfig);
        Assert.True(outcome.Succeeded);
    }

    [Fact]
    public void SeveralCas_WriteNothing_AndEachComesWithTheCommandToSaveIt()
    {
        var outcome = CertDiscovery.Decide(Found(Ca(@"ca01\One", "A"), Ca(@"ca02\Two", "A")), null, null);

        Assert.Null(outcome.WriteCaConfig);
        Assert.Null(outcome.WriteTemplate);
        Assert.Contains("dbdatasync config set DbDataSync:Certificates:CaConfig \"ca01\\One\"", Text(outcome));
        Assert.Contains("dbdatasync config set DbDataSync:Certificates:CaConfig \"ca02\\Two\"", Text(outcome));
    }

    [Fact]
    public void AConfiguredCa_IsNeverOverwritten_AndGoesOnToItsTemplates()
    {
        var outcome = CertDiscovery.Decide(Found(Ca(@"ca01\One", "OnlyTemplate"), Ca(@"ca02\Two")), @"CA01\ONE", null);

        Assert.Null(outcome.WriteCaConfig);
        Assert.Equal("OnlyTemplate", outcome.WriteTemplate);
    }

    [Fact]
    public void AConfiguredCaTheDomainDoesNotPublish_IsLeftAlone_AndThePublishedOnesAreListed()
    {
        var outcome = CertDiscovery.Decide(Found(Ca(@"ca01\One", "A")), @"gone\Old CA", null);

        Assert.Null(outcome.WriteCaConfig);
        Assert.Null(outcome.WriteTemplate);
        Assert.Contains(@"gone\Old CA", Text(outcome));
        Assert.Contains("dbdatasync config set DbDataSync:Certificates:CaConfig \"ca01\\One\"", Text(outcome));
    }

    [Fact]
    public void OneCaWithOneTemplate_SavesBoth_AndNamesTheEnrollCommand()
    {
        var outcome = CertDiscovery.Decide(Found(Ca(@"ca01\One", "WebServer")), null, null);

        Assert.Equal(@"ca01\One", outcome.WriteCaConfig);
        Assert.Equal("WebServer", outcome.WriteTemplate);
        Assert.Contains("dbdatasync cert enroll", Text(outcome));
    }

    [Fact]
    public void SeveralTemplates_WriteNoTemplate_AndEachComesWithTheCommandToSaveIt()
    {
        var outcome = CertDiscovery.Decide(Found(Ca(@"ca01\One", "WebServer", "Machine")), null, null);

        Assert.Equal(@"ca01\One", outcome.WriteCaConfig);
        Assert.Null(outcome.WriteTemplate);
        Assert.Contains("dbdatasync config set DbDataSync:Certificates:Template \"WebServer\"", Text(outcome));
        Assert.Contains("dbdatasync config set DbDataSync:Certificates:Template \"Machine\"", Text(outcome));
        Assert.DoesNotContain("Next:", Text(outcome));
    }

    [Fact]
    public void AConfiguredTemplate_IsNeverOverwritten_EvenWhenOnlyOneIsPublished()
    {
        var outcome = CertDiscovery.Decide(Found(Ca(@"ca01\One", "Published")), @"ca01\One", "Mine");

        Assert.Null(outcome.WriteTemplate);
        Assert.Contains("Mine", Text(outcome));
        Assert.Contains("not published on this CA", Text(outcome));
    }

    [Fact]
    public void ACaWithNoTemplates_SaysSo()
    {
        var outcome = CertDiscovery.Decide(Found(Ca(@"ca01\One")), null, null);

        Assert.Equal(@"ca01\One", outcome.WriteCaConfig);
        Assert.Null(outcome.WriteTemplate);
        Assert.Contains("publishes no templates", Text(outcome));
    }

    [Theory]
    [InlineData(TemplateListReason.NotDomainJoined)]
    [InlineData(TemplateListReason.DirectoryUnreachable)]
    [InlineData(TemplateListReason.AccessDenied)]
    public void ADirectoryFailure_WritesNothing_NamesTheReason_AndPointsAtTheManualRoute(TemplateListReason reason)
    {
        var outcome = CertDiscovery.Decide(new CaDiscoveryResult([], reason, "the detail"), null, null);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.WriteCaConfig);
        Assert.Contains(reason.ToString(), Text(outcome));
        Assert.Contains("the detail", Text(outcome));
        Assert.Contains("certutil", Text(outcome));
    }

    [Fact]
    public void NoCaPublished_WritesNothing_AndFails()
    {
        var outcome = CertDiscovery.Decide(Found(), null, null);

        Assert.False(outcome.Succeeded);
        Assert.Null(outcome.WriteCaConfig);
        Assert.Contains("No enterprise certificate authority", Text(outcome));
    }

    [Fact]
    public void TheSetupWalkThrough_ForAnEnrollChoice_MentionsDiscover()
    {
        var lines = DbDataSync.Cli.Tui.SetupSteps.WindowsCertificateInstructions("enroll", null);

        Assert.Contains(lines, l => l.Contains("cert discover"));
        Assert.Contains(lines, l => l.Contains("cert enroll --dns"));
    }
}
