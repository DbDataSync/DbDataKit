using DbDataKit.Knowledge.Cli;
using DbDataKit.State;
using DbDataKit.State.Tests;
using static DbDataKit.Knowledge.Cli.Tests.Samples;

namespace DbDataKit.Knowledge.Cli.Tests;

/// <summary>
/// Plan 27a KN10: the kind <c>knowledge-project</c> in plan 30's registry. The life cycle (publish, stage, activate, roll back), a project with a bad item refused naming file and line, an instruction that would push an agent over its budget refused naming the agent, and the same
/// files again changing nothing, on the state database of every engine (SQLite, and SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> / <c>DBDATAKIT_TEST_POSTGRES</c> name a server).
/// </summary>
public sealed class KnowledgePublishTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T09:00:00Z");
    private static readonly string Long = string.Join(' ', Enumerable.Repeat("Revenue is net of returns and discounts and is booked when the order ships.", 12));

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    private (DefinitionPublisher Publisher, PayloadStore Payloads) Open(string engine)
    {
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module], new StateScope(Group: "main"), time: _clock);
        var payloads = new PayloadStore(database);
        return (new DefinitionPublisher(database, KnowledgeDefinitions.Registry(), payloads, "serving"), payloads);
    }

    private static KnowledgeCheckReport Check(Folder folder, params AgentBudgetInput[] agents) => KnowledgeChecks.Check(folder.Load(), "project", Catalog(), null, agents);

    // ---- no database ----

    [Fact]
    public void The_registry_has_the_kind_for_a_serving_target_staged_by_default_and_the_validator_reads_the_manifest_and_the_paths()
    {
        var kind = KnowledgeDefinitions.Registry().Require("knowledge-project");
        Assert.Equal(["serving"], kind.TargetTypes);
        Assert.Equal(PublishMode.Stage, kind.DefaultMode);
        Assert.Equal(1, kind.FormatOf!(KnowledgeDefinitions.Manifest("sales")));
        Assert.Null(kind.FormatOf!("name: sales"));

        string? Validate(string manifest, params string[] paths) => kind.Validator!(new DefinitionCandidate("knowledge-project", "sales", manifest,
            [.. paths.Select(p => new ArtifactRef(p, new string('a', 64), 10, "text/markdown"))], [], 1));
        Assert.Null(Validate(KnowledgeDefinitions.Manifest("sales", "project"), "revenue.md", "build/_knowledge.yml"));
        Assert.Contains("published as `sales`", Validate(KnowledgeDefinitions.Manifest("other"), "a.md")!);
        Assert.Contains("not a key", Validate("knowledge-project: 1\nname: sales\nflavour: x\n", "a.md")!);
        Assert.Contains("no `name`", Validate("knowledge-project: 1\n", "a.md")!);
        Assert.Contains("not install or project", Validate("knowledge-project: 1\nname: sales\nsource: moon\n", "a.md")!);
        Assert.Contains("not a file of a knowledge project", Validate(KnowledgeDefinitions.Manifest("sales"), "../outside.md")!);
        Assert.Contains("not a file of a knowledge project", Validate(KnowledgeDefinitions.Manifest("sales"), "script.sh")!);
        Assert.Contains("no files", Validate(KnowledgeDefinitions.Manifest("sales"))!);
    }

    [Fact]
    public void The_files_of_a_folder_are_the_markdown_files_and_folder_defaults_and_a_hidden_folder_is_left_out()
    {
        using var folder = new Folder().With("a.md", "x").With("sub/_knowledge.yml", "y").With("sub/b.md", "z").With(".git/c.md", "no").With("notes.txt", "no");
        Assert.Equal(["a.md", "sub/_knowledge.yml", "sub/b.md"], KnowledgeDefinitions.FilesOf(folder.Path).Select(f => f.Path));
    }

    [Fact]
    public void A_project_read_from_the_files_of_a_version_is_the_project_read_from_the_folder()
    {
        using var folder = new Folder().With("revenue.md", Item("Revenue", "Revenue means net revenue.")).With("sub/_knowledge.yml", "knowledge: 1\ndefaults:\n  owner: ops\n").With("sub/x.md", "---\nknowledge: 1\ntitle: X\nrole: instruction\nabout:\n  - semantic-model: Sales\n---\nBody.\n");
        var fromDisk = folder.Load();
        var fromFiles = KnowledgeProject.LoadFiles(folder.Path, KnowledgeDefinitions.FilesOf(folder.Path).ToDictionary(f => f.Path, f => File.ReadAllText(f.File)));

        Assert.True(fromDisk.Ok, string.Join("\n", fromDisk.Diagnostics));
        Assert.True(fromFiles.Ok, string.Join("\n", fromFiles.Diagnostics));
        Assert.Equal(fromDisk.Items.Select(i => (i.Id, i.Hash, i.Owner)), fromFiles.Items.Select(i => (i.Id, i.Hash, i.Owner)));
        Assert.Equal("ops", fromFiles.Find("sub/x.md")!.Owner);
    }

    // ---- on every engine ----

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_project_is_published_staged_activated_and_rolled_back_and_the_same_files_again_change_nothing(string engine)
    {
        var (publisher, payloads) = Open(engine);
        using var folder = new Folder().With("revenue.md", Item("Revenue", "Revenue means net revenue.")).With("sub/margin.md", Item("Margin", "Margin is revenue less cost.", role: "reference"));

        var one = KnowledgeDefinitions.Publish(publisher, new PublishArtifacts(payloads, publisher, "ada"), folder.Path, "sales", "ada", "prod", Check(folder), mode: PublishMode.Serve);
        Assert.True(one.Changed);
        Assert.Equal(1, publisher.Head("knowledge-project", "sales")!.Active);
        Assert.Equal(["revenue.md", "sub/margin.md"], publisher.Artifacts("knowledge-project", "sales", 1).Select(a => a.Path));
        Assert.Equal(["revenue.md", "sub/margin.md"], KnowledgeDefinitions.Files(publisher, payloads, "sales", 1).Keys.Order());

        // the same files again: no new version
        var again = KnowledgeDefinitions.Publish(publisher, new PublishArtifacts(payloads, publisher, "ada"), folder.Path, "sales", "ada", "prod", Check(folder), mode: PublishMode.Serve);
        Assert.False(again.Changed);
        Assert.Equal(1, again.Version.Version);

        // a changed file is a new version, staged by default and active only when it is activated; rolling back restores the first
        folder.With("revenue.md", Item("Revenue", "Revenue means net revenue, after returns."));
        var two = KnowledgeDefinitions.Publish(publisher, new PublishArtifacts(payloads, publisher, "ada"), folder.Path, "sales", "ada", "prod", Check(folder));
        Assert.Equal((2, PublishMode.Stage), (two.Version.Version, two.Mode));
        Assert.Equal((1, 2), (publisher.Head("knowledge-project", "sales")!.Active, publisher.Head("knowledge-project", "sales")!.Staged));
        Assert.Contains("after returns", KnowledgeDefinitions.Files(publisher, payloads, "sales", 2)["revenue.md"]);
        publisher.Activate("knowledge-project", "sales", "ada");
        Assert.Equal(2, publisher.Head("knowledge-project", "sales")!.Active);
        Assert.Equal(1, publisher.Rollback("knowledge-project", "sales", "ada").Version);
        Assert.DoesNotContain("after returns", KnowledgeDefinitions.Files(publisher, payloads, "sales", 1)["revenue.md"]);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_project_with_a_bad_item_is_refused_naming_file_and_line_and_nothing_is_stored(string engine)
    {
        var (publisher, payloads) = Open(engine);
        using var folder = new Folder().With("ghost.md", Item("Ghost", "About nothing real.", "  - semantic-model: Ghosts"));

        var refused = Assert.Throws<KnowledgePublishRefusedException>(() =>
            KnowledgeDefinitions.Publish(publisher, new PublishArtifacts(payloads, publisher, "ada"), folder.Path, "sales", "ada", "prod", Check(folder)));
        Assert.Contains("ghost.md:6", refused.Message);
        Assert.Contains("Ghosts", refused.Message);
        Assert.Null(publisher.Head("knowledge-project", "sales"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_instruction_that_would_push_an_agent_over_its_budget_is_refused_naming_the_agent(string engine)
    {
        var (publisher, payloads) = Open(engine);
        using var folder = new Folder().With("big.md", Item("Big", Long));

        var refused = Assert.Throws<KnowledgePublishRefusedException>(() =>
            KnowledgeDefinitions.Publish(publisher, new PublishArtifacts(payloads, publisher, "ada"), folder.Path, "sales", "ada", "prod", Check(folder, Agent())));
        Assert.Contains("'helper'", refused.Message);
        Assert.Contains("big.md", refused.Message);
        Assert.Null(publisher.Head("knowledge-project", "sales"));

        // the same text as a reference is found by search and never always given: it passes
        folder.With("big.md", Item("Big", Long, role: "reference"));
        Assert.True(KnowledgeDefinitions.Publish(publisher, new PublishArtifacts(payloads, publisher, "ada"), folder.Path, "sales", "ada", "prod", Check(folder, Agent())).Changed);
    }
}
