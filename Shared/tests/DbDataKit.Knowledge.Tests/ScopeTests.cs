using DbDataKit.Config.Yaml;
using DbDataKit.Knowledge;

namespace DbDataKit.Knowledge.Tests;

/// <summary>Plan 27a KN03: scope matching, on the worked example of plan 27 section 3.5a.</summary>
public sealed class ScopeTests
{
    private static KnowledgeItem Item(string id, string frontMatter, string title = "T")
    {
        var result = KnowledgeItemLoader.Load($"---\nknowledge: 1\ntitle: {title}\nowner: data\n{frontMatter}\n---\ntext", id, id);
        Assert.True(result.Ok, string.Join('\n', result.Diagnostics));
        return result.Item!;
    }

    // K1 to K6 of the example
    private static readonly KnowledgeItem K1 = Item("k1.md", "role: instruction\nabout:\n  - build-project: sales-warehouse\nfor: { audience: back-end }");
    private static readonly KnowledgeItem K2 = Item("k2.md", "role: guardrail\nabout:\n  - build-project: sales-warehouse\n    folder: models/marts\nfor: { audience: back-end }");
    private static readonly KnowledgeItem K3 = Item("k3.md", "role: instruction\nabout:\n  - semantic-model: Sales\n    measure: revenue");
    private static readonly KnowledgeItem K4 = Item("k4.md", "role: reference\nabout:\n  - build-project: sales-warehouse\n    model: marts.dim_region");
    private static readonly KnowledgeItem K5 = Item("k5.md", "role: reference\nabout:\n  - build-project: sales-warehouse");
    private static readonly KnowledgeItem K6 = Item("k6.md", "role: instruction\nabout:\n  - install: true\nfor: { audience: front-end }");
    private static readonly KnowledgeItem[] All = [K1, K2, K3, K4, K5, K6];

    private static readonly InPlay Workspace = new(new KnowledgeSubject("build-project", ["sales-warehouse"]), Whole: true);
    private static readonly InPlay Revenue = new(new KnowledgeSubject("semantic-model", ["Sales"], "measure", "revenue"));
    private static readonly ScopeAudience BackEnd = new("back-end", "build-helper");

    private static string[] Ids(IEnumerable<KnowledgeItem> items) => [.. items.Select(i => i.Id)];

    [Fact]
    public void The_worked_example_gives_k1_and_k2_at_the_start_and_k3_once_revenue_is_in_play()
    {
        var start = ScopeMatcher.Match(All, [Workspace], BackEnd);

        Assert.Equal(["k1.md"], Ids(start.Instructions));
        Assert.Equal(["k2.md"], Ids(start.Guardrails));
        Assert.DoesNotContain("k6.md", Ids(start.All));           // for front-end agents: never loaded for a back-end agent
        Assert.DoesNotContain("k4.md", Ids(start.All));           // references are found by search, not given by scope
        Assert.DoesNotContain("k5.md", Ids(start.All));

        var later = ScopeMatcher.Match(All, [Workspace, Revenue], BackEnd);

        Assert.Equal(["k1.md", "k3.md"], Ids(later.Instructions));   // broad to narrow: the project, then the measure
        Assert.Equal(["k2.md"], Ids(later.Guardrails));
    }

    [Fact]
    public void A_front_end_agent_is_given_k6_and_not_the_back_end_items()
    {
        var bound = ScopeMatcher.Match(All, [Workspace], new ScopeAudience("front-end", "sales-helper"));

        Assert.Equal(["k6.md"], Ids(bound.Instructions));   // k1 is for back-end; k3 needs revenue in play
        Assert.Empty(bound.Guardrails);
    }

    [Fact]
    public void A_member_is_not_in_play_because_its_model_is_and_a_model_is_in_play_when_a_member_is()
    {
        var model = new InPlay(new KnowledgeSubject("semantic-model", ["Sales"]));
        Assert.Empty(ScopeMatcher.Match([K3], [model], BackEnd).Instructions);

        var wholeModelItem = Item("whole.md", "role: instruction\nabout:\n  - semantic-model: Sales");
        Assert.Equal(["whole.md"], Ids(ScopeMatcher.Match([wholeModelItem], [Revenue], BackEnd).Instructions));
        Assert.Empty(ScopeMatcher.Match([K3], [new InPlay(new KnowledgeSubject("semantic-model", ["Sales"], "measure", "margin"))], BackEnd).Instructions);
    }

    [Fact]
    public void A_folder_holds_the_folders_beneath_it_and_a_whole_project_in_play_holds_every_folder()
    {
        var deeper = new InPlay(new KnowledgeSubject("build-project", ["sales-warehouse"], "folder", "models/marts/finance"));
        var sibling = new InPlay(new KnowledgeSubject("build-project", ["sales-warehouse"], "folder", "models/staging"));
        var lookalike = new InPlay(new KnowledgeSubject("build-project", ["sales-warehouse"], "folder", "models/marts2"));

        Assert.Equal(["k2.md"], Ids(ScopeMatcher.Match([K2], [deeper], BackEnd).Guardrails));
        Assert.Empty(ScopeMatcher.Match([K2], [sibling], BackEnd).Guardrails);
        Assert.Empty(ScopeMatcher.Match([K2], [lookalike], BackEnd).Guardrails);
        Assert.Equal(["k2.md"], Ids(ScopeMatcher.Match([K2], [Workspace], BackEnd).Guardrails));
        Assert.Empty(ScopeMatcher.Match([K2], [new InPlay(new KnowledgeSubject("build-project", ["sales-warehouse"]))], BackEnd).Guardrails);   // the project alone, not "all of it"
    }

    [Fact]
    public void Install_product_tag_and_property_items_apply_where_their_subject_is_in_play_and_come_in_that_order()
    {
        var install = Item("a-install.md", "role: instruction\nabout:\n  - install: true");
        var product = Item("b-product.md", "role: instruction\nabout:\n  - product: [build, sync]");
        var tag = Item("c-tag.md", "role: instruction\nabout:\n  - tag: finance");
        var prop = Item("d-prop.md", "role: instruction\nabout:\n  - property: tier=1");
        var other = Item("e-other.md", "role: instruction\nabout:\n  - product: store");
        var nothing = Item("f-nothing.md", "role: instruction");

        var play = new List<InPlay> { new(new KnowledgeSubject("product", ["sync"])), new(new KnowledgeSubject("tag", ["finance"])), new(new KnowledgeSubject("property", ["tier=1"])) };
        var bound = ScopeMatcher.Match([nothing, prop, tag, other, product, install], play, BackEnd);

        Assert.Equal(["a-install.md", "f-nothing.md", "b-product.md", "c-tag.md", "d-prop.md"], Ids(bound.Instructions));
        Assert.Equal(["a-install.md", "f-nothing.md"], Ids(ScopeMatcher.Match([install, nothing, product], [], BackEnd).Instructions));   // with nothing in play: the install, and what is about nothing
    }

    [Fact]
    public void A_narrower_instruction_is_given_after_the_broader_one_and_the_guardrails_stay_in_a_section_of_their_own()
    {
        var narrow = Item("a-narrow.md", "role: instruction\nabout:\n  - build-project: sales-warehouse\n    model: marts.dim_region");
        var guard = Item("z-guard.md", "role: guardrail\nabout:\n  - install: true");
        var play = new InPlay(new KnowledgeSubject("build-project", ["sales-warehouse"], "model", "marts.dim_region"));

        var bound = ScopeMatcher.Match([narrow, K1, guard], [Workspace, play], BackEnd);

        Assert.Equal(["k1.md", "a-narrow.md"], Ids(bound.Instructions));
        Assert.Equal(["z-guard.md"], Ids(bound.Guardrails));
        Assert.All(bound.Guardrails, g => Assert.Equal(KnowledgeRole.Guardrail, g.Role));
        Assert.All(bound.Instructions, g => Assert.Equal(KnowledgeRole.Instruction, g.Role));
    }

    [Fact]
    public void The_same_input_always_gives_the_same_order()
    {
        var first = ScopeMatcher.Match(All.Reverse(), [Revenue, Workspace], BackEnd);
        var second = ScopeMatcher.Match(All, [Workspace, Revenue], BackEnd);

        Assert.Equal(Ids(first.All), Ids(second.All));
    }

    [Fact]
    public void An_item_for_an_agent_or_a_channel_is_given_only_to_that_agent_or_channel()
    {
        var forAgent = Item("agent.md", "role: instruction\nfor: { agent: build-helper, channel: [test] }");

        Assert.Single(ScopeMatcher.Match([forAgent], [], new ScopeAudience("back-end", "build-helper", "test")).Instructions);
        Assert.Empty(ScopeMatcher.Match([forAgent], [], new ScopeAudience("back-end", "other", "test")).Instructions);
        Assert.Empty(ScopeMatcher.Match([forAgent], [], new ScopeAudience("back-end", "build-helper", "production")).Instructions);
    }

    private static SubjectCatalog Catalog() => new SubjectCatalog()
        .Add("build-project", "sales-warehouse", "folder", "models/marts").Add("build-project", "sales-warehouse", "model", "marts.dim_region")
        .Add("semantic-model", "Sales", "measure", "revenue");

    [Fact]
    public void An_orphaned_item_reaches_nobody_and_without_a_catalog_nothing_is_known_to_be_gone()
    {
        var catalog = Catalog();
        Assert.Single(ScopeMatcher.Match([K3], [Revenue], BackEnd, catalog).Instructions);

        catalog.Retire("semantic-model", "Sales");

        Assert.Empty(ScopeMatcher.Match([K3], [Revenue], BackEnd, catalog).Instructions);
        Assert.Single(ScopeMatcher.Match([K3], [Revenue], BackEnd).Instructions);
    }
}
