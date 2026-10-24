using DbDataKit.Config.Git;
using DbDataKit.Web.Trust;
using LibGit2Sharp;
using Xunit;

namespace DbDataKit.Web.Tests;

public sealed class ManagementAuditTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdata-audit-").FullName;

    public void Dispose()
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(entry, FileAttributes.Normal);
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private static ValidatedToken Token(string? userName = "Ada", bool byConsole = true) =>
        new(new PairedConsole("console-a", "Operations", new byte[32], ServerRole.Admin), "user-7", userName, byConsole, ServerRole.Author,
            new Dictionary<string, ServerRole>(), "token-0001", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddMinutes(5));

    [Fact]
    public void An_audit_entry_names_the_user_the_console_the_command_and_the_outcome_and_reads_back()
    {
        var log = new FileAuditLog(Path.Combine(_root, "audit", "management.jsonl"));
        var at = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

        log.Append(ManagementIdentity.Entry(Token(), "project file write", "models/orders.sql", "applied", at));
        log.Append(ManagementIdentity.Entry(Token(null), "project file delete", "models/old.sql", "refused", at.AddSeconds(1)));

        var entries = log.Read();
        Assert.Equal(2, entries.Count);
        Assert.Equal(new AuditEntry(at, "console-a", "Operations", "user-7", "Ada", true, "project file write", "models/orders.sql", "applied", "token-0001"), entries[0]);
        Assert.Equal("refused", entries[1].Outcome);
        Assert.Null(entries[1].UserName);
    }

    [Fact]
    public void Entries_are_appended_in_order_when_many_writers_log_at_once()
    {
        var log = new FileAuditLog(Path.Combine(_root, "management.jsonl"));

        Parallel.For(0, 50, i => log.Append(ManagementIdentity.Entry(Token(), "secret set", $"dbdata:sync:connection:c{i}", "applied", DateTimeOffset.UnixEpoch)));

        var entries = log.Read(limit: 1000);
        Assert.Equal(50, entries.Count);
        Assert.Equal(50, entries.Select(e => e.Target).Distinct().Count());
    }

    [Fact]
    public void Read_returns_only_the_newest_entries()
    {
        var log = new FileAuditLog(Path.Combine(_root, "management.jsonl"));
        for (var i = 0; i < 5; i++) log.Append(ManagementIdentity.Entry(Token(), "c", $"t{i}", "applied", DateTimeOffset.UnixEpoch));

        Assert.Equal(["t3", "t4"], log.Read(limit: 2).Select(e => e.Target));
    }

    [Fact]
    public void A_missing_log_reads_as_empty() =>
        Assert.Empty(new FileAuditLog(Path.Combine(_root, "none.jsonl")).Read());

    [Fact]
    public void A_config_commit_is_authored_by_the_user_through_the_console()
    {
        Repository.Init(_root);
        var file = Path.Combine(_root, "server.yaml");
        File.WriteAllText(file, "listen: 5080\n");
        var (name, email) = ManagementIdentity.CommitAuthor(Token());

        new GitCommitService(_root).CommitChanges([file], "Change the listen port", new GitAuthor(name, email));

        var commit = new GitCommitService(_root).GetHistory("", 5).Single();
        Assert.Equal("Ada (console Operations)", commit.AuthorName);
        Assert.Equal("user-7@console-a.console.invalid", commit.AuthorEmail);
    }

    [Theory]
    [InlineData("Ada <ada@x>\nLovelace", "Ada ada@xLovelace")]
    [InlineData("  ", "user-7")]
    public void A_name_git_would_refuse_is_cleaned_and_a_blank_one_falls_back_to_the_user_id(string userName, string expectedName)
    {
        var (name, _) = ManagementIdentity.CommitAuthor(Token(userName, byConsole: false));

        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void The_local_channel_is_authored_by_the_operating_system_user()
    {
        var (name, email) = ManagementIdentity.LocalAuthor("svc dbdata");

        Assert.Equal("svc dbdata", name);
        Assert.Equal("svc-dbdata@local.invalid", email);
    }
}
