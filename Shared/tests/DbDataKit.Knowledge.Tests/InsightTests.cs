using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN37 (the owners' worklists) and KN38 (chunk and scope preview): what the console's knowledge screens compute, by the functions the runtime uses.</summary>
public sealed class InsightTests
{
    private static KnowledgeItem Item(string id, string frontMatter, string body = "text", string owner = "data")
    {
        var result = KnowledgeItemLoader.Load($"---\nknowledge: 1\ntitle: {id}\nowner: {owner}\n{frontMatter}\n---\n{body}", id, id);
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    private const string Long = "# Guide\n\nIntro text.\n\n## Revenue\n\nRevenue is net revenue after returns and discounts.\n\n## Churn\n\nChurn is the share of customers lost in a month.\n";

    private static string Reference(string body) => $"---\nknowledge: 1\nrole: reference\nowner: data\nabout:\n  - build-project: sales\n---\n{body}";

    // ---- KN38: how an item will be split ----

    [Fact]
    public void A_long_reference_is_shown_chunk_by_chunk_with_its_heading_path_and_size()
    {
        var preview = KnowledgeInsight.Chunks(Reference(Long), "guide.md", "guide.md", options: new ChunkerOptions(TargetTokens: 20));

        Assert.Equal("reference", preview.Role);
        Assert.Empty(preview.Diagnostics);
        Assert.True(preview.Chunks.Count >= 3);
        Assert.Contains(preview.Chunks, c => c.HeadingPath.SequenceEqual(["Guide", "Revenue"]));
        Assert.All(preview.Chunks, c => Assert.True(c.Tokens > 0));
        Assert.Equal(0, preview.ChangedChunks);   // nothing to compare with: nothing is marked changed
    }

    [Fact]
    public void Editing_one_section_marks_one_chunk_changed_against_the_published_text()
    {
        var published = Reference(Long);
        var edited = Reference(Long.Replace("net revenue after returns and discounts", "net revenue after returns, discounts and tax"));

        var preview = KnowledgeInsight.Chunks(edited, "guide.md", "guide.md", published, new ChunkerOptions(TargetTokens: 20));

        var changed = Assert.Single(preview.Chunks, c => c.Changed);
        Assert.Equal(["Guide", "Revenue"], changed.HeadingPath);
        Assert.Equal(1, preview.ChangedChunks);
    }

    [Fact]
    public void A_bad_subject_is_a_diagnostic_with_its_line_and_no_chunks()
    {
        var preview = KnowledgeInsight.Chunks("---\nknowledge: 1\ntitle: X\nrole: instruction\nabout:\n  - semantic-modle: Sales\n---\ntext", "x.md", "x.md");

        Assert.Empty(preview.Chunks);
        var problem = Assert.Single(preview.Diagnostics, d => d.Severity == "error");
        Assert.Equal(6, problem.Line);
        Assert.Contains("semantic-model", problem.Message + problem.Fix);
    }

    // ---- KN38: what would be given, and what not ----

    private static readonly KnowledgeItem K1 = Item("k1.md", "role: instruction\nabout:\n  - build-project: sales-warehouse\nfor: { audience: back-end }");
    private static readonly KnowledgeItem K2 = Item("k2.md", "role: guardrail\nabout:\n  - build-project: sales-warehouse\n    folder: models/marts\nfor: { audience: back-end }");
    private static readonly KnowledgeItem K3 = Item("k3.md", "role: instruction\nabout:\n  - semantic-model: Sales\n    measure: revenue");
    private static readonly KnowledgeItem K5 = Item("k5.md", "role: reference\nabout:\n  - build-project: sales-warehouse");
    private static readonly KnowledgeItem K6 = Item("k6.md", "role: instruction\nabout:\n  - install: true\nfor: { audience: front-end }");

    [Fact]
    public void With_the_workspace_project_and_revenue_in_play_k1_k2_and_k3_come_in_order_and_k6_is_left_out_with_its_reason()
    {
        var inPlay = new[]
        {
            new InPlay(new KnowledgeSubject("build-project", ["sales-warehouse"]), Whole: true),
            new InPlay(new KnowledgeSubject("semantic-model", ["Sales"], "measure", "revenue")),
        };

        var preview = KnowledgeInsight.Scope([K1, K2, K3, K5, K6], inPlay, new ScopeAudience("back-end", "build-helper"));

        Assert.Equal(["k1.md", "k3.md"], preview.Instructions.Select(i => i.Id));
        Assert.Equal(["a project", "a member"], preview.Instructions.Select(i => i.Scope));
        Assert.Equal(["k2.md"], preview.Guardrails.Select(i => i.Id));
        Assert.Equal(["k5.md", "k6.md"], preview.Skipped.Select(s => s.Id));
        Assert.StartsWith("A reference", preview.Skipped[0].Reason);
        Assert.StartsWith("Not for this audience", preview.Skipped[1].Reason);
    }

    [Fact]
    public void An_item_about_something_not_in_play_or_gone_is_skipped_and_says_which()
    {
        var catalog = new SubjectCatalog().Add("semantic-model", "Sales").Retire("semantic-model", "Sales");

        var preview = KnowledgeInsight.Scope([K3], [new InPlay(new KnowledgeSubject("semantic-model", ["Sales"], "measure", "revenue"))], new ScopeAudience("back-end", "x"), catalog);

        Assert.Empty(preview.Instructions);
        Assert.StartsWith("Orphaned", Assert.Single(preview.Skipped).Reason);
        Assert.StartsWith("What it is about is not in play", KnowledgeInsight.Scope([K1], [], new ScopeAudience("back-end", "x")).Skipped[0].Reason);
    }

    // ---- KN37: the owners' worklists ----

    [Fact]
    public void A_dropped_model_puts_its_items_in_their_owners_worklist_as_orphaned_and_re_pointing_one_takes_it_off()
    {
        var text = "---\nknowledge: 1\ntitle: Revenue\nrole: instruction\nowner: finance\nabout:\n  - semantic-model: Sales\n---\nRevenue is net.";
        var item = KnowledgeItemLoader.Load(text, "revenue.md", "revenue.md").Item!;
        var other = Item("other.md", "role: instruction\nabout:\n  - semantic-model: Sales", owner: "ops");
        var catalog = new SubjectCatalog().Add("semantic-model", "Sales").Retire("semantic-model", "Sales").Add("semantic-model", "Revenue");

        var lists = Worklists.Build([item, other], catalog);

        Assert.Equal(["finance", "ops"], lists.Select(l => l.Owner));
        var work = Assert.Single(lists[0].Work);
        Assert.Equal(("orphaned", "revenue.md", "semantic-model:Sales"), (work.Kind, work.ItemId, work.Subject));

        var moved = KnowledgeInsight.Repoint(text, work.SubjectDetail!, "Revenue", out var problem);

        Assert.Null(problem);
        Assert.Contains("  - semantic-model: Revenue", moved);
        var again = KnowledgeItemLoader.Load(moved!, "revenue.md", "revenue.md").Item!;
        Assert.Equal("semantic-model:Revenue", again.About[0].Key);
        Assert.Empty(Worklists.Build([again], catalog));
        Assert.Equal(text.Replace("Sales", "Revenue"), moved);   // nothing else in the file changed
    }

    [Fact]
    public void Re_pointing_refuses_when_the_file_changed_since_it_was_read()
    {
        var subject = new KnowledgeSubject("semantic-model", ["Sales"], Line: 2);
        Assert.Null(KnowledgeInsight.Repoint("a\nb\nc", subject, "Revenue", out var problem));
        Assert.Contains("line 2", problem);
        Assert.Null(KnowledgeInsight.Repoint("a", subject with { Line = 9 }, "Revenue", out var beyond));
        Assert.Contains("beyond", beyond);
    }

    [Fact]
    public void Items_over_a_budget_and_marked_for_another_look_are_listed_with_their_message_and_an_item_with_no_owner_has_a_list_of_its_own()
    {
        var noOwner = KnowledgeItemLoader.Load("---\nknowledge: 1\ntitle: N\nrole: instruction\nabout:\n  - install: true\n---\ntext", "n.md", "n.md").Item!;
        var catalog = new SubjectCatalog().Add("build-project", "sales-warehouse");

        var lists = Worklists.Build([K1, noOwner], catalog, i => i.Id == "k1.md" ? "It pushes build-helper 40 tokens over its budget." : null, new HashSet<string> { "n.md" });

        Assert.Equal(["(no owner)", "data"], lists.Select(l => l.Owner));
        Assert.Equal("needs-look", lists[0].Work.Single().Kind);
        Assert.Contains("40 tokens over", lists[1].Work.Single().Message);
    }
}
