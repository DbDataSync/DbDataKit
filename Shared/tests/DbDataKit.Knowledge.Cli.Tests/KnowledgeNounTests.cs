using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Cli;
using DbDataKit.Knowledge.Cli;
using DbDataKit.State;
using DbDataKit.State.Tests;
using Json.Schema;
using static DbDataKit.Knowledge.Cli.Tests.Samples;

namespace DbDataKit.Knowledge.Cli.Tests;

/// <summary>Plan 27a KN16 and KN17: the noun <c>knowledge</c> (check, context, orphans, undescribed, index, search, show, about, publish) over a real index on SQLite, every document validated against its schema.</summary>
public sealed class KnowledgeNounTests : IDisposable
{
    private readonly StateEngines _engines = new();
    private readonly Folder _folder = new();
    private readonly List<(string Name, int Version)> _published = [];

    public void Dispose() { _engines.Dispose(); _folder.Dispose(); }

    private sealed class Caller(params string[] readable) : IKnowledgeCaller
    {
        public string? Audience => "back-end";

        public bool MayRead(KnowledgeSubject subject) => readable.Contains($"{subject.Type}:{subject.Name}");
    }

    private sealed class Admin(StateKnowledgeIndex index, bool mayRebuild) : IKnowledgeIndexAdmin
    {
        public int Rebuilt { get; private set; }

        public bool MayRebuild => mayRebuild;

        public IReadOnlyList<KnowledgeIndexStatus> Status(string? channel) => channel is not null && channel != index.Channel ? [] : [index.Status()];

        public Task<int> RebuildAsync(string channel, CancellationToken cancellation) { Rebuilt++; return Task.FromResult(0); }
    }

    private StateKnowledgeIndex Index() =>
        new(new KnowledgeStore(_engines.Provision("Sqlite").Open([KnowledgeModule.Module], time: new StateTestClock(DateTimeOffset.Parse("2026-11-12T09:00:00Z")))), "prod");

    private (NounRouter Router, StateKnowledgeIndex Index, Admin Admin) Build(Func<StateKnowledgeIndex, KnowledgeHost, KnowledgeHost>? tune = null, bool admin = true, params string[] readable)
    {
        var index = Index();
        var catalog = Catalog();
        var host = new KnowledgeHost
        {
            Projects = () => [new CheckTarget(_folder.Load(), "project")],
            FromFolder = path => new CheckTarget(KnowledgeProject.Load(path), "project"),
            Catalog = () => catalog,
            OwnerExists = o => o == "finance-data",
            Agents = () => [Agent()],
            Context = (agent, inPlay) => agent != "helper" ? null : ContextOf(inPlay),
            SemanticCatalogs = _ => Task.FromResult<IReadOnlyList<JsonNode>>([JsonNode.Parse("""{ "name": "Sales", "measures": { "revenue": { "description": "Net." }, "margin": {} }, "dimensions": { "region": {} } }""")!]),
            Index = new Admin(index, admin),
            Reader = new StateKnowledgeReader(_ => index, new Caller(readable), catalog, null, KnowledgeSearchMode.Words),
            DefaultChannel = "prod",
            Publish = (target, name, channel, _) => { _published.Add((name, 1)); return Task.FromResult((1, true)); },
        };
        host = tune?.Invoke(index, host) ?? host;
        return (new NounRouter("demo", [KnowledgeNoun.Create(host)]), index, (Admin)host.Index!);
    }

    private static ContextReport ContextOf(IReadOnlyList<string> inPlay) =>
        new("helper", [new ContextPartRecord("instructions", "instruction", "role.md", "v1", 50), new ContextPartRecord("bound", "bound instruction", "revenue.md", "ab12", 30)], 80, 1000, 200, false, "an estimate", inPlay,
            [new ContextPartShare("instructions", "role.md", "v1", 50, 0.25), new ContextPartShare("bound", "revenue.md", "ab12", 30, 0.15)]);

    private async Task<(int Exit, JsonElement Root, string Out, string Err)> Run(NounRouter router, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await router.RunAsync([.. args, "--format", "json"], new CliContext(output, error, TextReader.Null));
        var text = output.ToString();
        var root = text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement : default;
        if (root.ValueKind == JsonValueKind.Object && exit == 0)
            Assert.True(JsonSchema.FromText(router.OutputSchema()).Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, text);
        var errors = error + (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var e) ? string.Join("\n", e.EnumerateArray().Select(x => x.ToString())) : "");
        return (exit, root, text, errors);
    }

    private void Index(StateKnowledgeIndex index, string id, string title, string text, params string[] subjects)
    {
        var item = KnowledgeItemLoader.Load(Item(title, text, string.Join("\n", subjects.Select(s => $"  - {s}")), role: "reference"), id, id).Item!;
        index.Put(item, Chunker.Split(item), "project", "v1");
    }

    // ---- check ----

    [Fact]
    public async Task Check_passes_a_clean_folder_and_fails_an_unknown_subject_with_file_and_line()
    {
        _folder.With("revenue.md", Item("Revenue", "Revenue means net revenue."));
        var (router, _, _) = Build();
        var clean = await Run(router, "knowledge", "check");
        Assert.Equal(0, clean.Exit);
        Assert.True(clean.Root.GetProperty("data").GetProperty("ok").GetBoolean());
        Assert.Equal("helper", clean.Root.GetProperty("data").GetProperty("reaches")[0].GetProperty("agent").GetString());

        _folder.With("ghost.md", Item("Ghost", "x", "  - semantic-model: Ghosts"));
        var (router2, _, _) = Build();
        var bad = await Run(router2, "knowledge", "check");
        Assert.NotEqual(0, bad.Exit);
        var d = bad.Root.GetProperty("data").GetProperty("diagnostics").EnumerateArray().Single(x => x.GetProperty("code").GetString() == "DBD-KNW-010");
        Assert.Equal(("ghost.md", 6), (d.GetProperty("file").GetString(), d.GetProperty("line").GetInt32()));
    }

    [Fact]
    public async Task Check_of_a_folder_on_the_command_line_names_the_budget_it_would_break()
    {
        using var other = new Folder().With("big.md", Item("Big", string.Join(' ', Enumerable.Repeat("Revenue is net of returns and discounts.", 40))));
        var (router, _, _) = Build();
        var run = await Run(router, "knowledge", "check", other.Path);
        Assert.NotEqual(0, run.Exit);
        Assert.Contains("DBD-KNW-040", run.Err);
        Assert.Contains("'helper'", run.Err);
    }

    // ---- context, orphans, undescribed ----

    [Fact]
    public async Task Context_prints_the_order_and_the_tokens_per_part_and_refuses_an_unknown_agent_or_subject()
    {
        var (router, _, _) = Build();
        var run = await Run(router, "knowledge", "context", "helper", "--in-play", "semantic-model:Sales/measure:revenue");
        Assert.Equal(0, run.Exit);
        var parts = run.Root.GetProperty("data").GetProperty("parts");
        Assert.Equal(["role.md", "revenue.md"], parts.EnumerateArray().Select(p => p.GetProperty("id").GetString()));
        Assert.Equal(80, run.Root.GetProperty("data").GetProperty("total_tokens").GetInt32());
        Assert.Equal(["semantic-model:Sales/measure:revenue"], run.Root.GetProperty("data").GetProperty("in_play").EnumerateArray().Select(e => e.GetString()));

        Assert.NotEqual(0, (await Run(router, "knowledge", "context", "nobody")).Exit);
        Assert.NotEqual(0, (await Run(router, "knowledge", "context", "helper", "--in-play", "not-a-subject")).Exit);
    }

    [Fact]
    public async Task Orphans_lists_the_items_whose_subjects_are_all_gone_and_undescribed_counts_what_has_no_description()
    {
        _folder.With("alive.md", Item("Alive", "x")).With("gone.md", Item("Gone", "x", "  - semantic-model: Finance"));
        var (router, _, _) = Build((_, h) => h with { Catalog = () => Catalog().Retire("semantic-model", "Finance"), Projects = () => [new CheckTarget(_folder.Load(), "project")] });
        var orphans = await Run(router, "knowledge", "orphans");
        Assert.Equal(["gone.md"], orphans.Root.GetProperty("data").GetProperty("orphans").EnumerateArray().Select(o => o.GetProperty("id").GetString()));

        var undescribed = await Run(router, "knowledge", "undescribed");
        var model = undescribed.Root.GetProperty("data").GetProperty("models")[0];
        Assert.Equal("Sales", model.GetProperty("model").GetString());
        Assert.Equal(2, model.GetProperty("missing").GetArrayLength());
        Assert.Equal("1 measure and 1 dimension in Sales have no description.", model.GetProperty("summary").GetString());
    }

    // ---- the index ----

    [Fact]
    public async Task Index_status_reports_the_channel_and_a_rebuild_needs_the_administrator_role()
    {
        var (router, index, admin) = Build();
        Index(index, "revenue.md", "Revenue", "Revenue means net revenue.", "semantic-model: Sales");
        var status = await Run(router, "knowledge", "index", "status");
        var channel = status.Root.GetProperty("data").GetProperty("channels")[0];
        Assert.Equal(("prod", 1, "fts5"), (channel.GetProperty("channel").GetString(), channel.GetProperty("items").GetInt32(), channel.GetProperty("mode").GetString()));
        Assert.Equal(0, (await Run(router, "knowledge", "index", "rebuild", "--channel", "prod")).Exit);
        Assert.Equal(1, admin.Rebuilt);

        var (reader, _, other) = Build(admin: false);
        var refused = await Run(reader, "knowledge", "index", "rebuild");
        Assert.NotEqual(0, refused.Exit);
        Assert.Contains("administrator", refused.Err);
        Assert.Equal(0, other.Rebuilt);
    }

    // ---- search, show, about ----

    [Fact]
    public async Task Search_finds_what_the_caller_may_see_and_nothing_else_and_filters_by_subject_and_kind()
    {
        var (router, index, _) = Build(readable: "semantic-model:Sales");
        Index(index, "revenue.md", "Revenue", "# Revenue\n\nRevenue means net revenue after returns.", "semantic-model: Sales");
        Index(index, "secret.md", "Secret", "# Secret\n\nRevenue of the finance model is confidential.", "semantic-model: Finance");

        var run = await Run(router, "knowledge", "search", "revenue", "returns");
        var hits = run.Root.GetProperty("data").GetProperty("hits");
        Assert.Equal(["revenue.md"], hits.EnumerateArray().Select(h => h.GetProperty("id").GetString()));   // the Finance item is not the caller's to see
        Assert.Equal("words", run.Root.GetProperty("data").GetProperty("used").GetString());
        Assert.True(hits[0].GetProperty("chunks").GetInt32() <= 3);

        Assert.Equal(1, (await Run(router, "knowledge", "search", "revenue", "--about", "semantic-model:Sales")).Root.GetProperty("data").GetProperty("hits").GetArrayLength());
        Assert.Equal(0, (await Run(router, "knowledge", "search", "revenue", "--about", "semantic-model:Finance")).Root.GetProperty("data").GetProperty("hits").GetArrayLength());
        Assert.Equal(0, (await Run(router, "knowledge", "search", "revenue", "--kind", "guardrail")).Root.GetProperty("data").GetProperty("hits").GetArrayLength());
        Assert.NotEqual(0, (await Run(router, "knowledge", "search", "revenue", "--kind", "gossip")).Exit);
    }

    [Fact]
    public async Task Show_returns_an_item_or_one_whole_section_and_a_hidden_item_is_not_found()
    {
        var (router, index, _) = Build(readable: "semantic-model:Sales");
        Index(index, "revenue.md", "Revenue", "# Revenue\n\nIntro.\n\n## Returns\n\nReturns are taken off.\n\n## Discounts\n\nDiscounts too.", "semantic-model: Sales");
        Index(index, "secret.md", "Secret", "# Secret\n\nHidden.", "semantic-model: Finance");

        var whole = await Run(router, "knowledge", "show", "revenue.md");
        Assert.True(whole.Exit == 0, whole.Out + whole.Err);
        Assert.Contains("Returns are taken off.", whole.Root.GetProperty("data").GetProperty("text").GetString());
        Assert.Contains("Discounts too.", whole.Root.GetProperty("data").GetProperty("text").GetString());

        var section = await Run(router, "knowledge", "show", "revenue.md", "--section", "Returns");
        var text = section.Root.GetProperty("data").GetProperty("text").GetString()!;
        Assert.Contains("Returns are taken off.", text);
        Assert.DoesNotContain("Discounts too.", text);

        Assert.NotEqual(0, (await Run(router, "knowledge", "show", "secret.md")).Exit);                 // not the caller's to see: the same answer as not there
        Assert.NotEqual(0, (await Run(router, "knowledge", "show", "revenue.md", "--section", "Nope")).Exit);
        Assert.NotEqual(0, (await Run(router, "knowledge", "show", "revenue.md", "--version", "v9")).Exit);
        Assert.Equal(0, (await Run(router, "knowledge", "show", "revenue.md", "--version", "v1")).Exit);
    }

    [Fact]
    public async Task About_lists_items_about_a_subject_and_about_its_members()
    {
        var (router, index, _) = Build(readable: "semantic-model:Sales");
        Index(index, "model.md", "Model", "About the model.", "semantic-model: Sales");
        Index(index, "measure.md", "Measure", "About a measure.", "semantic-model: Sales\n    measure: revenue");
        Index(index, "other.md", "Other", "About finance.", "semantic-model: Finance");

        var run = await Run(router, "knowledge", "about", "semantic-model:Sales");
        Assert.Equal(["measure.md", "model.md"], run.Root.GetProperty("data").GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).Order());
        Assert.NotEqual(0, (await Run(router, "knowledge", "about", "nonsense")).Exit);
    }

    // ---- publish and the noun's shape ----

    [Fact]
    public async Task Publish_goes_through_the_host_and_a_noun_without_an_index_has_no_search()
    {
        var (router, _, _) = Build();
        var run = await Run(router, "knowledge", "publish", _folder.Path, "--to", "test", "--name", "sales");
        Assert.Equal(0, run.Exit);
        Assert.Equal(("sales", "test", 1), (run.Root.GetProperty("data").GetProperty("name").GetString(), run.Root.GetProperty("data").GetProperty("channel").GetString(), run.Root.GetProperty("data").GetProperty("version").GetInt32()));
        Assert.Equal([("sales", 1)], _published);

        var bare = KnowledgeNoun.Create(new KnowledgeHost());
        Assert.Equal(["check", "orphans"], bare.Verbs.Select(v => v.Name));
        var full = Build().Router;
        Assert.All(new[] { "check", "context", "orphans", "undescribed", "index", "search", "show", "about", "publish" }, verb => Assert.Contains(verb, KnowledgeNoun.Create(new KnowledgeHost
        {
            Context = (_, _) => null, SemanticCatalogs = _ => Task.FromResult<IReadOnlyList<JsonNode>>([]), Index = new Admin(Index(), false), Reader = new StateKnowledgeReader(_ => Index(), new Caller(), Catalog()),
            FromFolder = _ => null!, Publish = (_, _, _, _) => Task.FromResult((1, true)),
        }).Verbs.Select(v => v.Name)));
        Assert.NotNull(full);
    }

    [Fact]
    public void The_publishing_verb_declares_that_it_writes_and_the_read_verbs_do_not()
    {
        var noun = KnowledgeNoun.Create(new KnowledgeHost { Reader = new StateKnowledgeReader(_ => Index(), new Caller(), Catalog()), Publish = (_, _, _, _) => Task.FromResult((1, true)), FromFolder = _ => null! });
        Assert.True(noun.Verbs.Single(v => v.Name == "publish").EffectiveTraits.HasFlag(Traits.WritesDefinitions));
        foreach (var read in new[] { "search", "show", "about", "check", "orphans" })
            Assert.False(noun.Verbs.Single(v => v.Name == read).EffectiveTraits.HasFlag(Traits.WritesDefinitions), read);
    }
}
