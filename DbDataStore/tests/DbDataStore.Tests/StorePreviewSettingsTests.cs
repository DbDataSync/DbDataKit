using DbDataStore.Server;

namespace DbDataStore.Tests;

/// <summary>Plan 21 CH07: the <c>previews:</c> section of <c>server.yaml</c> is read strictly.</summary>
public sealed class StorePreviewSettingsTests : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("dds-previews").FullName;

    public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }

    private string Write(string text)
    {
        var path = Path.Combine(directory, "server.yaml");
        File.WriteAllText(path, text);
        return path;
    }

    [Fact]
    public void No_file_or_no_section_is_thirty_days_with_a_week_of_notice()
    {
        Assert.Equal(new PreviewSettings(30, 7), PreviewSettings.Load(Path.Combine(directory, "missing.yaml")));
        Assert.Equal(new PreviewSettings(30, 7), PreviewSettings.Load(Write("state:\n  engine: sqlite\n")));
    }

    [Fact]
    public void The_section_sets_the_days()
    {
        Assert.Equal(new PreviewSettings(14, 3), PreviewSettings.Load(Write("previews:\n  idle_days: 14\n  notice_days: 3\n")));
        Assert.Equal(new PreviewSettings(5, 7), PreviewSettings.Load(Write("previews:\n  idle_days: 5\n")));
    }

    [Fact]
    public void An_unknown_key_or_a_bad_number_fails_naming_the_file_and_the_key()
    {
        var path = Write("previews:\n  idle_day: 14\n");
        var unknown = Assert.Throws<InvalidOperationException>(() => PreviewSettings.Load(path));
        Assert.Contains(path, unknown.Message);
        Assert.Contains("previews.idle_day", unknown.Message);
        Assert.Contains("Did you mean `idle_days`", unknown.Message);
        Assert.Contains("previews.idle_days", Assert.Throws<InvalidOperationException>(() => PreviewSettings.Load(Write("previews:\n  idle_days: 0\n"))).Message);
        Assert.Contains("previews.notice_days", Assert.Throws<InvalidOperationException>(() => PreviewSettings.Load(Write("previews:\n  notice_days: soon\n"))).Message);
    }

    [Fact]
    public void The_sweeper_reads_the_settings_at_each_pass_and_keeps_the_last_good_ones()
    {
        var path = Write("previews:\n  idle_days: 14\n");
        var log = new List<string>();
        var sweeper = new StoreDraftSweeper(directory, new StoreConnections(directory), null!, PreviewSettings.Load(path), () => PreviewSettings.Load(path), log.Add);
        Assert.Equal(new PreviewSettings(14, 7), sweeper.Reload());
        Write("previews:\n  idle_days: 10\n  notice_days: 2\n");
        Assert.Equal(new PreviewSettings(10, 2), sweeper.Reload());
        Write("previews:\n  idle_day: 3\n");
        Assert.Equal(new PreviewSettings(10, 2), sweeper.Reload());   // a wrong edit is logged, the last good settings stay
        Assert.Contains("previews.idle_day", Assert.Single(log));
    }
}
