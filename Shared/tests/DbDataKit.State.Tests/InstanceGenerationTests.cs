namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 24a UC05: each serving and runner instance writes the compatibility generation of its binary into its row (the program version was written by 29b U02); two instances of one group at different
/// generations are both listed with their numbers, and the one more than one behind the group's newest is marked; the migration guard of 29b U02 still names a blocking instance.
/// </summary>
public sealed class InstanceGenerationTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-07T12:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    [Fact]
    public void The_mark_is_for_an_instance_more_than_one_generation_behind_the_groups_newest_and_not_for_one_that_records_none()
    {
        Assert.Null(GroupGenerations.Outside((int?)3, 3));
        Assert.Null(GroupGenerations.Outside((int?)2, 3));
        Assert.Null(GroupGenerations.Outside((int?)null, 3));
        Assert.Null(GroupGenerations.Outside((int?)1, null));
        var behind = GroupGenerations.Outside((int?)1, 3)!;
        Assert.Contains("generation 1", behind);
        Assert.Contains("newest is 3", behind);
        Assert.StartsWith("outside N-1", behind);
        Assert.Equal(3, GroupGenerations.Newest(new int?[] { 1, null, 3, 2 }));
        Assert.Null(GroupGenerations.Newest(new int?[] { null, null }));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Two_runner_instances_at_different_generations_are_both_listed_with_their_numbers_and_the_older_one_is_marked(string engine)
    {
        var location = _engines.Provision(engine);
        var database = location.Open([RunnerModule.Module], StateScope.Standalone, time: _clock);
        var instances = new RunnerInstances(database);
        var current = instances.Register("dbdatarunner", "host-new", "2026.11.7.1200", generation: 3);
        var behind = instances.Register("dbdatarunner", "host-old", "2026.9.1.0900", generation: 1);
        var unknown = instances.Register("dbdatarunner", "host-unknown", "2026.1.1.0900");

        Assert.Equal(3, instances.Get(current)!.Generation);
        Assert.Equal(1, instances.Get(behind)!.Generation);
        Assert.Null(instances.Get(unknown)!.Generation);

        using var connection = database.OpenConnection();
        var live = new StandardGroupInstanceSource().Live(new GroupInstanceContext(connection, null, database.Layout!, _clock.GetUtcNow(), _ => RunnerModule.Module.Version));
        Assert.Equal(3, live.Count);
        Assert.Equal(3, live.Single(i => i.Id == current.ToString()).Generation);
        Assert.Equal("2026.9.1.0900", live.Single(i => i.Id == behind.ToString()).SoftwareVersion);
        var newest = GroupGenerations.Newest(live);
        Assert.Equal(3, newest);
        Assert.Equal([behind.ToString()], live.Where(i => GroupGenerations.Outside(i, newest) is not null).Select(i => i.Id));
    }

    [Fact]
    public void The_migrations_are_appended_and_a_binary_of_the_version_before_still_works_with_them()
    {
        Assert.Equal(6, RunnerModule.Module.Version);
        Assert.Equal(1, RunnerModule.Module.CompatibleWithPrevious);
        Assert.Contains("generation", RunnerModule.Module.Migrations[3], StringComparison.Ordinal);
        Assert.Contains("task_owner", RunnerModule.Module.Migrations[3], StringComparison.Ordinal);   // plan 28c E09: part of the newest migration, no release holds the module yet
        Assert.Contains("connection_copy", RunnerModule.Module.Migrations[4], StringComparison.Ordinal);   // plan 27a IX23: the connections the console hands the runner, an additive table
        Assert.Contains("task_lineage", RunnerModule.Module.Migrations[5], StringComparison.Ordinal);   // plan 27a IX12: what a task reported it read and wrote, an additive table
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_runner_row_from_before_the_columns_is_read_without_a_generation_and_the_guard_still_names_a_blocking_instance(string engine)
    {
        var location = _engines.Provision(engine);
        // the module as the release before this one made it (three migrations, the guard's N-1 allows the fourth beside it) and as the one two releases back made it (two migrations)
        StateModule Cut(int migrations, int compatible) => new("runner", StateScopeKind.Group, RunnerModule.Module.Migrations.Take(migrations).ToList(), compatibleWithPrevious: compatible, retention: RunnerModule.Module.Retention);
        var old = location.Open([Cut(2, 1)], StateScope.Standalone, time: _clock);
        using (var connection = old.OpenConnection())
        {
            using var insert = old.Command("runner", connection, "INSERT INTO {table:instance} (id, runner, host, version, started_at, heartbeat_at, state, software_version, module_versions) VALUES ($id, 'dbdatarunner', 'host-old', '1.0.0', $at, $at, 'Running', '1.0.0', 'runner=2');");
            insert.Bind(old, "id", Guid.NewGuid()).Bind(old, "at", _clock.GetUtcNow()).ExecuteNonQuery();
        }

        // the instance runs runner version 2; the migration to 4 declares only 3 compatible, so it is refused, naming that instance
        var refused = Assert.Throws<MigrationRefusedException>(() => location.Open([Cut(4, 1)], StateScope.Standalone, time: _clock, groupInstances: new StandardGroupInstanceSource()));
        Assert.Contains("host-old", refused.Message);
        Assert.Contains("runner version 2", refused.Message);

        // one release at a time: version 3 is applied beside it, and the row is read without the newer columns
        var three = location.Open([Cut(3, 1)], StateScope.Standalone, time: _clock, groupInstances: new StandardGroupInstanceSource());
        Assert.Equal(3, three.RegisteredModules().Single(m => m.Module == "runner").Version);
        var formats = Guid.NewGuid();
        using (var connection = three.OpenConnection())
        {
            using var insert = three.Command("runner", connection, "INSERT INTO {table:instance} (id, runner, host, version, started_at, heartbeat_at, state, software_version, module_versions, definition_formats) VALUES ($id, 'dbdatarunner', 'host-three', '3.0.0', $at, $at, 'Running', '3.0.0', 'runner=3', 'integration=1-1');");
            insert.Bind(three, "id", formats).Bind(three, "at", _clock.GetUtcNow()).ExecuteNonQuery();
        }

        var current = location.Open([RunnerModule.Module], StateScope.Standalone, time: _clock);
        using var read = current.OpenConnection();
        var live = new StandardGroupInstanceSource().Live(new GroupInstanceContext(read, null, current.Layout!, _clock.GetUtcNow(), _ => RunnerModule.Module.Version));
        Assert.All(live, i => Assert.Null(i.Generation));
        Assert.Equal(2, live.Count);
        Assert.Contains(live, i => i.Id == formats.ToString() && i.DefinitionFormats!["integration"].Current == 1);
    }
}
