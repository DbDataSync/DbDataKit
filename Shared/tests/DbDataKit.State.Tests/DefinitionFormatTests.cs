namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 24a UC01: a format on every published definition. The column is additive and every version stored before it is format 1; a kind reads its current format and the one before it; a publish
/// in a format the kind does not read is refused, and so is one a live instance of the group cannot read, naming the instance; a follower and the runner refuse a version newer than they read with
/// a message naming kind, name and both numbers; the instance rows carry the formats.
/// </summary>
public sealed class DefinitionFormatTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-07T12:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    private static DefinitionKinds Kinds() => new DefinitionKinds()
        .Register(new DefinitionKind("semantic-model", ["serving"], PublishMode.Serve) { Formats = DefinitionFormats.Of(2) })
        .Register(new DefinitionKind("app", ["serving"], PublishMode.Serve))
        .Register(new DefinitionKind("integration", ["runner"], PublishMode.Stage) { FormatOf = m => m.StartsWith("package/", StringComparison.Ordinal) ? int.Parse(m[8..9], System.Globalization.CultureInfo.InvariantCulture) : null, Formats = DefinitionFormats.Of(2) });

    private sealed class ListSource : IGroupInstanceSource
    {
        public List<GroupInstance> Instances { get; } = [];

        public IReadOnlyList<GroupInstance> Live(GroupInstanceContext context) => Instances;
    }

    private static GroupInstance Instance(string id, string? formats, string software = "2026.11.1.0900") =>
        new("serving", id, "host-" + id, 100, Start, Start, software, null, DefinitionFormatText.Parse(formats));

    private (DefinitionPublisher Publisher, StateDatabase Database, ListSource Instances) Open(string engine, string? target = "serving")
    {
        var source = new ListSource();
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module], new StateScope(Group: "main"), time: _clock, groupInstances: source);
        return (new DefinitionPublisher(database, Kinds(), new PayloadStore(database), target), database, source);
    }

    private static PublishRequest Serve(string kind, string name, string manifest, int? format = null) => new(kind, name, manifest, "ada", "prod", PublishMode.Serve, Format: format);

    [Fact]
    public void The_text_of_formats_round_trips_and_a_bad_part_is_skipped()
    {
        var text = DefinitionFormatText.Format([("semantic-model", DefinitionFormats.Of(2)), ("app", DefinitionFormats.One)]);
        Assert.Equal("app=1-1;semantic-model=1-2", text);
        var read = DefinitionFormatText.Parse(text)!;
        Assert.Equal(new DefinitionFormats(1, 2), read["semantic-model"]);
        Assert.Equal(DefinitionFormats.One, read["app"]);
        Assert.Equal(3, DefinitionFormatText.Parse("x=3;junk;y=a-b;z=3-2;w=0")!["x"].Current);
        Assert.Single(DefinitionFormatText.Parse("x=3;junk;y=a-b;z=3-2;w=0")!);
        Assert.Null(DefinitionFormatText.Parse(""));
        Assert.Equal("1 to 2", DefinitionFormats.Of(2).ToString());
        Assert.Equal("1", DefinitionFormats.Of(1).ToString());
        Assert.True(DefinitionFormats.Of(3).Reads(2) && !DefinitionFormats.Of(3).Reads(1) && !DefinitionFormats.Of(3).Reads(4));
    }

    [Fact]
    public void The_registry_says_what_it_reads_as_an_instance_row_records_it() =>
        Assert.Equal("app=1-1;integration=1-2;semantic-model=1-2", Kinds().FormatText);

    [Fact]
    public void The_definitions_module_still_follows_the_conventions() => Assert.Empty(StateConventions.Violations(DefinitionsModule.Module));

    // ---- the column

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_database_migrated_from_before_the_column_keeps_its_versions_as_format_1(string engine)
    {
        var location = _engines.Provision(engine);
        var before = new StateModule("definitions", StateScopeKind.Group, DefinitionsModule.Module.Migrations.Take(3).ToList(), retention: DefinitionsModule.Module.Retention);
        var old = location.Open([before], new StateScope(Group: "main"), time: _clock);
        using (var connection = old.OpenConnection())
        {
            foreach (var version in new[] { 1, 2 })
            {
                using var insert = old.Command("definitions", connection,
                    "INSERT INTO {table:definition_version} (kind, name, version, hash, content, depends_on, published_by, published_at) VALUES ('semantic-model', 'sales', $v, $h, $c, CAST('[]' AS {{json}}), 'ada', $at);");
                insert.Bind(old, "v", (long)version).Bind(old, "h", $"hash{version}").Bind(old, "c", $"content {version}").Bind(old, "at", _clock.GetUtcNow()).ExecuteNonQuery();
            }
        }

        var current = location.Open([DefinitionsModule.Module], new StateScope(Group: "main"), time: _clock);

        Assert.Equal(DefinitionsModule.Module.Version, current.RegisteredModules().Single(m => m.Module == "definitions").Version);
        var store = new DefinitionStore(current);
        Assert.All(store.Versions("semantic-model", "sales"), v => Assert.Equal(1, v.Format));
        Assert.Equal(2, store.Versions("semantic-model", "sales").Count);
    }

    [Fact]
    public void The_migration_is_appended_and_an_instance_of_the_version_before_still_works_with_it()
    {
        Assert.Equal(5, DefinitionsModule.Module.Version);   // the fifth migration is plan 30 PB03's signature columns and trust table: nullable, so version 4 still works with it
        Assert.Equal(1, DefinitionsModule.Module.CompatibleWithPrevious);
        Assert.Contains("DEFAULT 1", DefinitionsModule.Module.Migrations[3], StringComparison.Ordinal);
        Assert.DoesNotContain("NOT NULL DEFAULT", DefinitionsModule.Module.Migrations[4].Replace("PRIMARY KEY", ""), StringComparison.Ordinal);
    }

    // ---- publish

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_publish_records_the_format_and_a_default_is_format_1(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var store = new DefinitionStore(database);

        var plain = publisher.Publish(Serve("semantic-model", "sales", "model one"));
        var two = publisher.Publish(Serve("semantic-model", "sales", "model two", format: 2));

        Assert.Equal(1, plain.Version.Format);
        Assert.Equal(2, two.Version.Format);
        Assert.Equal(2, store.Current("semantic-model", "sales")!.Format);
        Assert.Equal([1, 2], store.Versions("semantic-model", "sales").Select(v => v.Format));
        Assert.Equal(2, store.CurrentOfKind("semantic-model").Single().Format);
        // the same text in another format is another version: the hash covers the format
        Assert.NotEqual(plain.Version.Hash, publisher.Publish(Serve("semantic-model", "other", "model one", format: 2)).Version.Hash);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_manifest_says_its_format_when_the_kind_can_read_it_and_a_format_the_kind_does_not_read_is_refused(string engine)
    {
        var (publisher, _, _) = Open(engine, target: null);
        var staged = publisher.Publish(new PublishRequest("integration", "etl", "package/2 manifest", "ada", Mode: PublishMode.Stage));
        Assert.Equal(2, staged.Version.Format);

        var tooNew = Assert.Throws<PublishRefusedException>(() => publisher.Publish(Serve("semantic-model", "sales", "model", format: 3)));
        Assert.Equal(PublishRefusal.Format, tooNew.Reason);
        Assert.Contains("semantic-model", tooNew.Message);
        Assert.Contains("'sales'", tooNew.Message);
        Assert.Contains("format 3", tooNew.Message);
        Assert.Contains("1 to 2", tooNew.Message);

        var viaManifest = Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("integration", "etl2", "package/3 manifest", "ada", Mode: PublishMode.Stage)));
        Assert.Contains("format 3", viaManifest.Message);
        var unreadAnyMore = Assert.Throws<PublishRefusedException>(() => publisher.Publish(Serve("app", "portal", "app", format: 0)));
        Assert.Equal(PublishRefusal.Format, unreadAnyMore.Reason);
        Assert.Null(publisher.Head("semantic-model", "sales"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_format_2_publish_to_a_group_with_an_instance_that_reads_format_1_is_refused_naming_the_instance(string engine)
    {
        var (publisher, database, instances) = Open(engine);
        var store = new DefinitionStore(database);
        publisher.Publish(Serve("semantic-model", "sales", "one"));

        // two instances at different versions: the new one reads 1 to 2, the old one only 1
        instances.Instances.Add(Instance("new", "semantic-model=1-2"));
        instances.Instances.Add(Instance("old", "semantic-model=1-1", "2026.10.1.0900"));

        var refused = Assert.Throws<PublishRefusedException>(() => publisher.Publish(Serve("semantic-model", "sales", "two", format: 2)));

        Assert.Equal(PublishRefusal.InstanceFormat, refused.Reason);
        Assert.Contains("instance old", refused.Message);
        Assert.Contains("host-old", refused.Message);
        Assert.Contains("2026.10.1.0900", refused.Message);
        Assert.Contains("format 2", refused.Message);
        Assert.Contains("format 1", refused.Message);
        Assert.DoesNotContain("instance new", refused.Message);
        Assert.Equal(1, store.Current("semantic-model", "sales")!.Version);

        // a format-1 publish is read by every instance; a format-2 one goes once the old instance is gone
        Assert.Equal(2, publisher.Publish(Serve("semantic-model", "sales", "one again", format: 1)).Version.Version);
        instances.Instances.RemoveAll(i => i.Id == "old");
        var two = publisher.Publish(Serve("semantic-model", "sales", "two", format: 2));
        Assert.Equal((3, 2), (two.Version.Version, two.Version.Format));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_instance_whose_row_records_no_formats_reads_format_1_and_one_that_does_not_serve_the_kind_is_not_asked(string engine)
    {
        var (publisher, _, instances) = Open(engine);
        instances.Instances.Add(Instance("unknown", null));
        var refused = Assert.Throws<PublishRefusedException>(() => publisher.Publish(Serve("semantic-model", "sales", "two", format: 2)));
        Assert.Contains("its row records no formats", refused.Message);
        Assert.Contains("instance unknown", refused.Message);

        instances.Instances.Clear();
        instances.Instances.Add(Instance("apps-only", "app=1-1"));
        Assert.Equal(2, publisher.Publish(Serve("semantic-model", "sales", "two", format: 2)).Version.Format);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_switch_a_rollback_and_an_activation_refuse_a_version_a_live_instance_cannot_read(string engine)
    {
        var (publisher, _, instances) = Open(engine, target: null);
        publisher.Publish(Serve("semantic-model", "sales", "one"));
        publisher.Publish(Serve("semantic-model", "sales", "two", format: 2));
        publisher.Publish(Serve("semantic-model", "sales", "three"));
        publisher.Publish(new PublishRequest("integration", "etl", "package/2 one", "ada", Mode: PublishMode.Stage));

        instances.Instances.Add(Instance("old", "semantic-model=1-1;integration=1-1"));

        Assert.Equal(PublishRefusal.InstanceFormat, Assert.Throws<PublishRefusedException>(() => publisher.Switch("semantic-model", "sales", 2, "ada")).Reason);
        Assert.Equal(1, publisher.Switch("semantic-model", "sales", 1, "ada").Version);
        Assert.Equal(PublishRefusal.InstanceFormat, Assert.Throws<PublishRefusedException>(() => publisher.Activate("integration", "etl", "ada")).Reason);

        instances.Instances.Clear();
        Assert.Equal(2, publisher.Switch("semantic-model", "sales", 2, "ada").Version);
        Assert.Equal(2, publisher.Activate("integration", "etl", "ada").Format);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_store_the_serving_layer_publishes_through_takes_a_format_and_is_refused_by_the_same_rule(string engine)
    {
        var (_, database, instances) = Open(engine);
        var store = new DefinitionStore(database);
        instances.Instances.Add(Instance("old", "data-source=1-1"));

        var refused = Assert.Throws<PublishRefusedException>(() => store.Publish("data-source", "warehouse", "schema: connection/2", "ada", format: 2));
        Assert.Equal(PublishRefusal.InstanceFormat, refused.Reason);
        Assert.Contains("instance old", refused.Message);
        Assert.Null(store.Current("data-source", "warehouse"));

        instances.Instances.Clear();
        instances.Instances.Add(Instance("new", "data-source=1-2"));
        Assert.Equal(2, store.Publish("data-source", "warehouse", "schema: connection/2", "ada", format: 2).Format);
        Assert.Equal(1, store.Publish("data-source", "other", "schema: connection/1", "ada").Format);
    }

    // ---- reads

    private sealed class Subscriber(string kind, DefinitionFormats reads) : IDefinitionSubscriber
    {
        public string Kind => kind;
        public DefinitionFormats Formats => reads;
        public List<(string Name, int? Version)> Applied { get; } = [];
        public void Apply(string name, DefinitionVersion? current) => Applied.Add((name, current?.Version));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_follower_refuses_a_version_newer_than_it_reads_naming_kind_name_and_both_formats_and_the_old_one_keeps_serving(string engine)
    {
        var (publisher, database, _) = Open(engine);
        var store = new DefinitionStore(database);
        var subscriber = new Subscriber("semantic-model", DefinitionFormats.One);
        var problems = new List<(string Kind, string Name, int? Version, Exception Error)>();
        var follower = new DefinitionFollower(store, [subscriber], (k, n, v, e) => problems.Add((k, n, v, e)));
        publisher.Publish(Serve("semantic-model", "sales", "one"));
        follower.Start();
        Assert.Equal([("sales", (int?)1)], subscriber.Applied);

        publisher.Publish(Serve("semantic-model", "sales", "two", format: 2));
        Assert.Equal(1, follower.Poll());

        // never handed to the subscriber; reported once with the words of the rule
        Assert.Equal([("sales", (int?)1)], subscriber.Applied);
        var (kind, name, version, error) = Assert.Single(problems);
        Assert.Equal(("semantic-model", "sales", (int?)2), (kind, name, version));
        var refusal = Assert.IsType<DefinitionFormatException>(error);
        Assert.Equal(2, refusal.Format);
        Assert.Equal(DefinitionFormats.One, refusal.Reads);
        Assert.Contains("version 2 of the semantic-model 'sales' is in format 2", refusal.Message);
        Assert.Contains("this instance reads format 1 of semantic-model", refusal.Message);
        Assert.Contains("Upgrade this instance", refusal.Message);

        // an instance that reads format 2 serves it, and a rollback to format 1 reaches the first
        var newer = new Subscriber("semantic-model", DefinitionFormats.Of(2));
        new DefinitionFollower(store, [newer], (_, _, _, _) => { }).Start();
        Assert.Equal([("sales", (int?)2)], newer.Applied);
        Assert.Equal("semantic-model=1-1", follower.FormatText);
    }

    // ---- the instance rows

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_runner_instance_row_carries_the_formats_and_the_source_reads_them_back(string engine)
    {
        var location = _engines.Provision(engine);
        var database = location.Open([RunnerModule.Module], StateScope.Standalone, time: _clock);
        var instances = new RunnerInstances(database);
        var id = instances.Register("dbdatarunner", "host-r", "2.0.0", definitionFormats: "integration=1-2");
        var without = instances.Register("dbdatarunner", "host-s", "1.0.0");

        Assert.Equal("integration=1-2", instances.Get(id)!.DefinitionFormats);
        Assert.Null(instances.Get(without)!.DefinitionFormats);

        using var connection = database.OpenConnection();
        var live = new StandardGroupInstanceSource().Live(new GroupInstanceContext(connection, null, database.Layout!, _clock.GetUtcNow(), _ => RunnerModule.Module.Version));
        Assert.Equal(new DefinitionFormats(1, 2), live.Single(i => i.Id == id.ToString()).DefinitionFormats!["integration"]);
        Assert.Null(live.Single(i => i.Id == without.ToString()).DefinitionFormats);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_real_runner_source_makes_a_publish_refuse_the_instance_that_reads_only_format_1(string engine)
    {
        var location = _engines.Provision(engine);
        var database = location.Open([DefinitionsModule.Module, PayloadStore.Module, RunnerModule.Module], new StateScope(Group: "main"), time: _clock, groupInstances: new StandardGroupInstanceSource());
        var instances = new RunnerInstances(database);
        instances.Register("dbdatarunner", "host-old", "2026.10.1.0900", definitionFormats: "integration=1-1");
        instances.Register("dbdatarunner", "host-new", "2026.11.7.1200", definitionFormats: "integration=1-2");
        var publisher = new DefinitionPublisher(database, Kinds(), new PayloadStore(database), "runner");

        var refused = Assert.Throws<PublishRefusedException>(() => publisher.Publish(new PublishRequest("integration", "etl", "package/2 one", "ada", Mode: PublishMode.Stage)));
        Assert.Contains("host-old", refused.Message);
        Assert.Contains("2026.10.1.0900", refused.Message);
        Assert.DoesNotContain("host-new", refused.Message);
        Assert.Null(publisher.Head("integration", "etl"));
    }
}
