using DbDataKit.Server;

namespace DbDataKit.Server.Tests;

/// <summary>The section <c>quality: alerts:</c> of the console's <c>settings.yaml</c> (plan 23a FQ13), read strictly: what a good one gives, and each bad part refused with the file and the line.</summary>
public sealed class ConsoleQualityAlertsTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-quality-alerts-" + Guid.NewGuid().ToString("N")[..8]);

    public ConsoleQualityAlertsTests() => Directory.CreateDirectory(directory);

    public void Dispose()
    {
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private (ConsoleQualityAlertSettings? Alerts, string? Problem) Load(string yaml)
    {
        File.WriteAllText(ConsoleState.SettingsFileIn(directory), yaml);
        return ConsoleQualitySettings.Load(directory);
    }

    [Fact]
    public void A_good_section_gives_the_channel_the_recipients_and_the_interval()
    {
        var (alerts, problem) = Load("quality:\n  alerts:\n    channel: ops\n    recipients: [data@example.com, owner@example.com]\n    release_channel: test\n    every_seconds: 30\n");
        Assert.Null(problem);
        Assert.Equal("ops", alerts!.Channel);
        Assert.Equal(["data@example.com", "owner@example.com"], alerts.Recipients);
        Assert.Equal(("test", 30), (alerts.ReleaseChannel, alerts.EverySeconds));
    }

    [Fact]
    public void No_file_no_section_or_no_alerts_means_no_alerts_and_no_problem()
    {
        Assert.Equal((null, null), ConsoleQualitySettings.Load(directory));
        Assert.Equal((null, null), Load("delivery:\n  lease_seconds: 60\n"));
        Assert.Equal((null, null), Load("quality:\n"));
        var (alerts, problem) = Load("quality:\n  alerts:\n    channel: ops\n");
        Assert.Null(problem);
        Assert.Empty(alerts!.Recipients);
        Assert.Equal((null, 60), (alerts.ReleaseChannel, alerts.EverySeconds));
    }

    [Theory]
    [InlineData("quality:\n  alert:\n    channel: ops\n", "line 2", "'alert' is not a setting of quality")]
    [InlineData("quality:\n  alerts:\n    chanel: ops\n", "line 3", "'chanel' is not a setting of quality alerts")]
    [InlineData("quality:\n  alerts:\n    recipients: [a@example.com]\n", "line 2", "alerts needs a channel")]
    [InlineData("quality:\n  alerts:\n    channel: ops\n    recipients: [nobody]\n", "line 4", "a recipient is an address")]
    [InlineData("quality:\n  alerts:\n    channel: ops\n    every_seconds: 3\n", "line 4", "every_seconds is a whole number of at least 10")]
    public void A_bad_part_is_refused_with_the_file_and_the_line(string yaml, string line, string words)
    {
        var (alerts, problem) = Load(yaml);
        Assert.Null(alerts);
        Assert.Contains("settings.yaml", problem);
        Assert.Contains(line, problem);
        Assert.Contains(words, problem);
    }
}
