using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 27a KN19: the suggestions of the <c>knowledge</c> module (its third migration) on every state engine (SQLite always; SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> and <c>DBDATAKIT_TEST_POSTGRES</c> name them):
/// a suggestion is a row and nothing else, the query that lists them is the rule of who sees which, a person withdraws their own while it is undecided, and the owner's decision moves it only from the state it expects.
/// </summary>
public sealed class SuggestionStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-12T09:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private SuggestionStore Open(string engine, StateTestClock? clock = null) => new(_engines.Provision(engine).Open([KnowledgeModule.Module], time: clock ?? new StateTestClock(Start)));

    private static SuggestionRow Add(SuggestionStore store, string by = "maria", string? owner = "finance-data", string item = "revenue.md", string via = "person", string? preview = null) =>
        store.Add("project", item, SuggestionKinds.Change, "Revenue", "---\nknowledge: 1\n---\nnew text", "abc", "because", by, via, owner, preview);

    [Fact]
    public void The_module_has_a_table_for_suggestions_and_follows_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(KnowledgeModule.Module));
        Assert.Equal(["id"], StateTableDefinitions.Read(KnowledgeModule.Module).Single(t => t.Name == "suggestion").PrimaryKey);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_suggestion_is_kept_with_everything_it_carries(string engine)
    {
        var store = Open(engine);

        var added = Add(store, preview: """{"row_count":3}""");
        var read = store.Get(added.Id)!;

        Assert.Equal(SuggestionStates.Open, read.State);
        Assert.Equal(("project", "revenue.md", "change", "Revenue", "abc", "because", "maria", "person", "finance-data"), (read.Project, read.ItemId, read.Kind, read.Title, read.BaseVersion, read.Reason, read.SuggestedBy, read.Via, read.Owner));
        Assert.Equal("---\nknowledge: 1\n---\nnew text", read.Proposed);
        Assert.Equal("""{"row_count":3}""", read.Preview);
        Assert.Equal((Start, Start), (read.CreatedAt, read.UpdatedAt));
        Assert.Null(read.DecidedBy);
        Assert.Null(store.Get("nothing"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_person_sees_their_own_and_those_for_the_groups_that_own_the_item_and_nothing_else(string engine)
    {
        var clock = new StateTestClock(Start);
        var store = Open(engine, clock);
        var mine = Add(store, "maria", "finance-data");
        clock.Advance(TimeSpan.FromMinutes(1));
        var his = Add(store, "sam", "finance-data", "margin.md");
        clock.Advance(TimeSpan.FromMinutes(1));
        var other = Add(store, "sam", "sales-ops", "orders.md");
        clock.Advance(TimeSpan.FromMinutes(1));
        var noOwner = Add(store, "sam", null, "new.md");

        Assert.Equal([mine.Id], store.Visible("maria", []).Select(s => s.Id));
        Assert.Equal([his.Id, mine.Id], store.Visible("olga", ["finance-data"]).Select(s => s.Id));                 // newest first
        Assert.Equal([noOwner.Id, other.Id, his.Id], store.Visible("sam", []).Select(s => s.Id).ToList());
        Assert.Equal([other.Id], store.Visible("zed", ["sales-ops"]).Select(s => s.Id));
        Assert.Empty(store.Visible("zed", ["marketing"]));
        // a person in the owning group who also made one sees it once
        Assert.Equal([mine.Id], store.Visible("maria", ["finance-data"]).Where(s => s.SuggestedBy == "maria").Select(s => s.Id));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_state_narrows_the_list_and_a_person_withdraws_only_their_own_undecided_suggestion(string engine)
    {
        var store = Open(engine);
        var mine = Add(store, "maria");
        var his = Add(store, "sam");

        Assert.False(store.Withdraw(his.Id, "maria"));
        Assert.True(store.Withdraw(mine.Id, "maria"));
        Assert.False(store.Withdraw(mine.Id, "maria"));
        Assert.Equal(SuggestionStates.Withdrawn, store.Get(mine.Id)!.State);
        Assert.Equal("maria", store.Get(mine.Id)!.DecidedBy);
        Assert.NotNull(store.Get(mine.Id)!.DecidedAt);

        Assert.Equal([mine.Id], store.Visible("maria", [], SuggestionStates.Withdrawn).Select(s => s.Id));
        Assert.Empty(store.Visible("maria", [], SuggestionStates.Open));
        Assert.Equal([his.Id], store.InState(SuggestionStates.Open).Select(s => s.Id));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_decision_moves_a_suggestion_only_from_the_state_it_expects_and_a_decided_one_cannot_be_withdrawn(string engine)
    {
        var store = Open(engine);
        var suggestion = Add(store);

        Assert.False(store.Decide(suggestion.Id, SuggestionStates.NeedsAnotherLook, SuggestionStates.Approved, "olga", null));
        Assert.True(store.Decide(suggestion.Id, SuggestionStates.Open, SuggestionStates.NeedsAnotherLook, "workspace", "the item moved on"));
        Assert.True(store.Withdraw(suggestion.Id, "maria"));      // still undecided: waiting for another look

        var second = Add(store);
        Assert.True(store.Decide(second.Id, SuggestionStates.Open, SuggestionStates.Approved, "olga", "yes"));
        Assert.False(store.Withdraw(second.Id, "maria"));
        Assert.Equal(("olga", "yes"), (store.Get(second.Id)!.DecidedBy, store.Get(second.Id)!.Note));
        Assert.Throws<ArgumentException>(() => store.Decide(second.Id, SuggestionStates.Approved, "lost", "x", null));
    }

    [Theory, MemberData(nameof(Engines))]
    public void What_is_missing_is_refused_before_anything_is_written(string engine)
    {
        var store = Open(engine);

        Assert.Throws<ArgumentException>(() => store.Add("project", "a.md", "change", "T", "text", null, "", "maria", "person", null));
        Assert.Throws<ArgumentException>(() => store.Add("project", "a.md", "mystery", "T", "text", null, "why", "maria", "person", null));
        Assert.Empty(store.InState(SuggestionStates.Open));
    }
}
