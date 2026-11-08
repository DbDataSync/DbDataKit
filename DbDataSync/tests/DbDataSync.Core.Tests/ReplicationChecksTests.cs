using DbDataKit.Config.Git;
using DbDataKit.Config.Yaml;
using DbDataKit.Quality;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;
using LibGit2Sharp;

namespace DbDataSync.Core.Tests;

/// <summary>
/// Plan 23a FQ04: the <c>checks:</c> section of a replication's <c>task.yaml</c> (Sync's configuration format 2). It is read by Sync's strict loader through <c>DbDataKit.Quality</c>, a bad part is refused with file and line,
/// an unknown key near <c>checks</c> is answered with the right one, and a save of the replication keeps the section as written, comments included. Nothing evaluates a check here (FQ10).
/// </summary>
public sealed class ReplicationChecksTests : IDisposable
{
    private static readonly GitAuthor Author = new("Test", "test@example.com");

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-checks-").FullName;
    private readonly ConfigRepository _config;

    public ReplicationChecksTests()
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
        ChangeProcessing = new ChangeProcessingConfig
        {
            Reader = new ReaderConfig { Kind = "MsSqlChangeTracking" },
            Cache = new CacheConfig { Kind = "MsSqlStagingTable" },
            Writer = new WriterConfig { Kind = "MsSqlMerge" },
        },
    };

    /// <summary>A saved replication, then its file with <paramref name="section"/> added at the end (as a person would edit it).</summary>
    private string WriteWith(string section)
    {
        _config.SaveReplicationTask(Task(), Author);
        var text = File.ReadAllText(TaskPath).TrimEnd() + "\n\n" + section;
        File.WriteAllText(TaskPath, text);
        return text;
    }

    private const string Section = """
        # what the business relies on for this replication
        checks:
          # the orders table must not fall behind
          - id: orders_fresh   # freshness first
            kind: freshness
            dataset: dbo.Orders
            max_age: 6h
            severity: block
          - id: orders_key_not_null
            kind: not_null
            dataset: dbo.Orders
            column: OrderId
        """;

    private ConfigFileException Refused(string section)
    {
        WriteWith(section);
        return Assert.Throws<ConfigFileException>(() => _config.LoadReplicationTask("sales"));
    }

    [Fact]
    public void A_saved_replication_is_format_2_and_has_no_checks()
    {
        _config.SaveReplicationTask(Task(), Author);
        Assert.Contains("schemaVersion: 2", File.ReadAllText(TaskPath));
        Assert.DoesNotContain("checks", File.ReadAllText(TaskPath));
        Assert.Empty(_config.LoadReplicationTask("sales").Checks);
    }

    [Fact]
    public void A_replication_with_a_freshness_check_loads_it()
    {
        WriteWith(Section);

        var checks = _config.LoadReplicationTask("sales").Checks;

        Assert.Equal(["orders_fresh", "orders_key_not_null"], checks.Select(c => c.Id));
        var fresh = checks[0];
        Assert.Equal(CheckKind.Freshness, fresh.Kind);
        Assert.Equal("dbo.Orders", fresh.Attachment.Dataset);
        Assert.Equal(CheckSeverity.Block, fresh.Severity);
        Assert.Equal(TimeSpan.FromHours(6), Assert.IsType<FreshnessParameters>(fresh.Parameters).MaxAge);
        Assert.Equal("OrderId", checks[1].Attachment.Column);
    }

    [Fact]
    public void Saving_the_replication_keeps_the_checks_section_as_written_comments_included()
    {
        WriteWith(Section);
        var task = _config.LoadReplicationTask("sales");
        task.Notes = "changed from a screen that knows nothing of checks";

        _config.SaveReplicationTask(task, Author);

        var text = File.ReadAllText(TaskPath).Replace("\r\n", "\n");
        Assert.Contains("changed from a screen", text);
        Assert.Contains(Section.Replace("\r\n", "\n"), text);
        Assert.Equal(["orders_fresh", "orders_key_not_null"], _config.LoadReplicationTask("sales").Checks.Select(c => c.Id));

        // and again: a second save does not duplicate or lose it
        _config.SaveReplicationTask(_config.LoadReplicationTask("sales"), Author);
        Assert.Equal(1, CountOf(File.ReadAllText(TaskPath), "\nchecks:"));
        Assert.Equal(2, _config.LoadReplicationTask("sales").Checks.Count);
    }

    [Fact]
    public void A_task_the_program_saves_from_a_model_without_checks_loses_none_that_the_file_has_on_windows_line_endings_too()
    {
        WriteWith(Section);
        File.WriteAllText(TaskPath, File.ReadAllText(TaskPath).Replace("\r\n", "\n").Replace("\n", "\r\n"));

        _config.SaveReplicationTask(Task(), Author);

        Assert.Equal(["orders_fresh", "orders_key_not_null"], _config.LoadReplicationTask("sales").Checks.Select(c => c.Id));
        Assert.Contains("# the orders table must not fall behind", File.ReadAllText(TaskPath));
    }

    [Fact]
    public void An_unknown_key_near_checks_suggests_it()
    {
        var ex = Refused("chekcs:\n  - { id: a, kind: not_null, dataset: dbo.Orders, column: OrderId }\n");

        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal("chekcs", d.KeyPath);
        Assert.True(d.Line > 1);
        Assert.Contains("Did you mean `checks`?", d.Message);
        Assert.Contains("task.yaml", ex.Message);
    }

    [Fact]
    public void A_bad_check_is_refused_with_file_and_line_and_a_suggestion()
    {
        var ex = Refused("checks:\n  - id: a\n    kind: freshnes\n    dataset: dbo.Orders\n    max_age: 6h\n");

        var d = Assert.Single(ex.Diagnostics);
        Assert.Equal(CheckSection.UnknownKind, d.Code);
        Assert.Contains("Did you mean `freshness`?", d.Message);
        var kindLine = File.ReadAllLines(TaskPath).Select((l, i) => (l, i)).Single(x => x.l.Contains("kind: freshnes")).i + 1;
        Assert.Equal(kindLine, d.Line);
        Assert.Contains("task.yaml", ex.Message);
    }

    [Fact]
    public void A_key_a_freshness_check_does_not_have_is_refused_with_the_near_one()
    {
        var ex = Refused("checks:\n  - { id: a, kind: freshness, dataset: dbo.Orders, max_ag: 6h }\n");

        Assert.Contains(ex.Diagnostics, d => d.Message.Contains("max_age"));
    }

    [Fact]
    public void A_task_file_of_format_1_is_refused_with_the_clean_break_message_naming_the_file()
    {
        _config.SaveReplicationTask(Task(), Author);
        File.WriteAllText(TaskPath, File.ReadAllText(TaskPath).Replace("schemaVersion: 2", "schemaVersion: 1"));

        var ex = Assert.Throws<ConfigFileException>(() => _config.LoadReplicationTask("sales"));

        var d = Assert.Single(ex.Diagnostics);
        Assert.Contains("is format 1, which this version of Sync no longer reads (current: 2)", d.Message);
        Assert.Contains("only adds the optional checks: section", d.Message);
        Assert.StartsWith("task.yaml cannot be read:", ex.Message);
    }

    [Fact]
    public void A_task_file_of_a_newer_format_is_refused_not_half_read()
    {
        _config.SaveReplicationTask(Task(), Author);
        File.WriteAllText(TaskPath, File.ReadAllText(TaskPath).Replace("schemaVersion: 2", "schemaVersion: 3"));

        var d = Assert.Single(Assert.Throws<ConfigFileException>(() => _config.LoadReplicationTask("sales")).Diagnostics);

        Assert.Equal(SharedCodes.SchemaVersionNewer, d.Code);
        Assert.Contains("is format 3, which is newer than this version of Sync reads (format 2)", d.Message);
    }

    [Fact]
    public void Only_the_replication_has_checks_a_table_mapping_does_not()
    {
        var mapping = Path.Combine(_root, "config", "replications", "sales", "table-mappings", "m1.yaml");
        Directory.CreateDirectory(Path.GetDirectoryName(mapping)!);
        File.WriteAllText(mapping, "schemaVersion: 1\nname: m1\nchecks: []\n");

        var d = Assert.Single(Assert.Throws<ConfigFileException>(() => _config.LoadTableMapping("sales", "m1")).Diagnostics);
        Assert.Equal("checks", d.KeyPath);
    }

    [Fact]
    public void A_table_mapping_and_a_script_manifest_stay_format_1_and_a_2_is_refused_as_newer()
    {
        var mapping = Path.Combine(_root, "config", "replications", "sales", "table-mappings", "m1.yaml");
        var script = Path.Combine(_root, "config", "scripts", "s1.yaml");
        foreach (var file in new[] { mapping, script }) Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(mapping, "schemaVersion: 2\nname: m1\n");
        File.WriteAllText(script, "schemaVersion: 2\nname: s1\n");

        var m = Assert.Single(Assert.Throws<ConfigFileException>(() => _config.LoadTableMapping("sales", "m1")).Diagnostics);
        var sc = Assert.Single(Assert.Throws<ConfigFileException>(() => _config.LoadScriptManifest("s1")).Diagnostics);
        Assert.Contains("is format 2, which is newer than this version of Sync reads (format 1)", m.Message);
        Assert.Contains("is format 2, which is newer than this version of Sync reads (format 1)", sc.Message);
    }

    private static int CountOf(string text, string what) => text.Split(what).Length - 1;
}
