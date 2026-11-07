namespace DbDataKit.State.Tests;

/// <summary>Published definitions on the module (plan 29a Q08): the older store's tests, carried over to the module on every engine.</summary>
public sealed class DefinitionStoreTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private DefinitionStore Open(string engine, out StateDatabase database)
    {
        database = _engines.Provision(engine).Open([DefinitionsModule.Module], new StateScope(Group: "main"));
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

        var fresh = new DefinitionStore(_engines.Provision(engine).Open([DefinitionsModule.Module], new StateScope(Group: "main")));
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


/// <summary>
/// Plan 20 O03: an instance serves what its group publishes. The follower hands each subscriber what its kind serves at start, then what moved (a publish, a switch, a rollback, a retirement); a version a subscriber cannot
/// use is reported and does not stop the rest. Two followers on one state stand for two instances.
/// </summary>
public sealed class DefinitionFollowerTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    /// <summary>Serves what it is handed, keeping the last version it could use (one whose content is "bad" it cannot).</summary>
    private sealed class Served(string kind) : IDefinitionSubscriber
    {
        public Dictionary<string, (int Version, string Content)> Now { get; } = [];

        public string Kind => kind;

        public void Apply(string name, DefinitionVersion? current)
        {
            if (current is null) { Now.Remove(name); return; }
            if (current.Content == "bad") throw new InvalidDataException($"{name} v{current.Version} does not load");
            Now[name] = (current.Version, current.Content);
        }
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void What_one_instance_publishes_another_serves_and_a_version_it_cannot_use_leaves_the_one_it_served(string engine)
    {
        var state = _engines.Provision(engine).Open([DefinitionsModule.Module], new StateScope(Group: "main"));
        var publisher = new DefinitionStore(state);
        publisher.Publish("semantic-model", "sales", "v1 of sales", "build-1");
        publisher.Publish("app", "orders", "an app", "build-1");

        var problems = new List<string>();
        var models = new Served("semantic-model");
        var follower = new DefinitionFollower(new DefinitionStore(state), [models], (kind, name, version, ex) => problems.Add($"{kind}/{name} v{version}: {ex.Message}"));
        follower.Start();
        Assert.Equal(("v1 of sales"), models.Now["sales"].Content);
        Assert.Single(models.Now);   // only its kind

        // a publish, then one it cannot load: the version it served stays, and it says why
        publisher.Publish("semantic-model", "sales", "v2 of sales", "build-1");
        publisher.Publish("semantic-model", "returns.daily", "v1 of returns", "build-1");
        Assert.Equal(2, follower.Poll());
        Assert.Equal((2, "v2 of sales"), models.Now["sales"]);
        Assert.Equal(1, models.Now["returns.daily"].Version);
        publisher.Publish("semantic-model", "sales", "bad", "build-1");
        publisher.Publish("semantic-model", "returns.daily", "v2 of returns", "build-1");
        follower.Poll();
        Assert.Equal((2, "v2 of sales"), models.Now["sales"]);
        Assert.Equal(2, models.Now["returns.daily"].Version);   // the bad one stopped nothing else
        Assert.Equal("semantic-model/sales v3: sales v3 does not load", Assert.Single(problems));

        // a rollback and a retirement reach it too
        publisher.Rollback("semantic-model", "sales", "ada");
        publisher.Retire("semantic-model", "returns.daily", "ada");
        follower.Poll();
        Assert.Equal((2, "v2 of sales"), models.Now["sales"]);
        Assert.False(models.Now.ContainsKey("returns.daily"));
        Assert.Equal(0, follower.Poll());

        // an instance that starts later, with the publisher gone, serves what the group serves
        var late = new Served("semantic-model");
        new DefinitionFollower(new DefinitionStore(state), [late], (_, _, _, _) => { }).Start();
        Assert.Equal(models.Now, late.Now);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_retired_definition_keeps_its_versions_and_a_new_publish_serves_the_next_one(string engine)
    {
        var store = new DefinitionStore(_engines.Provision(engine).Open([DefinitionsModule.Module], new StateScope(Group: "main")));
        Assert.Equal(0, store.LatestEventId());
        store.Publish("semantic-model", "sales", "one", "ada");
        Assert.True(store.Retire("semantic-model", "sales", "ada"));
        Assert.False(store.Retire("semantic-model", "sales", "ada"));
        Assert.Null(store.Current("semantic-model", "sales"));
        Assert.Empty(store.CurrentOfKind("semantic-model"));
        Assert.Single(store.Versions("semantic-model", "sales"));
        Assert.Equal(["publish", "retire"], store.Changes().Select(e => e.Action));
        Assert.Equal(store.Changes()[^1].Id, store.LatestEventId());

        Assert.Equal(2, store.Publish("semantic-model", "sales", "one", "ada").Version);   // serving nothing, the same content is a new version
        Assert.Throws<DefinitionNotFoundException>(() => store.Rollback("semantic-model", "sales", "ada"));
    }
}

/// <summary>The module's own rules: conventions, two groups in one database, and the follower reading either store.</summary>
public sealed class DefinitionsModuleTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    [Fact]
    public void The_modules_follow_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(DefinitionsModule.Module));
        Assert.Empty(StateConventions.Violations(AppStorageModule.Module));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Two_groups_in_one_database_publish_and_serve_their_own_definitions(string engine)
    {
        var location = _engines.Provision(engine);
        var main = new DefinitionStore(location.Open([DefinitionsModule.Module], new StateScope(Group: "main")));
        var eu = new DefinitionStore(location.Open([DefinitionsModule.Module], new StateScope(Group: "eu")));

        main.Publish("app", "crm", "main's crm", "ada");

        Assert.Equal("main's crm", main.Current("app", "crm")!.Content);
        Assert.Null(eu.Current("app", "crm"));
        Assert.Empty(eu.Changes());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_dependency_list_round_trips_as_a_json_document(string engine)
    {
        var store = new DefinitionStore(_engines.Provision(engine).Open([DefinitionsModule.Module], new StateScope(Group: "main")));

        store.Publish("app", "crm", "x", "ada", ["model:sales@3", "model:\"odd\"@1"]);

        Assert.Equal(["model:sales@3", "model:\"odd\"@1"], store.Current("app", "crm")!.DependsOn);
    }
}
