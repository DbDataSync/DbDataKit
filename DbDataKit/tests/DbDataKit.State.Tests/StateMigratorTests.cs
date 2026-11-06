using System.Data.Common;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q02: the registry, the migrator, and what it refuses. Runs on SQLite and, when the shared engines are up, on SQL Server and PostgreSQL.</summary>
public sealed class StateMigratorTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private static StateModule Things(params string[] extra) => new(
        "things", StateScopeKind.Install,
        ["CREATE TABLE {table:thing} (id {{key}} PRIMARY KEY, label {{text}} NULL);", .. extra]);

    private static StateModule Parts(StateModule things) => new(
        "parts", StateScopeKind.Install,
        ["CREATE TABLE {table:part} (id {{key}} PRIMARY KEY, thing_id {{key}} NOT NULL REFERENCES {table:things.thing}(id));", "CREATE INDEX {index:part_by_thing} ON {table:part}(thing_id);"],
        dependsOn: [things]);

    private static long Count(StateDatabase database, string sql)
    {
        using var connection = database.OpenConnection();
        using var command = database.Command(connection, sql);
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static IReadOnlyDictionary<string, (long Version, DateTimeOffset AppliedAt)> Registry(StateDatabase database)
    {
        using var connection = database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT module, version, applied_at FROM {database.Layout!.Registry}";
        using var reader = command.ExecuteReader();
        var rows = new Dictionary<string, (long, DateTimeOffset)>();
        while (reader.Read()) rows[reader.GetString(0)] = (reader.Int64(1), reader.Time(2));
        return rows;
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_fresh_database_gets_every_module_in_dependency_order_and_a_registry_row_each(string engine)
    {
        var things = Things();
        var location = _engines.Provision(engine);

        var database = location.Open([Parts(things)]);

        var rows = Registry(database);
        Assert.Equal(1, rows["things"].Version);
        Assert.Equal(2, rows["parts"].Version);
        // the foreign key to the other module's table works, through the layout's names
        using (var connection = database.OpenConnection())
        {
            Insert(database, connection, "INSERT INTO {table:things.thing} (id, label) VALUES ($id, 'a');", "t1");
            Insert(database, connection, "INSERT INTO {table:parts.part} (id, thing_id) VALUES ($id, 't1');", "p1");
        }
        Assert.Equal(1, Count(database, "SELECT COUNT(*) FROM {table:parts.part}"));
    }

    private static void Insert(StateDatabase database, DbConnection connection, string sql, string id)
    {
        using var command = database.Command(connection, sql);
        command.Bind(database, "id", id);
        command.ExecuteNonQuery();
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Reopening_applies_nothing_and_an_appended_migration_is_applied_alone(string engine)
    {
        var location = _engines.Provision(engine);
        var clock = new Clock(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));
        var first = location.Open([Parts(Things())], time: clock);
        var before = Registry(first);

        clock.Advance(TimeSpan.FromHours(1));
        var again = location.Open([Parts(Things())], time: clock);
        Assert.Equal(before["things"].AppliedAt, Registry(again)["things"].AppliedAt);

        // the library adds a migration to its module; nothing else changes and no product edited anything
        var things2 = Things("ALTER TABLE {table:thing} {{addcolumn}} note {{text}} NULL;");
        var upgraded = location.Open([Parts(things2)], time: clock);
        var after = Registry(upgraded);
        Assert.Equal(2, after["things"].Version);
        Assert.NotEqual(before["things"].AppliedAt, after["things"].AppliedAt);
        Assert.Equal(before["parts"].AppliedAt, after["parts"].AppliedAt);
        using var connection = upgraded.OpenConnection();
        Insert(upgraded, connection, "INSERT INTO {table:things.thing} (id, note) VALUES ($id, 'n');", "t2");
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_module_newer_than_the_service_is_refused_naming_it_and_both_versions(string engine)
    {
        var location = _engines.Provision(engine);
        location.Open([Things("ALTER TABLE {table:thing} {{addcolumn}} note {{text}} NULL;")], appliedBy: "newer-service 2.0");

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([Things()]));

        Assert.Contains("'things'", ex.Message);
        Assert.Contains("version 2", ex.Message);
        Assert.Contains("version 1", ex.Message);
        Assert.Contains("newer-service 2.0", ex.Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_edited_migration_that_shipped_is_refused(string engine)
    {
        var location = _engines.Provision(engine);
        location.Open([Things()]);
        var edited = new StateModule("things", StateScopeKind.Install, ["CREATE TABLE {table:thing} (id {{key}} PRIMARY KEY, label {{text}} NOT NULL);"]);

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([edited]));

        Assert.Contains("'things'", ex.Message);
        Assert.Contains("edited", ex.Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Changed_namespace_settings_are_refused_not_applied_to_a_new_set_of_tables(string engine)
    {
        var location = _engines.Provision(engine);
        location.Open([Things()]);
        var moved = new StateNamespaceSettings { Default = new NamespaceSetting(Schema: "elsewhere", Prefix: "x_") };

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([Things()], settings: moved));

        Assert.Contains("'things'", ex.Message);
        Assert.Contains("state.namespaces.things", ex.Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_database_made_by_the_old_structure_is_refused_and_named(string engine)
    {
        var location = _engines.Provision(engine);
        location.Open(new StateSchema(["CREATE TABLE Widgets (Id {{key}} PRIMARY KEY);"]));

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([Things()]));

        Assert.Contains("one schema version", ex.Message);
        Assert.Contains(engine == StateEngineIds.Sqlite ? "state.db" : "dbdatakit_state_", ex.Message);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Two_services_opening_one_database_at_once_apply_each_migration_once(string engine)
    {
        var location = _engines.Provision(engine);
        StateModule[] Modules() => [Parts(Things("ALTER TABLE {table:thing} {{addcolumn}} note {{text}} NULL;"))];

        var opened = await Task.WhenAll(Enumerable.Range(0, 4).Select(i => Task.Run(() => location.Open(Modules(), appliedBy: $"service-{i}"))));

        var registry = Registry(opened[0]);
        Assert.Equal(2, registry["things"].Version);
        Assert.Equal(2, registry["parts"].Version);
        Assert.Equal(2, Count(opened[0], "SELECT COUNT(*) FROM " + opened[0].Layout!.Registry));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Two_scopes_of_one_module_keep_separate_tables_and_rows(string engine)
    {
        var location = _engines.Provision(engine);
        var sessions = new StateModule("sessions", StateScopeKind.Group, ["CREATE TABLE {table:session} (id {{key}} PRIMARY KEY);"]);
        var main = location.Open([sessions], new StateScope(Group: "main"));
        var eu = location.Open([sessions], new StateScope(Group: "eu"));

        using (var connection = main.OpenConnection())
            Insert(main, connection, "INSERT INTO {table:sessions.session} (id) VALUES ($id);", "s1");

        Assert.Equal(1, Count(main, "SELECT COUNT(*) FROM {table:sessions.session}"));
        Assert.Equal(0, Count(eu, "SELECT COUNT(*) FROM {table:sessions.session}"));
        Assert.Equal(2, Count(eu, "SELECT COUNT(*) FROM " + eu.Layout!.Registry));
    }

    /// <summary>A clock the test moves.</summary>
    internal sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
