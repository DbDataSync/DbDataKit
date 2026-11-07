namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 29b U02: the migration guard. A migration is refused, naming the instance, while a live instance runs a module version that cannot work with the new schema; the module's
/// <see cref="StateModule.CompatibleWithPrevious"/> says how far back an instance may be; an instance that is not live never blocks; the instance rows carry the versions (round trip).
/// </summary>
public sealed class MixedVersionGroupTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-07T10:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    private static StateModule Things(int version, int compatible = 0) => new(
        "things", StateScopeKind.Group,
        [.. new[]
        {
            "CREATE TABLE {table:thing} (id {{key}} PRIMARY KEY);",
            "ALTER TABLE {table:thing} {{addcolumn}} a {{key}} NULL;",
            "ALTER TABLE {table:thing} {{addcolumn}} b {{key}} NULL;",
        }.Take(version)],
        compatibleWithPrevious: compatible);

    private sealed class ListSource(params GroupInstance[] instances) : IGroupInstanceSource
    {
        public List<GroupInstance> Instances { get; } = [.. instances];
        public int Asked { get; private set; }

        public IReadOnlyList<GroupInstance> Live(GroupInstanceContext context)
        {
            Asked++;
            return Instances;
        }
    }

    private static GroupInstance Instance(string id, int? thingsVersion, string software = "1.0.0") =>
        new("serving", id, "host-a", 4242, Start, Start, software, thingsVersion is { } v ? new Dictionary<string, int> { ["things"] = v } : null);

    private StateDatabase Open(StateEngines.Location location, int version, int compatible, IGroupInstanceSource? source) =>
        location.Open([Things(version, compatible)], StateScope.Standalone, time: _clock, groupInstances: source);

    private static long Version(StateEngines.Location location) =>
        Convert.ToInt64(location.Scalar(location.Engine switch
        {
            StateEngineIds.Sqlite => "SELECT version FROM dbdatakit_modules WHERE module = 'things'",
            _ => "SELECT version FROM dbdatakit.modules WHERE module = 'things'",
        }));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_migration_is_refused_naming_the_instance_while_a_live_incompatible_one_is_registered(string engine)
    {
        var location = _engines.Provision(engine);
        Open(location, 1, 0, null);
        var source = new ListSource(Instance("inst-old", 1, "1.4.2"));

        var ex = Assert.Throws<MigrationRefusedException>(() => Open(location, 2, 0, source));

        Assert.Contains("inst-old", ex.Message);
        Assert.Contains("host-a", ex.Message);
        Assert.Contains("1.4.2", ex.Message);
        Assert.Contains("things version 1", ex.Message);
        Assert.Contains("Stop that instance, or finish the rolling upgrade in the order the plan gives", ex.Message);
        Assert.Equal("inst-old", ex.Instance.Id);
        Assert.Equal(1, Version(location));

        // the old instance stops (its row is gone): the migration applies
        source.Instances.Clear();
        Open(location, 2, 0, source);
        Assert.Equal(2, Version(location));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_module_that_declares_the_previous_version_compatible_applies_beside_it_and_refuses_an_older_one(string engine)
    {
        var location = _engines.Provision(engine);
        Open(location, 1, 1, null);

        // N-1 is live: the migration to 2 applies
        Open(location, 2, 1, new ListSource(Instance("inst-n1", 1)));
        Assert.Equal(2, Version(location));

        // N-2 is live: the migration to 3 is refused for it; an N-1 instance (version 2) beside it would not be
        var ex = Assert.Throws<MigrationRefusedException>(() => Open(location, 3, 1, new ListSource(Instance("inst-n2", 1), Instance("inst-n1", 2))));
        Assert.Equal("inst-n2", ex.Instance.Id);
        Assert.Equal(2, Version(location));

        Open(location, 3, 1, new ListSource(Instance("inst-n1", 2)));
        Assert.Equal(3, Version(location));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_row_that_records_no_versions_is_taken_to_run_the_version_the_database_is_at(string engine)
    {
        var location = _engines.Provision(engine);
        Open(location, 1, 0, null);
        Assert.Throws<MigrationRefusedException>(() => Open(location, 2, 0, new ListSource(Instance("inst-unknown", null))));
        Open(location, 2, 1, new ListSource(Instance("inst-unknown", null)));
        Assert.Equal(2, Version(location));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_instance_that_does_not_use_the_module_and_a_database_that_needs_no_migration_ask_nobody(string engine)
    {
        var location = _engines.Provision(engine);
        Open(location, 1, 0, null);
        var other = new GroupInstance("serving", "inst-x", "h", 1, Start, Start, "1", new Dictionary<string, int> { ["unrelated"] = 1 });
        Open(location, 2, 0, new ListSource(other));
        var source = new ListSource(Instance("inst-current", 2));
        Open(location, 2, 0, source);
        Assert.Equal(0, source.Asked);
    }

    [Fact]
    public void A_module_version_text_round_trips_and_a_bad_part_is_skipped()
    {
        Assert.Equal("things=2", ModuleVersionText.Format([Things(2)]));
        Assert.Equal(2, ModuleVersionText.Parse("a=1;things=2;;junk;x=y")!["things"]);
        Assert.Equal(2, ModuleVersionText.Parse("a=1;things=2;;junk;x=y")!.Count);
        Assert.Null(ModuleVersionText.Parse(""));
        Assert.Equal(0, Things(1).CompatibleWithPrevious);
        Assert.Throws<ArgumentOutOfRangeException>(() => Things(1, -1));
    }

    // ---- the real instance tables ----

    /// <summary>The runner module cut to its first migration, the table as an older binary made it (no version columns).</summary>
    private static StateModule RunnerV1(int compatible) => new("runner", StateScopeKind.Group, [RunnerModule.Module.Migrations[0]], compatibleWithPrevious: compatible);

    private static StateModule RunnerCurrent(int compatible) => new("runner", StateScopeKind.Group, RunnerModule.Module.Migrations, compatibleWithPrevious: compatible, retention: RunnerModule.Module.Retention);

    private void InsertOldRunner(StateDatabase old)
    {
        using var connection = old.OpenConnection();
        using var insert = old.Command("runner", connection, "INSERT INTO {table:instance} (id, runner, host, version, started_at, heartbeat_at, state) VALUES ($id, 'dbdatarunner', 'host-old', '1.0.0', $at, $at, 'Running');");
        insert.Bind(old, "id", Guid.NewGuid()).Bind(old, "at", _clock.GetUtcNow());
        insert.ExecuteNonQuery();
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_runner_instance_row_carries_the_versions_and_a_lapsed_heartbeat_never_blocks(string engine)
    {
        var location = _engines.Provision(engine);
        var database = location.Open([RunnerModule.Module], StateScope.Standalone, time: _clock);
        var instances = new RunnerInstances(database);
        var id = instances.Register("dbdatarunner", "host-r", "2.0.0");

        var stored = instances.Get(id)!;
        Assert.Equal("2.0.0", stored.SoftwareVersion);
        Assert.Equal($"runner={RunnerModule.Module.Version}", stored.ModuleVersions);

        var source = new StandardGroupInstanceSource(TimeSpan.FromSeconds(90));
        IReadOnlyList<GroupInstance> Live()
        {
            using var connection = database.OpenConnection();
            return source.Live(new GroupInstanceContext(connection, null, database.Layout!, _clock.GetUtcNow(), _ => RunnerModule.Module.Version));
        }

        var live = Assert.Single(Live());
        Assert.Equal(id.ToString(), live.Id);
        Assert.Equal("2.0.0", live.SoftwareVersion);
        Assert.Equal(RunnerModule.Module.Version, live.ModuleVersions!["runner"]);

        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Empty(Live());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_real_source_guards_the_runner_migration_that_adds_the_version_columns(string engine)
    {
        var location = _engines.Provision(engine);
        InsertOldRunner(location.Open([RunnerV1(0)], StateScope.Standalone, time: _clock));
        var source = new StandardGroupInstanceSource();

        // the new code declares nothing about the old one: refused, with the old instance named
        var ex = Assert.Throws<MigrationRefusedException>(() => location.Open([RunnerCurrent(0)], StateScope.Standalone, time: _clock, groupInstances: source));
        Assert.Contains("host-old", ex.Message);
        Assert.Contains("1.0.0", ex.Message);

        // its heartbeat lapses: it never blocks
        _clock.Advance(TimeSpan.FromMinutes(10));
        var lapsed = location.Open([RunnerCurrent(0)], StateScope.Standalone, time: _clock, groupInstances: source);
        Assert.Equal(2, lapsed.RegisteredModules().Single(m => m.Module == "runner").Version);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_real_source_lets_the_migration_through_when_the_module_declares_the_previous_version_compatible(string engine)
    {
        var location = _engines.Provision(engine);
        InsertOldRunner(location.Open([RunnerV1(1)], StateScope.Standalone, time: _clock));

        var current = location.Open([RunnerCurrent(1)], StateScope.Standalone, time: _clock, groupInstances: new StandardGroupInstanceSource());

        Assert.Equal(2, current.RegisteredModules().Single(m => m.Module == "runner").Version);
        // the old instance's row is still there and is read without versions now that the columns exist
        using var read = current.OpenConnection();
        var live = Assert.Single(new StandardGroupInstanceSource().Live(new GroupInstanceContext(read, null, current.Layout!, _clock.GetUtcNow(), _ => 2)));
        Assert.Equal("host-old", live.Host);
        Assert.Equal("1.0.0", live.SoftwareVersion);
        Assert.Null(live.ModuleVersions);
    }
}
