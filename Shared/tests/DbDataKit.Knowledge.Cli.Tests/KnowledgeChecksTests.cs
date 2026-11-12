using DbDataKit.Config.Yaml;
using DbDataKit.Knowledge.Cli;
using static DbDataKit.Knowledge.Cli.Tests.Samples;

namespace DbDataKit.Knowledge.Cli.Tests;

/// <summary>Plan 27a KN10 and KN17: what a check says about a project before it is published.</summary>
public sealed class KnowledgeChecksTests
{
    private static readonly string Long = string.Join(' ', Enumerable.Repeat("Revenue is net of returns and discounts and is booked when the order ships.", 12));

    [Fact]
    public void A_clean_project_passes_and_says_what_each_agent_would_be_given()
    {
        using var folder = new Folder().With("revenue.md", Item("Revenue", "Revenue means net revenue."));
        var report = KnowledgeChecks.Check(folder.Load(), "project", Catalog(), _ => true, [Agent()]);

        Assert.True(report.Ok, string.Join("\n", report.Diagnostics));
        var reach = Assert.Single(report.Reaches);
        Assert.Equal("helper", reach.Agent);
        Assert.False(reach.PushesOver);
        Assert.True(reach.Adds > 0 && reach.Used > 0 && reach.Budget == 200);
        Assert.Equal(["revenue.md"], reach.Items);
    }

    [Fact]
    public void A_bad_item_is_refused_naming_file_and_line_and_an_unknown_subject_at_its_own_line()
    {
        using var folder = new Folder()
            .With("bad.md", "---\nknowledge: 1\nrole: wizard\nowner: finance-data\n---\ntext\n")
            .With("sub/ghost.md", Item("Ghost", "About a model nobody has.", "  - semantic-model: Ghosts"));
        var report = KnowledgeChecks.Check(folder.Load(), "project", Catalog(), null, []);

        Assert.False(report.Ok);
        var role = report.Diagnostics.First(d => d.Path == "bad.md" && d.Severity == ConfigSeverity.Error);
        Assert.True(role.Line > 0);
        var ghost = report.Diagnostics.Single(d => d.Code == SubjectResolver.UnknownSubject);
        Assert.Equal("sub/ghost.md", ghost.Path);
        Assert.Equal(6, ghost.Line);   // the subject's own line in the file
        Assert.Contains("Ghosts", ghost.Message);
    }

    [Fact]
    public void An_owner_that_is_not_a_group_is_an_error_and_one_that_is_passes()
    {
        using var folder = new Folder().With("revenue.md", Item("Revenue", "Revenue means net revenue.", owner: "nobody"));

        var refused = KnowledgeChecks.Check(folder.Load(), "project", Catalog(), o => o == "finance-data", []);
        Assert.Equal(KnowledgeChecks.UnknownOwner, Assert.Single(refused.Diagnostics, d => d.Severity == ConfigSeverity.Error).Code);
        Assert.True(KnowledgeChecks.Check(folder.Load(), "project", Catalog(), _ => true, []).Ok);
    }

    [Fact]
    public void An_instruction_that_would_push_an_agent_over_its_budget_is_refused_naming_the_agent_and_the_item()
    {
        using var folder = new Folder()
            .With("small.md", Item("Small", "Say net."))
            .With("big.md", Item("Big", Long));
        var report = KnowledgeChecks.Check(folder.Load(), "project", Catalog(), null, [Agent()]);

        Assert.False(report.Ok);
        var over = Assert.Single(report.Diagnostics, d => d.Code == KnowledgeChecks.OverBudget);
        Assert.Equal("big.md", over.Path);
        Assert.True(over.Line > 1);
        Assert.Contains("'helper'", over.Message);
        Assert.Contains("'big.md'", over.Message);
        Assert.True(Assert.Single(report.Reaches).PushesOver);
    }

    [Fact]
    public void A_reference_is_found_by_search_and_never_counts_against_the_budget_and_an_agent_it_does_not_reach_is_not_listed()
    {
        using var folder = new Folder().With("big.md", Item("Big", Long, role: "reference"));
        var report = KnowledgeChecks.Check(folder.Load(), "project", Catalog(), null, [Agent(), Agent("other", scope: "semantic-models: [Finance]")]);

        Assert.True(report.Ok, string.Join("\n", report.Diagnostics));
        Assert.Empty(report.Reaches);   // a reference is never always given; neither agent is given anything of it
    }

    [Fact]
    public void An_agent_that_is_over_before_the_project_adds_anything_is_a_warning_not_an_error()
    {
        using var folder = new Folder().With("small.md", Item("Small", "Say net."));
        var report = KnowledgeChecks.Check(folder.Load(), "project", Catalog(), null, [Agent(fixedTokens: 400)]);

        Assert.True(report.Ok);
        Assert.Contains(report.Diagnostics, d => d.Code == KnowledgeChecks.AlreadyOver && d.Severity == ConfigSeverity.Warning);
    }
}
