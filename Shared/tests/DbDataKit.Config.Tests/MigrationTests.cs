using DbDataKit.Config.Testing;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Config.Tests;

public class MigrationTests
{
    private sealed class RenameTimeout : IConfigMigration
    {
        public string Kind => "proxy";
        public int From => 1;
        public int To => 2;
        public void Apply(YamlDocument doc, IMigrationLog log)
        {
            if (doc.Has("oldTimeout")) { doc.RenameKey("oldTimeout", "timeout"); log.Info("renamed oldTimeout"); }
        }
    }

    private sealed class AddMode : IConfigMigration
    {
        public string Kind => "proxy";
        public int From => 2;
        public int To => 3;
        public void Apply(YamlDocument doc, IMigrationLog log)
        {
            var answer = log.Ask("mode", "Which mode do existing files use?", ["fast", "safe"]);
            if (answer == null) return;
            doc.Set("mode", answer);
        }
    }

    private sealed class Boom : IConfigMigration
    {
        public string Kind => "boom";
        public int From => 1;
        public int To => 2;
        public void Apply(YamlDocument doc, IMigrationLog log) => doc.Set("a.b", "x");   // a is a scalar
    }

    private static MigrationEngine Engine() => new([new RenameTimeout(), new AddMode(), new Boom()]);

    private const string V1 = "# header comment\n\nname: x  # keep\noldTimeout: 5   # seconds\ntags: [a]\n";

    [Fact]
    public void Missing_version_is_1_and_migrates_with_comments_kept_and_version_added_below_the_header()
    {
        var r = Engine().Plan("proxy", V1, "p.yml", new() { TargetVersion = 2 });
        Assert.Equal(MigrationStatus.Migrated, r.Status);
        Assert.Equal(1, r.FromVersion);
        Assert.Equal(2, r.ToVersion);
        Assert.Equal("# header comment\n\nschemaVersion: 2\nname: x  # keep\ntimeout: 5   # seconds\ntags: [a]\n", r.NewText);
        Assert.Contains("-oldTimeout: 5   # seconds", r.Diff);
        Assert.Contains("+timeout: 5   # seconds", r.Diff);
        Assert.Contains("+schemaVersion: 2", r.Diff);
    }

    [Fact]
    public void Dry_run_changes_nothing_and_the_result_loads()
    {
        var r = Engine().Plan("proxy", V1, "p.yml", new() { TargetVersion = 2 });
        var cfg = new ConfigBuilder<ProxyConfig>().AddText("p.yml", r.NewText).Build();
        // only the sample's own unknown key (`name`) remains; oldTimeout is gone
        Assert.True(cfg.Diagnostics.Count == 1 && cfg.Diagnostics[0].KeyPath == "name", string.Join("; ", cfg.Diagnostics));
    }

    [Fact]
    public void Chains_run_in_order_and_questions_need_answers()
    {
        var e = Engine();
        var open = e.Plan("proxy", V1, "p.yml");
        Assert.Equal(MigrationStatus.Failed, open.Status);
        Assert.Equal("mode", Assert.Single(open.Questions).Id);
        Assert.Contains(open.Diagnostics, d => d.Code == "DBD-113");
        Assert.Equal(V1, open.NewText);

        var answered = e.Plan("proxy", V1, "p.yml", new() { Answers = new Dictionary<string, string> { ["mode"] = "safe" } });
        Assert.Equal(MigrationStatus.Migrated, answered.Status);
        Assert.Equal(3, answered.ToVersion);
        Assert.Contains("schemaVersion: 3", answered.NewText);
        Assert.Contains("mode: safe", answered.NewText);
        Assert.Contains("timeout: 5", answered.NewText);

        var invalidAnswer = e.Plan("proxy", V1, "p.yml", new() { Answers = new Dictionary<string, string> { ["mode"] = "turbo" } });
        Assert.Equal(MigrationStatus.Failed, invalidAnswer.Status);
    }

    [Fact]
    public void Existing_version_key_is_updated_in_place()
    {
        var r = Engine().Plan("proxy", "schemaVersion: 1   # v\noldTimeout: 1\n", "p.yml", new() { TargetVersion = 2 });
        Assert.Equal("schemaVersion: 2   # v\ntimeout: 1\n", r.NewText);
    }

    [Fact]
    public void Up_to_date_and_newer_files()
    {
        var e = Engine();
        Assert.Equal(MigrationStatus.UpToDate, e.Plan("proxy", "schemaVersion: 3\n", "p.yml").Status);
        var newer = e.Plan("proxy", "schemaVersion: 4\nx: 1\n", "p.yml");
        Assert.Equal(MigrationStatus.Refused, newer.Status);
        Assert.Equal("DBD-110", Assert.Single(newer.Diagnostics).Code);
        Assert.Equal("schemaVersion: 4\nx: 1\n", newer.NewText);
        Assert.Equal(MigrationStatus.UpToDate, e.Plan("unknownkind", "a: 1\n", "p.yml").Status);
    }

    [Fact]
    public void Invalid_version_broken_yaml_and_migration_failures_are_diagnostics()
    {
        var e = Engine();
        Assert.Equal("DBD-111", e.Plan("proxy", "schemaVersion: two\n", "p.yml").Diagnostics[0].Code);
        Assert.Equal(MigrationStatus.Failed, e.Plan("proxy", "a: [1\n", "p.yml").Status);
        var boom = e.Plan("boom", "a: 1\n", "p.yml");
        Assert.Equal(MigrationStatus.Failed, boom.Status);
        Assert.Equal("DBD-112", boom.Diagnostics[0].Code);
        Assert.Equal("a: 1\n", boom.NewText);
    }

    [Fact]
    public void Apply_writes_a_backup_and_never_overwrites_an_earlier_one()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var f = Path.Combine(dir.FullName, "p.yml");
            File.WriteAllText(f, V1);
            var r = Engine().Apply("proxy", f, new() { TargetVersion = 2 });
            Assert.True(r.Written);
            Assert.Equal(f + ".bak", r.BackupPath);
            Assert.Equal(V1, File.ReadAllText(r.BackupPath!));
            Assert.Contains("schemaVersion: 2", File.ReadAllText(f));

            File.WriteAllText(f, V1);
            var again = Engine().Apply("proxy", f, new() { TargetVersion = 2 });
            Assert.Equal(f + ".bak.1", again.BackupPath);
            Assert.Equal(V1, File.ReadAllText(f + ".bak"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Apply_refuses_newer_files_and_writes_nothing()
    {
        var dir = Directory.CreateTempSubdirectory();
        try
        {
            var f = Path.Combine(dir.FullName, "p.yml");
            File.WriteAllText(f, "schemaVersion: 9\n");
            var r = Engine().Apply("proxy", f);
            Assert.False(r.Written);
            Assert.Equal(MigrationStatus.Refused, r.Migration.Status);
            Assert.False(File.Exists(f + ".bak"));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void Migrate_on_read_returns_text_in_memory()
    {
        var (text, r) = Engine().MigrateOnRead("proxy", V1, "p.yml", new() { TargetVersion = 2 });
        Assert.NotNull(text);
        Assert.True(r.Changed);
        Assert.Null(Engine().MigrateOnRead("proxy", "schemaVersion: 7\n", "p.yml").Text);
    }

    [Fact]
    public void Engine_rejects_gaps_and_duplicates()
    {
        Assert.Throws<ArgumentException>(() => new MigrationEngine([new RenameTimeout(), new RenameTimeout()]));
        Assert.Equal(3, Engine().LatestVersion("proxy"));
    }

    [Fact]
    public void Golden_pairs_are_stable_and_idempotent() =>
        GoldenMigration.AssertPair(Engine(), "proxy", V1, "# header comment\n\nschemaVersion: 2\nname: x  # keep\ntimeout: 5   # seconds\ntags: [a]\n",
            new() { TargetVersion = 2 }, "p.yml");

    [Fact]
    public void CRLF_files_keep_their_line_endings()
    {
        var r = Engine().Plan("proxy", "name: x\r\noldTimeout: 5\r\n", "p.yml", new() { TargetVersion = 2 });
        Assert.Equal("schemaVersion: 2\r\nname: x\r\ntimeout: 5\r\n", r.NewText);
    }

    [Fact]
    public void Schema_header_form_is_read_and_updated_in_place()
    {
        var r = Engine().Plan("proxy", "schema: proxy/1   # v\noldTimeout: 1\n", "p.yml", new() { TargetVersion = 2 });
        Assert.Equal(MigrationStatus.Migrated, r.Status);
        Assert.Equal("schema: proxy/2   # v\ntimeout: 1\n", r.NewText);
    }

    [Fact]
    public void Schema_header_is_refused_when_newer_wrong_kind_or_combined()
    {
        var e = Engine();
        var newer = e.Plan("proxy", "schema: proxy/9\n", "p.yml");
        Assert.Equal(MigrationStatus.Refused, newer.Status);
        Assert.Contains(newer.Diagnostics, d => d.Code == "DBD-110" && d.Message.Contains("`schema`"));
        Assert.Equal(MigrationStatus.Failed, e.Plan("proxy", "schema: other/1\n", "p.yml").Status);
        Assert.Equal(MigrationStatus.Failed, e.Plan("proxy", "schema: proxy\n", "p.yml").Status);
        Assert.Equal(MigrationStatus.Failed, e.Plan("proxy", "schema: proxy/1\nschemaVersion: 1\n", "p.yml").Status);
    }

    [Fact]
    public void Binder_and_schema_accept_the_header_form()
    {
        var ok = new ConfigBuilder<ProxyConfig>().AddText("p.yml", "schema: proxy/2\ntimeout: 5\n").Build();
        Assert.True(ok.Ok, string.Join("; ", ok.Diagnostics));
        var newer = new ConfigBuilder<ProxyConfig>().AddText("p.yml", "schema: proxy/3\n").Build();
        Assert.Contains(newer.Diagnostics, d => d.Code == "DBD-110");
        var bad = new ConfigBuilder<ProxyConfig>().AddText("p.yml", "schema: other/1\n").Build();
        Assert.Contains(bad.Diagnostics, d => d.Code == "DBD-111");
        Assert.Equal("^proxy/[1-9][0-9]*$", JsonSchemaGenerator.Generate<ProxyConfig>()["properties"]!["schema"]!["pattern"]!.GetValue<string>());
    }
}
