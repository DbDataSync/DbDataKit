namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 20 O03: an instance serves what its group publishes. The follower hands each subscriber what its kind serves at start, then what moved (a publish, a switch, a rollback, a retirement); a version a subscriber cannot
/// use is reported and does not stop the rest. Two followers on one state stand for two instances.
/// </summary>
public sealed class DefinitionFollowerTests : IDisposable
{
    private static readonly StateSchema Schema = new([DefinitionSchema.Tables]);
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
        var state = _engines.Open(engine, Schema);
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
        var store = new DefinitionStore(_engines.Open(engine, Schema));
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
