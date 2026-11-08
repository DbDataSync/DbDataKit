using DbDataKit.Config.Git;
using DbDataKit.Quality;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Plan 33 UE07: the checks of a replication saved from a screen. The section is written from the forms and the whole file is read again by the strict reader; what the file has outside the section stays as written (comments, line ends),
/// a section changed since it was read is refused with the current one, and a fault is the reader's, with file, line and suggestion, with nothing written.
/// </summary>
public sealed class ReplicationChecksEditTests : IDisposable
{
    private static readonly GitAuthor Author = new("Ada", "ada@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-checks-edit-").FullName;
    private readonly ConfigRepository _config;

    public ReplicationChecksEditTests()
    {
        Repository.Init(_root);
        _config = new ConfigRepository(Path.Combine(_root, "config"), new GitCommitService(_root), SecretStore.ForProviders([new InMemorySecretProvider()]));
    }

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private string TaskPath => Path.Combine(_root, "config", "replications", "sales", "task.yaml");

    private static ReplicationTaskConfig Task() => new()
    {
        Name = "sales",
        Scheduling = new SchedulingConfig { Mode = ScheduleMode.Continuous, FrequencySeconds = 30 },
        ChangeProcessing = new ChangeProcessingConfig { Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" }, Cache = new CacheConfig { Kind = "MsSqlStagingTable" }, Writer = new WriterConfig { Kind = "MsSqlMerge" } },
    };

    private static CheckForm Freshness(string id = "orders_fresh", string age = "6h") => new() { Id = id, Kind = "freshness", Dataset = "dbo.Orders", MaxAge = age, Severity = "block", OnError = "alert" };

    private void Saved(string? sectionText = null)
    {
        _config.SaveReplicationTask(Task(), Author);
        if (sectionText is not null) File.WriteAllText(TaskPath, File.ReadAllText(TaskPath).TrimEnd() + "\n\n" + sectionText);
    }

    [Fact]
    public void A_replication_with_no_checks_has_an_empty_document_and_a_freshness_check_is_saved_and_read_back_and_committed()
    {
        Saved();
        var empty = _config.LoadReplicationChecks("sales");
        Assert.Empty(empty.Checks);
        Assert.Equal("", empty.Text);

        var saved = _config.SaveReplicationChecks("sales", [Freshness()], empty.Version, Author);

        Assert.NotEqual(empty.Version, saved.Version);
        var again = _config.LoadReplicationChecks("sales");
        Assert.Equal(saved.Version, again.Version);
        var check = Assert.Single(again.Checks);
        Assert.Equal(("orders_fresh", "freshness", "6h", "block"), (check.Id, check.Kind, check.MaxAge, check.Severity));
        Assert.Equal("orders_fresh", Assert.Single(_config.LoadReplicationTask("sales").Checks).Id);               // the task loads with it
        using var repo = new Repository(_root);
        Assert.Contains("checks of replication 'sales'", repo.Commits.First().Message);
        Assert.Equal("Ada", repo.Commits.First().Author.Name);
    }

    [Fact]
    public void Saving_the_checks_keeps_the_rest_of_the_file_as_written_comments_and_line_ends_included()
    {
        Saved();
        var text = File.ReadAllText(TaskPath).TrimEnd();
        File.WriteAllText(TaskPath, ("# owned by the sales team\n" + text + "\n").Replace("\n", "\r\n"));
        var before = File.ReadAllText(TaskPath);

        _config.SaveReplicationChecks("sales", [Freshness()], _config.LoadReplicationChecks("sales").Version, Author);

        var after = File.ReadAllText(TaskPath);
        Assert.StartsWith("# owned by the sales team\r\n", after);
        Assert.StartsWith(before.TrimEnd('\r', '\n'), after);                                  // everything that was there is still there, first
        Assert.DoesNotContain("\n", after.Replace("\r\n", ""));                                // and the file's line ends are the same throughout
    }

    [Fact]
    public void The_checks_are_replaced_a_check_removed_goes_and_none_removes_the_section_and_a_comment_above_the_old_section_goes_with_it()
    {
        Saved("# the orders must be fresh\nchecks:\n  - {id: a, kind: freshness, dataset: dbo.Orders, max_age: 6h}\n  - {id: b, kind: not_null, dataset: dbo.Orders, column: OrderId}\n");
        var read = _config.LoadReplicationChecks("sales");
        Assert.Equal(["a", "b"], read.Checks.Select(c => c.Id));
        Assert.Contains("# the orders must be fresh", read.Text);

        var one = _config.SaveReplicationChecks("sales", [read.Checks[1]], read.Version, Author);
        Assert.Equal(["b"], one.Checks.Select(c => c.Id));
        Assert.DoesNotContain("the orders must be fresh", File.ReadAllText(TaskPath));

        var none = _config.SaveReplicationChecks("sales", [], one.Version, Author);
        Assert.Empty(none.Checks);
        Assert.DoesNotContain("checks:", File.ReadAllText(TaskPath));
        Assert.Equal("sales", _config.LoadReplicationTask("sales").Name);
    }

    [Fact]
    public void A_section_changed_since_it_was_read_is_refused_with_the_current_one_and_a_change_elsewhere_in_the_file_is_not_a_conflict()
    {
        Saved("checks:\n  - {id: a, kind: freshness, dataset: dbo.Orders, max_age: 6h}\n");
        var read = _config.LoadReplicationChecks("sales");

        // somebody edits another part of the file: the version of the checks is still good
        File.WriteAllText(TaskPath, "# a note\n" + File.ReadAllText(TaskPath));
        _config.SaveReplicationChecks("sales", [Freshness("a", "1d")], read.Version, Author);
        Assert.StartsWith("# a note", File.ReadAllText(TaskPath));

        // somebody edits the section: the old version is refused, and the answer is what the file has now
        var conflict = Assert.Throws<ReplicationChecksConflictException>(() => _config.SaveReplicationChecks("sales", [Freshness("a", "2d")], read.Version, Author));
        Assert.Equal("1d", Assert.Single(conflict.Current.Checks).MaxAge);
        Assert.Equal("1d", Assert.Single(_config.LoadReplicationChecks("sales").Checks).MaxAge);        // nothing was written
    }

    [Fact]
    public void A_check_that_is_not_one_is_refused_with_file_line_and_suggestion_and_the_file_is_untouched_and_a_missing_replication_is_not_found()
    {
        Saved();
        var version = _config.LoadReplicationChecks("sales").Version;
        var before = File.ReadAllText(TaskPath);

        var refused = Assert.Throws<ConfigFileException>(() => _config.SaveReplicationChecks("sales", [Freshness(), new CheckForm { Id = "b", Kind = "freshnes", Dataset = "dbo.Orders", MaxAge = "6h" }], version, Author));

        Assert.Equal("task.yaml", Path.GetFileName(refused.File));
        var fault = Assert.Single(refused.Diagnostics);
        Assert.Equal(CheckSection.UnknownKind, fault.Code);
        Assert.True(fault.Line > 0);
        Assert.Contains("Did you mean `freshness`?", fault.Message);
        Assert.Equal(before, File.ReadAllText(TaskPath));
        Assert.Throws<ConfigFileException>(() => _config.SaveReplicationChecks("sales", [Freshness("x"), Freshness("x")], version, Author));   // a repeated id
        Assert.Throws<FileNotFoundException>(() => _config.LoadReplicationChecks("nowhere"));
        Assert.Throws<FileNotFoundException>(() => _config.SaveReplicationChecks("nowhere", [], "v", Author));
    }

    [Fact]
    public void A_section_a_person_broke_by_hand_is_a_diagnostic_on_load_not_a_crash()
    {
        Saved("checks:\n  - {id: a, kind: nope, dataset: dbo.Orders}\n");

        var fault = Assert.Throws<ConfigFileException>(() => _config.LoadReplicationChecks("sales"));

        Assert.Contains(fault.Diagnostics, d => d.Code == CheckSection.UnknownKind && d.Line > 0);
    }
}
