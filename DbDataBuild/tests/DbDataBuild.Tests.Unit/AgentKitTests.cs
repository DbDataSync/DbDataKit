using System.Text.RegularExpressions;
using DbDataBuild.Cli;
using DbDataBuild.Core;
using DbDataBuild.Models;
using static DbDataBuild.Tests.Unit.PolyglotBindingTests;
using static DbDataBuild.Tests.Unit.TestSupport;

namespace DbDataBuild.Tests.Unit;

/// <summary>
/// The skill an agent reads must not rot: every command, option, diagnostic code and schema file it names exists, its example model loads, and the files the command installs
/// are the ones in the repository.
/// </summary>
public partial class AgentKitTests
{
    private static string Skill => AgentKitCommand.Files().Single(f => f.Path == "SKILL.md").Content;

    private static (int Exit, string Out, string Err) Cli(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (CliApp.Run(args, o, e), o.ToString(), e.ToString());
    }

    [GeneratedRegex(@"`dbdatabuild ([a-z][a-z-]*)")]
    private static partial Regex CommandMention();

    [GeneratedRegex(@"(?<![\w-])(--[a-z][a-z-]*)")]
    private static partial Regex FlagMention();

    [Fact]
    public void The_skill_has_the_front_matter_an_agent_needs_to_find_and_use_it()
    {
        var lines = Skill.Split('\n');
        Assert.Equal("---", lines[0]);
        Assert.Equal("name: dbdatabuild", lines[1]);
        Assert.StartsWith("description: ", lines[2]);
        Assert.InRange(lines[2].Length, 100, 1024);
        Assert.Equal("---", lines[3]);
    }

    [Fact]
    public void Every_command_option_code_and_schema_the_skill_names_exists()
    {
        var catalog = TuiCommand.CliHost.Catalog();
        var commands = CommandSpecs.All.Select(c => c.Name).Concat(CommandSpecs.All.Select(c => c.Path[0])).ToHashSet();
        var options = catalog.SelectMany(c => c.Options.Select(o => o.Name)).Concat(["--format"]).ToHashSet();
        foreach (Match m in CommandMention().Matches(Skill)) Assert.True(commands.Contains(m.Groups[1].Value), $"the skill mentions `dbdatabuild {m.Groups[1].Value}`, which is not a command");
        // the table names commands without the program name, so check the backticked first words too
        foreach (var name in catalog.Select(c => c.Name).Where(n => n is not "project agent-kit")) Assert.Contains($"{name}", Skill);
        foreach (Match m in FlagMention().Matches(Skill))
            Assert.True(options.Contains(m.Groups[1].Value) || ConfigOnly.Contains(m.Groups[1].Value), $"the skill mentions {m.Groups[1].Value}, which is not an option of any command");

        var known = DiagnosticCatalog.All.Select(d => d.Code).ToHashSet();
        foreach (Match m in Regex.Matches(Skill, @"DDB-\d{3}")) Assert.True(known.Contains(m.Value), $"the skill mentions {m.Value}, which is not a diagnostic code");
        foreach (Match m in Regex.Matches(Skill, @"schemas/([a-z.]+\.json)")) Assert.Contains(AgentKitCommand.Files(), f => f.Path == "schemas/" + m.Groups[1].Value);
    }

    private static readonly string[] ConfigOnly = ["--allow-writes", "--allow-apply"];      // options of `mcp` and `web`, which are not in the catalog the test reads

    [Fact]
    public void The_example_model_in_the_skill_is_a_valid_definition()
    {
        var yaml = Regex.Match(Skill, "```yaml\n(.*?)```", RegexOptions.Singleline).Groups[1].Value;
        var (def, diags) = Load(yaml);
        Assert.Empty(diags.Select(DiagnosticFormatter.Format));
        Assert.Equal("marts.fct_orders", def!.Name);
        Assert.Equal(["order_id"], def.UniqueKey);
        Assert.Single(def.Indexes);
    }

    [Fact]
    public void The_kit_includes_every_schema_in_the_repository_unchanged()
    {
        var repo = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "schemas"), "*.json").Select(f => Path.GetFileName(f)).Order().ToList();
        var kit = AgentKitCommand.Files().Where(f => f.Path.StartsWith("schemas/")).ToList();
        Assert.Equal(repo, kit.Select(f => Path.GetFileName(f.Path)).Order());
        foreach (var (path, content) in kit) Assert.Equal(File.ReadAllText(Path.Combine(RepoRoot(), path)).Replace("\r\n", "\n"), content);
    }

    [Fact]
    public void Listing_writes_nothing_write_installs_and_check_notices_a_change()
    {
        var dir = NewProjectDir();
        var before = Snapshot(dir);
        var listed = Cli("project", "agent-kit", "--project", dir);
        Assert.Equal(0, listed.Exit);
        Assert.Contains(".claude/skills/dbdatabuild/SKILL.md", listed.Out);
        Assert.Equal(before, Snapshot(dir));

        Assert.Equal(CliApp.ExitFindings, Cli("project", "agent-kit", "--project", dir, "--check").Exit);          // nothing is installed yet
        var written = Cli("project", "agent-kit", "--project", dir, "--write");
        Assert.Equal(0, written.Exit);
        var skill = Path.Combine(dir, ".claude", "skills", "dbdatabuild", "SKILL.md");
        Assert.Equal(Skill, File.ReadAllText(skill));
        Assert.True(File.Exists(Path.Combine(dir, ".claude", "skills", "dbdatabuild", "schemas", "output.schema.json")));
        Assert.Contains("already up to date", Cli("project", "agent-kit", "--project", dir, "--write").Out);
        Assert.Equal(0, Cli("project", "agent-kit", "--project", dir, "--check").Exit);

        File.AppendAllText(skill, "local edit\n");
        var stale = Cli("project", "agent-kit", "--project", dir, "--check");
        Assert.Equal(CliApp.ExitFindings, stale.Exit);
        Assert.Contains("SKILL.md", stale.Err);
        Cli("project", "agent-kit", "--project", dir, "--write");
        Assert.Equal(Skill, File.ReadAllText(skill));                                                  // an edited copy is replaced: the kit belongs to the tool version

        Assert.Equal(CliApp.ExitUsage, Cli("project", "agent-kit", "--project", dir, "--write", "--check").Exit);
        var elsewhere = Cli("project", "agent-kit", "--project", dir, "--write", "--dir", "agents/dbdatabuild");
        Assert.True(File.Exists(Path.Combine(dir, "agents", "dbdatabuild", "SKILL.md")));
        Assert.Equal(0, elsewhere.Exit);
    }

    // ---- agent-kit --mcp ----

    private static (int Exit, string Out, string Err) Kit(string dir, params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        var exit = CliApp.Run(["project", "agent-kit", "--project", dir, .. args], o, e);
        return (exit, o.ToString(), e.ToString());
    }

    private static string NewDir() { var d = Path.Combine(Path.GetTempPath(), "ddb-kit-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void Mcp_is_opt_in_and_adds_only_the_server_with_the_read_logins_by_name()
    {
        var dir = NewDir();
        try
        {
            Assert.Equal(0, Kit(dir, "--write").Exit);
            Assert.False(File.Exists(Path.Combine(dir, ".mcp.json")));            // not without --mcp
            var listed = Kit(dir, "--mcp");
            Assert.Equal(0, listed.Exit);
            Assert.False(File.Exists(Path.Combine(dir, ".mcp.json")));            // listing writes nothing
            Assert.Contains("--write would add the dbdatabuild server", listed.Out);

            Assert.Equal(0, Kit(dir, "--write", "--mcp").Exit);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ".mcp.json")))!;
            var server = json["mcpServers"]!["dbdatabuild"]!;
            Assert.Equal("stdio", (string)server["type"]!);
            Assert.Equal("dbdatabuild", (string)server["command"]!);
            Assert.Equal(["ui", "mcp", "--project", "."], server["args"]!.AsArray().Select(a => (string)a!));
            var env = server["env"]!.AsObject().Select(p => p.Key).ToList();
            Assert.All(env, k => Assert.EndsWith("_READ", k));                    // the write login is never in the file
            Assert.Contains("DBDATABUILD_POSTGRES_READ", env);
            Assert.Equal("${DBDATABUILD_POSTGRES_READ:-}", (string)server["env"]!["DBDATABUILD_POSTGRES_READ"]!);       // a value comes from the environment, never from the file
            Assert.DoesNotContain("--allow", File.ReadAllText(Path.Combine(dir, ".mcp.json")));                          // read-only
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Mcp_keeps_the_other_servers_is_idempotent_and_is_checked()
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".mcp.json"), "{\n  \"mcpServers\": { \"other\": { \"type\": \"http\", \"url\": \"https://example.com/mcp\" } },\n  \"extra\": 1\n}\n");
            Assert.Equal(CliApp.ExitFindings, Kit(dir, "--check", "--mcp").Exit);                      // the server is missing
            Assert.Equal(0, Kit(dir, "--write", "--mcp").Exit);
            var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, ".mcp.json")))!;
            Assert.Equal("https://example.com/mcp", (string)json["mcpServers"]!["other"]!["url"]!);
            Assert.Equal(1, (int)json["extra"]!);
            Assert.NotNull(json["mcpServers"]!["dbdatabuild"]);
            var once = File.ReadAllText(Path.Combine(dir, ".mcp.json"));
            Assert.Equal(0, Kit(dir, "--write", "--mcp").Exit);
            Assert.Equal(once, File.ReadAllText(Path.Combine(dir, ".mcp.json")));                      // nothing changes the second time
            Assert.Equal(0, Kit(dir, "--check", "--mcp").Exit);
            Assert.DoesNotContain("\r", once);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1, 2]")]
    [InlineData("{ \"mcpServers\": 3 }")]
    public void A_file_that_cannot_be_merged_is_refused_and_left_as_it_is(string content)
    {
        var dir = NewDir();
        try
        {
            File.WriteAllText(Path.Combine(dir, ".mcp.json"), content);
            Assert.Equal(CliApp.ExitFindings, Kit(dir, "--write", "--mcp").Exit);
            Assert.Equal(content, File.ReadAllText(Path.Combine(dir, ".mcp.json")));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_skill_tells_an_agent_how_to_work_when_the_server_is_there_and_not_to_start_it()
    {
        Assert.Contains("If the dbdatabuild tools are available", Skill);
        Assert.Contains("You cannot start the server yourself", Skill);
        Assert.Contains("agent-kit --write --mcp", Skill);
        foreach (var screen in new[] { "plans", "lineage", "models", "health", "sample", "diff", "tests", "matrix" }) Assert.Contains($"`{screen}`", Skill);       // the screens `show` takes
    }
}
