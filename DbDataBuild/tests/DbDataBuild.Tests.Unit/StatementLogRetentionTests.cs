using DbDataBuild.Execution;

namespace DbDataBuild.Tests.Unit;

public class StatementLogRetentionTests
{
    private static string Folder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-retention-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static string Log(string dir, string name, int daysOld)
    {
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-daysOld));
        return path;
    }

    [Fact]
    public void Logs_older_than_the_limit_go_and_newer_ones_stay_and_other_files_are_not_touched()
    {
        var dir = Folder();
        var old = Log(dir, "a.jsonl", 40);
        var edge = Log(dir, "b.jsonl", 29);
        var recent = Log(dir, "c.jsonl", 1);
        var other = Log(dir, "notes.txt", 400);
        Assert.Equal(1, StatementLogRetention.Prune(dir, 30, DateTime.UtcNow));
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(edge) && File.Exists(recent) && File.Exists(other));
    }

    [Fact]
    public void Zero_keeps_everything_and_a_folder_that_is_not_there_is_nothing_to_do()
    {
        var dir = Folder();
        var old = Log(dir, "a.jsonl", 4000);
        Assert.Equal(0, StatementLogRetention.Prune(dir, 0, DateTime.UtcNow));
        Assert.True(File.Exists(old));
        Assert.Equal(0, StatementLogRetention.Prune(Path.Combine(dir, "nowhere"), 30, DateTime.UtcNow));
    }

    [Fact]
    public void A_log_in_use_is_left_alone()
    {
        var dir = Folder();
        var path = Log(dir, "a.jsonl", 40);
        using var open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var removed = StatementLogRetention.Prune(dir, 30, DateTime.UtcNow);
        if (OperatingSystem.IsWindows()) { Assert.Equal(0, removed); Assert.True(File.Exists(path)); }    // a file in use cannot be deleted there; elsewhere the delete simply works
    }
}
