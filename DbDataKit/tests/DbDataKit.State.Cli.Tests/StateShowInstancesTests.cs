using static DbDataKit.State.Cli.Tests.StateCliHarness;

namespace DbDataKit.State.Cli.Tests;

/// <summary>Plan 29b U02: <c>state show</c> lists the live instances of a group with the software and module versions their rows record.</summary>
public sealed class StateShowInstancesTests : IDisposable
{
    private readonly StateCliHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(StateCliHarness))]
    public async Task Show_lists_two_instances_at_different_versions_with_their_versions_and_leaves_out_one_whose_heartbeat_lapsed(string engine)
    {
        var db = _h.Provision(engine);
        StateOpener.RegisterProviders();
        var layout = StateLayout.Create(engine, [RunnerModule.Module], StateScope.Standalone);
        var database = engine == StateEngineIds.Sqlite ? new StateDatabase(db.Target, layout) : new StateDatabase(engine, db.Target, layout);
        var instances = new RunnerInstances(database);
        var current = instances.Register("dbdatarunner", "host-new", "2.0.0");
        using (var connection = database.OpenConnection())
        {
            Insert(database, connection, Guid.NewGuid(), "host-old", "1.9.0", "runner=1", DateTimeOffset.UtcNow);
            Insert(database, connection, Guid.NewGuid(), "host-lapsed", "1.0.0", "runner=1", DateTimeOffset.UtcNow.AddHours(-2));
        }

        var context = Context(db, [RunnerModule.Module]) with { Scope = StateScope.Standalone };
        var shown = await RunAsync(context, "show");

        Assert.Equal(0, shown.Exit);
        Assert.Contains("host-new", shown.Out);
        Assert.Contains("software 2.0.0", shown.Out);
        Assert.Contains($"runner=v{RunnerModule.Module.Version}", shown.Out);
        Assert.Contains("host-old", shown.Out);
        Assert.Contains("software 1.9.0", shown.Out);
        Assert.Contains("runner=v1", shown.Out);
        Assert.DoesNotContain("host-lapsed", shown.Out);

        var listed = shown.Data["instances"]!.AsArray();
        Assert.Equal(2, listed.Count);
        var newer = listed.Single(i => (string?)i!["key"] == current.ToString())!;
        Assert.Equal("2.0.0", (string?)newer["software_version"]);
        Assert.Equal(RunnerModule.Module.Version, (int)newer["module_versions"]!["runner"]!);
        var older = listed.Single(i => (string?)i!["host"] == "host-old")!;
        Assert.Equal("1.9.0", (string?)older["software_version"]);
        Assert.Equal(1, (int)older["module_versions"]!["runner"]!);
    }

    [Theory]
    [MemberData(nameof(Engines), MemberType = typeof(StateCliHarness))]
    public async Task Show_lists_each_instances_generation_and_marks_the_one_outside_N_minus_1_of_the_groups_newest(string engine)
    {
        var db = _h.Provision(engine);
        StateOpener.RegisterProviders();
        var layout = StateLayout.Create(engine, [RunnerModule.Module], StateScope.Standalone);
        var database = engine == StateEngineIds.Sqlite ? new StateDatabase(db.Target, layout) : new StateDatabase(engine, db.Target, layout);
        var instances = new RunnerInstances(database);
        var newest = instances.Register("dbdatarunner", "host-new", "2026.11.7.1200", generation: 3, definitionFormats: "integration=1-2");
        var behind = instances.Register("dbdatarunner", "host-behind", "2026.9.1.0900", generation: 1);
        var one = instances.Register("dbdatarunner", "host-one-back", "2026.10.1.0900", generation: 2);

        var shown = await RunAsync(Context(db, [RunnerModule.Module]) with { Scope = StateScope.Standalone }, "show");

        Assert.Equal(0, shown.Exit);
        var lines = shown.Out.Split('\n').Where(l => l.Contains("host-", StringComparison.Ordinal) && l.Contains("software", StringComparison.Ordinal)).ToList();
        Assert.Contains(lines, l => l.Contains("host-new", StringComparison.Ordinal) && l.Contains("generation 3", StringComparison.Ordinal) && !l.Contains("outside N-1", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("host-one-back", StringComparison.Ordinal) && l.Contains("generation 2", StringComparison.Ordinal) && !l.Contains("outside N-1", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("host-behind", StringComparison.Ordinal) && l.Contains("generation 1", StringComparison.Ordinal) && l.Contains("outside N-1", StringComparison.Ordinal));

        var listed = shown.Data["instances"]!.AsArray();
        Assert.Equal(3, (int)listed.Single(i => (string?)i!["key"] == newest.ToString())!["compatibility_generation"]!);
        Assert.Equal(2, (int)listed.Single(i => (string?)i!["key"] == one.ToString())!["compatibility_generation"]!);
        var marked = listed.Single(i => (string?)i!["key"] == behind.ToString())!;
        Assert.Equal(1, (int)marked["compatibility_generation"]!);
        Assert.Contains("outside N-1", (string?)marked["outside_n_minus_1"]);
        Assert.Null(listed.Single(i => (string?)i!["key"] == newest.ToString())!["outside_n_minus_1"]);
        Assert.Equal(2, (int)listed.Single(i => (string?)i!["key"] == newest.ToString())!["definition_formats"]!["integration"]!["current"]!);
    }

    private static void Insert(StateDatabase database, System.Data.Common.DbConnection connection, Guid id, string host, string software, string modules, DateTimeOffset heartbeat)
    {
        using var insert = database.Command("runner", connection, "INSERT INTO {table:instance} (id, runner, host, version, started_at, heartbeat_at, state, software_version, module_versions) VALUES ($id, 'dbdatarunner', $host, $software, $at, $at, 'Running', $software, $modules);");
        insert.Bind(database, "id", id).Bind(database, "host", host).Bind(database, "software", software).Bind(database, "at", heartbeat).Bind(database, "modules", modules);
        insert.ExecuteNonQuery();
    }
}
