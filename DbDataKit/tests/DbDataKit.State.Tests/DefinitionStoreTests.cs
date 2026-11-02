namespace DbDataKit.State.Tests;

/// <summary>Published definitions (plan 20, O03) on SQLite and, when the shared engines are up, SQL Server and PostgreSQL.</summary>
public sealed class DefinitionStoreTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private DefinitionStore Open(string engine, out StateDatabase database)
    {
        database = _engines.Open(engine, new StateSchema([DefinitionSchema.Tables]));
        return new DefinitionStore(database);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_publish_is_served_at_once_and_counts_versions_up(string engine)
    {
        var store = Open(engine, out _);
        Assert.Null(store.Current("app", "crm"));

        var one = store.Publish("app", "crm", "v1 text", "ada", ["model:sales@3"], "first");
        var two = store.Publish("app", "crm", "v2 text", "ada");

        Assert.Equal((1, 2), (one.Version, two.Version));
        Assert.Equal(["model:sales@3"], one.DependsOn);
        Assert.Equal("first", one.Note);
        Assert.Equal(2, store.Current("app", "crm")!.Version);
        Assert.Equal(["v1 text", "v2 text"], store.Versions("app", "crm").Select(v => v.Content));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_version_that_fails_validation_is_not_stored_and_the_old_one_keeps_serving(string engine)
    {
        var store = Open(engine, out _);
        store.Publish("app", "crm", "good", "ada");

        var refused = Assert.Throws<DefinitionRejectedException>(() => store.Publish("app", "crm", "bad", "ada", validate: text => text == "bad" ? "the text is bad" : null));

        Assert.Equal("the text is bad", refused.Message);
        Assert.Equal("good", store.Current("app", "crm")!.Content);
        Assert.Single(store.Versions("app", "crm"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Publishing_what_is_already_served_changes_nothing(string engine)
    {
        var store = Open(engine, out _);
        store.Publish("app", "crm", "same", "ada");

        var again = store.Publish("app", "crm", "same", "bob");

        Assert.Equal(1, again.Version);
        Assert.Single(store.Versions("app", "crm"));
        Assert.Single(store.Changes());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_rollback_returns_to_the_last_good_version_and_a_second_one_goes_forward_again(string engine)
    {
        var store = Open(engine, out _);
        store.Publish("app", "crm", "one", "ada");
        store.Publish("app", "crm", "two", "ada");

        Assert.Equal(1, store.Rollback("app", "crm", "ada").Version);
        Assert.Equal("one", store.Current("app", "crm")!.Content);
        Assert.Equal(2, store.Rollback("app", "crm", "ada").Version);

        var fresh = new DefinitionStore(_engines.Open(engine, new StateSchema([DefinitionSchema.Tables])));
        fresh.Publish("app", "only", "x", "ada");
        Assert.Throws<DefinitionNotFoundException>(() => fresh.Rollback("app", "only", "ada"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_channel_serves_its_own_version_and_a_switch_moves_only_that_channel(string engine)
    {
        var store = Open(engine, out _);
        store.Publish("app", "crm", "one", "ada");
        store.Publish("app", "crm", "two", "ada");

        store.Switch("app", "crm", 1, "ada", channel: "preview");

        Assert.Equal(1, store.Current("app", "crm", "preview")!.Version);
        Assert.Equal(2, store.Current("app", "crm")!.Version);
        Assert.Throws<DefinitionNotFoundException>(() => store.Switch("app", "crm", 9, "ada", channel: "preview"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Another_instance_on_the_same_state_serves_what_was_published_and_learns_what_changed(string engine)
    {
        var publisher = Open(engine, out var database);
        var other = new DefinitionStore(database);
        publisher.Publish("app", "crm", "one", "ada");
        var seen = other.Changes();
        Assert.Equal(["publish"], seen.Select(e => e.Action));

        publisher.Publish("app", "crm", "two", "ada");
        publisher.Rollback("app", "crm", "ada");

        var news = other.Changes(seen[^1].Id);
        Assert.Equal(["publish", "rollback"], news.Select(e => e.Action));
        Assert.Equal("one", other.Current("app", "crm")!.Content);
        Assert.Equal(["crm"], other.CurrentOfKind("app").Select(d => d.Name));
    }

    [Fact]
    public void Names_are_plain()
    {
        var store = Open(StateEngineIds.Sqlite, out _);
        Assert.Throws<ArgumentException>(() => store.Publish("app", "Not Plain", "x", "ada"));
        Assert.Throws<ArgumentException>(() => store.Publish("app", "crm", "x", "ada", channel: "../x"));
    }
}
