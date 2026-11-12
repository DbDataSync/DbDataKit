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

/// <summary>
/// Plan 27a KN19 and KN23: suggestions at the edge (written with no workspace server anywhere, read strictly first, seen by their person and by the owner of the item, withdrawn by their person) and a verified query suggested from an
/// answer (the request, the values, the model version and a preview of the result; from an agent only after a person confirmed the answer, and only after the approval its traits ask for).
/// </summary>
public sealed class SuggestionTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T09:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private sealed class Access : IKnowledgeCaller
    {
        public string? Audience => "back-end";

        public bool MayRead(KnowledgeSubject subject) => subject.Type == "semantic-model" && subject.Name == "Sales";
    }

    private sealed class Caller(string person, string? agent = null, params string[] groups) : ISuggestionCaller
    {
        public HashSet<string> Confirmed { get; } = [];

        public string Person { get; } = person;

        public string? Agent { get; } = agent;

        public IReadOnlyCollection<string> Groups { get; } = groups;

        public bool AnswerConfirmed(string answerKey) => Confirmed.Contains(answerKey);
    }

    private sealed class Model : IVerifiedQueryReader
    {
        public bool Fails { get; set; }

        public List<string> Statements { get; } = [];

        public VerifiedQueryView? Find(string id, string channel) => null;

        public IReadOnlyList<VerifiedQueryView> List(string channel, string? model) => [];

        public string? CurrentModelVersion(string model) => "7";

        public Task<VerifiedAnswer> RunAsync(string model, string statement, int maxRows, CancellationToken cancellation)
        {
            Statements.Add(statement);
            if (Fails) throw new InvalidOperationException("column region.name is not in Sales");
            IReadOnlyList<IReadOnlyList<object?>> rows = [["EMEA", 120.5], ["APAC", 80.0], ["AMER", null]];
            return Task.FromResult(new VerifiedAnswer([("region", "string"), ("revenue", "double")], rows, false, new Dictionary<string, string>()));
        }
    }

    private const string Revenue = "---\nknowledge: 1\ntitle: Revenue\nrole: instruction\nabout:\n  - semantic-model: Sales\nowner: finance-data\n---\nRevenue is net revenue.\n";

    private const string RequestJson = """{"kind":"query","query":{"measures":["revenue"],"attributes":["region.name"],"limit":10}}""";

    private (StateKnowledgeIndex Index, SuggestionStore Store) Open(string engine = "Sqlite")
    {
        var database = _engines.Provision(engine).Open([KnowledgeModule.Module], time: new StateTestClock(Start));
        var index = new StateKnowledgeIndex(new KnowledgeStore(database), "prod");
        foreach (var text in new[] { Revenue, Revenue.Replace("title: Revenue", "title: Margin", StringComparison.Ordinal).Replace("about:\n  - semantic-model: Sales", "about:\n  - semantic-model: Finance", StringComparison.Ordinal) })
        {
            var id = text.Contains("Margin", StringComparison.Ordinal) ? "margin.md" : "revenue.md";
            var item = KnowledgeItemLoader.Load(text, id, id).Item!;
            index.Put(item, Chunker.Split(item), "project", "v1");
        }
        return (index, new SuggestionStore(database));
    }

    private static SuggestionDesk Desk(SuggestionStore store, StateKnowledgeIndex index, Caller caller, Model? model = null) =>
        new(store, index, caller, new Access(), Catalog(), model, _ => "finance-data", new StateTestClock(Start));

    private static NounRouter Router(ISuggestionDesk desk) => new("demo", [KnowledgeNoun.Create(new KnowledgeHost { Suggestions = desk })]);

    private static async Task<(int Exit, JsonElement Root, string Err)> Run(NounRouter router, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await router.RunAsync([.. args, "--format", "json"], new CliContext(output, error, TextReader.Null));
        var text = output.ToString();
        var root = text.TrimStart().StartsWith('{') ? JsonDocument.Parse(text).RootElement : default;
        if (root.ValueKind == JsonValueKind.Object && exit == 0)
            Assert.True(JsonSchema.FromText(router.OutputSchema()).Evaluate(root, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, text);
        return (exit, root, error + (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("errors", out var e) ? string.Join("\n", e.EnumerateArray().Select(x => x.ToString())) : ""));
    }

    private static string Changed => Revenue.Replace("Revenue is net revenue.", "Revenue is net revenue after returns.", StringComparison.Ordinal);

    // ---- KN19 ----

    [Fact]
    public async Task A_suggestion_is_stored_with_no_workspace_server_and_carries_the_item_its_base_version_its_owner_and_who_made_it()
    {
        var (index, store) = Open();
        var router = Router(Desk(store, index, new Caller("maria")));

        var (exit, root, _) = await Run(router, "knowledge", "suggest", "revenue.md", "--reason", "Returns are netted out too", "--text", Changed);

        Assert.Equal(0, exit);
        var row = Assert.Single(store.InState(SuggestionStates.Open));
        Assert.Equal(row.Id, root.GetProperty("data").GetProperty("suggestion").GetProperty("id").GetString());
        Assert.Equal(("project", "revenue.md", "change", "maria", "person", "finance-data"), (row.Project, row.ItemId, row.Kind, row.SuggestedBy, row.Via, row.Owner));
        Assert.Equal(index.Store.Item("prod", "revenue.md")!.Hash, row.BaseVersion);
        Assert.Equal(Changed, row.Proposed);
        Assert.Equal("Returns are netted out too", row.Reason);
    }

    [Fact]
    public async Task A_new_item_is_suggested_with_its_place_and_project_and_may_come_on_the_standard_input()
    {
        var (index, store) = Open();
        var desk = Desk(store, index, new Caller("maria"));
        var (output, error) = (new StringWriter(), new StringWriter());

        var exit = await Router(desk).RunAsync(["knowledge", "suggest", "--new", "guides/refunds.md", "--project", "project", "--reason", "Nobody wrote it down", "--format", "json"],
            new CliContext(output, error, new StringReader("---\nknowledge: 1\nrole: reference\ntitle: Refunds\nowner: finance-data\n---\nRefunds go back to the card.\n")));

        Assert.Equal(0, exit);
        var row = Assert.Single(store.InState(SuggestionStates.Open));
        Assert.Equal(("guides/refunds.md", "new", null), (row.ItemId, row.Kind, row.BaseVersion));
        Assert.Equal("finance-data", row.Owner);
    }

    [Theory]
    [InlineData("revenue.md", "", "Why")]                                    // no reason
    [InlineData("revenue.md", "x", "nothing to suggest")]                    // the text is the item as it is
    [InlineData("nothing.md", "x", "no item 'nothing.md' that you may see")] // not there
    [InlineData("margin.md", "x", "no item 'margin.md' that you may see")]   // there, but not for this person: the same answer
    [InlineData("generated:semantic-model:Sales/measure:revenue", "x", "generated from a definition")]
    public async Task A_suggestion_that_cannot_be_made_says_why_and_keeps_nothing(string item, string reason, string words)
    {
        var (index, store) = Open();
        var text = item == "revenue.md" && words.StartsWith("nothing", StringComparison.Ordinal) ? Revenue : Changed;

        var (exit, _, error) = await Run(Router(Desk(store, index, new Caller("maria"))), "knowledge", "suggest", item, "--reason", reason, "--text", text);

        Assert.NotEqual(0, exit);
        Assert.Contains(words, error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(store.InState(SuggestionStates.Open));
    }

    [Fact]
    public async Task A_text_that_does_not_read_is_refused_with_its_line_so_no_owner_reviews_a_broken_file()
    {
        var (index, store) = Open();
        var broken = Changed.Replace("role: instruction", "role: policy", StringComparison.Ordinal);

        var (exit, _, error) = await Run(Router(Desk(store, index, new Caller("maria"))), "knowledge", "suggest", "revenue.md", "--reason", "x", "--text", broken);

        Assert.NotEqual(0, exit);
        Assert.Contains("does not read as a knowledge item", error, StringComparison.Ordinal);
        Assert.Contains("line 4", error, StringComparison.Ordinal);
        Assert.Empty(store.InState(SuggestionStates.Open));
    }

    [Fact]
    public async Task A_person_sees_only_their_own_suggestions_and_the_owner_of_an_item_sees_those_for_it()
    {
        var (index, store) = Open();
        await Run(Router(Desk(store, index, new Caller("maria"))), "knowledge", "suggest", "revenue.md", "--reason", "mine", "--text", Changed);
        await Run(Router(Desk(store, index, new Caller("sam"))), "knowledge", "suggest", "revenue.md", "--reason", "his", "--text", Changed.Replace("returns", "returns and VAT", StringComparison.Ordinal));
        var maria = Desk(store, index, new Caller("maria"));
        var owner = Desk(store, index, new Caller("olga", null, "finance-data"));
        var stranger = Desk(store, index, new Caller("zed", null, "sales-ops"));

        Assert.Equal(["maria"], maria.List(null).Select(s => s.SuggestedBy));
        Assert.Equal(["maria", "sam"], owner.List(null).Select(s => s.SuggestedBy).Order());
        Assert.Empty(stranger.List(null));
        // showing one is the same rule: the owner sees maria's, sam does not, and a stranger gets the answer a missing one gets
        var id = maria.List(null).Single().Id;
        Assert.NotNull(owner.Show(id));
        Assert.Null(Desk(store, index, new Caller("sam")).Show(id));
        Assert.Null(stranger.Show(id));
        Assert.Null(owner.Show("no-such-id"));
    }

    [Fact]
    public async Task The_commands_list_and_show_a_suggestion_and_say_when_the_item_has_moved_on()
    {
        var (index, store) = Open();
        var router = Router(Desk(store, index, new Caller("maria")));
        await Run(router, "knowledge", "suggest", "revenue.md", "--reason", "mine", "--text", Changed);

        var list = await Run(router, "knowledge", "suggestions", "list");
        var id = list.Root.GetProperty("data").GetProperty("suggestions")[0].GetProperty("id").GetString()!;
        Assert.False(list.Root.GetProperty("data").GetProperty("suggestions")[0].GetProperty("moved").GetBoolean());

        // the item is published again with other text: the suggestion was based on the old one
        var item = KnowledgeItemLoader.Load(Revenue.Replace("net revenue.", "net revenue, VAT excluded.", StringComparison.Ordinal), "revenue.md", "revenue.md").Item!;
        index.Put(item, Chunker.Split(item), "project", "v2");

        var shown = await Run(router, "knowledge", "suggestions", "show", id);
        Assert.True(shown.Root.GetProperty("data").GetProperty("suggestion").GetProperty("moved").GetBoolean());
        Assert.Equal(Changed, shown.Root.GetProperty("data").GetProperty("suggestion").GetProperty("proposed").GetString());
        Assert.Equal(1, (await Run(router, "knowledge", "suggestions", "list", "--state", "open")).Root.GetProperty("data").GetProperty("suggestions").GetArrayLength());
        Assert.Equal(0, (await Run(router, "knowledge", "suggestions", "list", "--state", "approved")).Root.GetProperty("data").GetProperty("suggestions").GetArrayLength());
    }

    [Fact]
    public async Task A_person_withdraws_their_own_suggestion_once_and_nobody_withdraws_another_persons()
    {
        var (index, store) = Open();
        await Run(Router(Desk(store, index, new Caller("maria"))), "knowledge", "suggest", "revenue.md", "--reason", "mine", "--text", Changed);
        var id = store.InState(SuggestionStates.Open).Single().Id;

        var other = await Run(Router(Desk(store, index, new Caller("olga", null, "finance-data"))), "knowledge", "suggestions", "withdraw", id);
        Assert.NotEqual(0, other.Exit);                                     // the owner decides; she does not withdraw for the person
        Assert.Equal(SuggestionStates.Open, store.Get(id)!.State);

        var mine = Router(Desk(store, index, new Caller("maria")));
        Assert.Equal(0, (await Run(mine, "knowledge", "suggestions", "withdraw", id)).Exit);
        Assert.Equal((SuggestionStates.Withdrawn, "maria"), (store.Get(id)!.State, store.Get(id)!.DecidedBy));
        Assert.NotEqual(0, (await Run(mine, "knowledge", "suggestions", "withdraw", id)).Exit);   // already withdrawn
    }

    [Fact]
    public async Task An_agents_suggestion_needs_the_approval_its_traits_ask_for_and_is_marked_as_the_agents()
    {
        var (index, store) = Open();
        var desk = Desk(store, index, new Caller("maria", "sales-helper"));
        var router = Router(desk);
        var tool = CommandTools.From(router, new McpOptions(McpOptions.Always, AllowWrites: true)).Single(t => t.Name == "knowledge_suggest");
        var args = new JsonObject { ["args"] = new JsonArray("revenue.md", "--reason", "A user asked", "--text", Changed) };
        var context = new AgentRunContext("maria", "sales-helper", "1", "conv-1");

        // the traits of the command (it writes definitions) ask once: a person who says no keeps the agent from writing anything
        var no = new ApprovalGate(new ScriptedApprover(false), PromptingPolicy.None);
        var denied = await no.BeforeCallAsync(new ToolCall("1", tool.Name, args), tool, context, 1, default);
        Assert.False(denied.Allowed);
        Assert.Empty(store.InState(SuggestionStates.Open));

        var yes = new ScriptedApprover(true);
        var allowed = await new ApprovalGate(yes, PromptingPolicy.None).BeforeCallAsync(new ToolCall("2", tool.Name, args), tool, context, 1, default);
        Assert.True(allowed.Allowed);
        Assert.Contains("writes-definitions", Assert.Single(yes.Asked).Preview, StringComparison.Ordinal);
        var outcome = await tool.InvokeAsync(args, default);
        Assert.False(outcome.IsError, outcome.Text);

        var row = Assert.Single(store.InState(SuggestionStates.Open));
        Assert.Equal(("maria", "agent:sales-helper"), (row.SuggestedBy, row.Via));
        Assert.True(row.FromAgent);
    }

    [Fact]
    public void An_agent_cannot_name_a_file_for_the_request_of_a_verify_or_read_one_off_the_disk()
    {
        var (index, store) = Open();
        var agent = Desk(store, index, new Caller("maria", "sales-helper"));

        Assert.True(agent.ForAgent);
        Assert.False(Desk(store, index, new Caller("maria")).ForAgent);
    }

    // ---- KN23 ----

    private static async Task<(int Exit, JsonElement Root, string Err)> Verify(NounRouter router, string request = RequestJson, params string[] more) =>
        await Run(router, ["knowledge", "verify", "--question", "What was revenue by region?", "--model", "Sales", "--request", request, .. more]);

    [Fact]
    public async Task A_verified_query_is_suggested_from_an_answer_with_the_request_the_values_the_model_version_and_a_preview_of_the_result()
    {
        var (index, store) = Open();
        var model = new Model();
        var router = Router(Desk(store, index, new Caller("maria"), model));

        var (exit, _, error) = await Verify(router, RequestJson, "--param", "quarter=FY27-Q1");

        Assert.Equal(0, exit);
        Assert.Equal("", error.Trim());
        var row = Assert.Single(store.InState(SuggestionStates.Open));
        Assert.Equal((SuggestionKinds.VerifiedQuery, "maria", "person"), (row.Kind, row.SuggestedBy, row.Via));
        Assert.StartsWith("queries/what-was-revenue-by-region", row.ItemId, StringComparison.Ordinal);
        Assert.Equal("finance-data", row.Owner);

        // the proposed file is a verified query the strict reader reads: the question, the model, the request as it was run, and who verified it against which version
        var item = KnowledgeItemLoader.Load(row.Proposed, row.ItemId, row.ItemId).Item!;
        Assert.Equal(["What was revenue by region?"], item.Verified!.Questions);
        Assert.Equal("Sales", item.Verified.Model);
        Assert.Equal(new Verification("maria", "2026-11-12", "7"), item.Verified.Verified);
        Assert.Equal("revenue", item.Verified.Request!["query"]!["measures"]![0]!.GetValue<string>());

        // the preview is what the person saw: columns, the first rows, the values given and the model version
        var preview = JsonNode.Parse(row.Preview!)!;
        Assert.Equal(["region", "revenue"], preview["columns"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()));
        Assert.Equal(3, preview["row_count"]!.GetValue<int>());
        Assert.Equal("EMEA", preview["rows"]![0]![0]!.GetValue<string>());
        Assert.Equal("FY27-Q1", preview["parameters"]!["quarter"]!.GetValue<string>());
        Assert.Equal("7", preview["model_version"]!.GetValue<string>());
        Assert.Equal("FY27-Q1", JsonNode.Parse(Assert.Single(model.Statements))!["parameters"]!["quarter"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_agent_may_verify_an_answer_only_after_a_person_has_confirmed_it()
    {
        var (index, store) = Open();
        var agent = new Caller("maria", "sales-helper");
        var router = Router(Desk(store, index, agent, new Model()));

        var refused = await Verify(router);
        Assert.NotEqual(0, refused.Exit);
        Assert.Contains("A person has not confirmed this answer", refused.Err, StringComparison.Ordinal);
        Assert.Empty(store.InState(SuggestionStates.Open));

        // a confirmation of another answer does not count; this answer's does, whatever the order of the keys in the request
        agent.Confirmed.Add(SuggestionDesk.AnswerKey("Another question?", JsonNode.Parse(RequestJson)!.AsObject()));
        Assert.NotEqual(0, (await Verify(router)).Exit);
        agent.Confirmed.Add(SuggestionDesk.AnswerKey("What was revenue by region?", JsonNode.Parse("""{"query":{"limit":10,"attributes":["region.name"],"measures":["revenue"]},"kind":"query"}""")!.AsObject()));
        var accepted = await Verify(router);

        Assert.Equal(0, accepted.Exit);
        var row = Assert.Single(store.InState(SuggestionStates.Open));
        Assert.Equal(("maria", "agent:sales-helper"), (row.SuggestedBy, row.Via));
        Assert.Contains("confirmed by maria", row.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_that_does_not_run_for_the_person_or_is_not_a_query_cannot_be_verified()
    {
        var (index, store) = Open();
        var model = new Model { Fails = true };
        var router = Router(Desk(store, index, new Caller("maria"), model));

        var failing = await Verify(router);
        var notAQuery = await Verify(Router(Desk(store, index, new Caller("maria"), new Model())), """{"kind":"clarify"}""");
        var noRunner = await Verify(Router(Desk(store, index, new Caller("maria"))));

        Assert.Contains("does not run for you", failing.Err, StringComparison.Ordinal);
        Assert.Contains("column region.name is not in Sales", failing.Err, StringComparison.Ordinal);
        Assert.Contains("`kind: query`", notAQuery.Err, StringComparison.Ordinal);
        Assert.Contains("cannot run a query", noRunner.Err, StringComparison.Ordinal);
        Assert.Empty(store.InState(SuggestionStates.Open));
    }

    [Fact]
    public async Task A_request_file_is_read_for_a_person_and_refused_for_an_agent()
    {
        var (index, store) = Open();
        var path = Path.Combine(Path.GetTempPath(), "kn-verify-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        await File.WriteAllTextAsync(path, RequestJson);
        try
        {
            var person = await Verify(Router(Desk(store, index, new Caller("maria"), new Model())), "@" + path);
            var agent = await Verify(Router(Desk(store, index, new Caller("maria", "sales-helper"), new Model())), "@" + path);

            Assert.Equal(0, person.Exit);
            Assert.NotEqual(0, agent.Exit);
            Assert.Contains("a file cannot be named here", agent.Err, StringComparison.Ordinal);
        }
        finally { File.Delete(path); }
    }
}
