namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q01: where modules' tables are, worked out and checked before any database is touched.</summary>
public sealed class StateLayoutTests
{
    private static StateModule Module(string name, StateScopeKind scope = StateScopeKind.Install, IEnumerable<StateModule>? dependsOn = null, params string[] migrations) =>
        new(name, scope, migrations.Length == 0 ? [$"CREATE TABLE {{table:{name}}} (id {{{{key}}}} PRIMARY KEY);"] : migrations, dependsOn);

    [Fact]
    public void Dependencies_come_first_and_each_module_once()
    {
        var identity = Module("identity");
        var trust = Module("trust", dependsOn: [identity]);
        var sessions = Module("sessions", dependsOn: [identity, trust]);

        var layout = StateLayout.Create(StateEngineIds.Sqlite, [sessions, trust, identity]);

        Assert.Equal(["identity", "trust", "sessions"], layout.Namespaces.Select(n => n.Module.Name));
    }

    [Fact]
    public void A_service_that_names_only_what_it_needs_gets_its_dependencies_too()
    {
        var identity = Module("identity");
        var trust = Module("trust", dependsOn: [identity]);

        var layout = StateLayout.Create(StateEngineIds.Postgres, [trust]);

        Assert.True(layout.Has("identity"));
    }

    [Fact]
    public void Server_engines_default_to_a_schema_per_module_and_SQLite_to_a_prefix()
    {
        var modules = new[] { Module("identity") };

        var postgres = StateLayout.Create(StateEngineIds.Postgres, modules);
        var sqlite = StateLayout.Create(StateEngineIds.Sqlite, modules);
        var mssql = StateLayout.Create(StateEngineIds.MsSql, modules);

        Assert.Equal("dbdatakit_identity.identity", postgres.Table("identity", "identity"));
        Assert.Equal("dbdatakit_identity.identity", mssql.Table("identity", "identity"));
        Assert.Equal("dbdatakit_identity_identity", sqlite.Table("identity", "identity"));
        Assert.Equal("dbdatakit.modules", postgres.Registry);
        Assert.Equal("dbdatakit_modules", sqlite.Registry);
        Assert.Equal(["dbdatakit", "dbdatakit_identity"], postgres.Schemas);
        Assert.Empty(sqlite.Schemas);
    }

    [Fact]
    public void A_group_module_adds_the_group_to_the_default_name_and_two_groups_do_not_clash()
    {
        var sessions = Module("sessions", StateScopeKind.Group);

        var main = StateLayout.Create(StateEngineIds.Postgres, [sessions], new StateScope(Group: "main"));
        var eu = StateLayout.Create(StateEngineIds.Postgres, [sessions], new StateScope(Group: "EU-west"));
        var sqlite = StateLayout.Create(StateEngineIds.Sqlite, [sessions], new StateScope(Group: "main"));

        Assert.Equal("dbdatakit_sessions_main.sessions", main.Table("sessions", "sessions"));
        Assert.Equal("dbdatakit_sessions_eu_west.sessions", eu.Table("sessions", "sessions"));
        Assert.Equal("dbdatakit_sessions_main_sessions", sqlite.Table("sessions", "sessions"));
    }

    [Fact]
    public void A_group_module_without_a_group_name_says_so()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StateLayout.Create(StateEngineIds.Postgres, [Module("sessions", StateScopeKind.Group)]));
        Assert.Contains("sessions", ex.Message);
        Assert.Contains("group", ex.Message);
    }

    [Fact]
    public void An_administrator_can_put_everything_in_one_schema_with_a_prefix_per_module()
    {
        var settings = new StateNamespaceSettings { Default = new NamespaceSetting(Schema: "dbdatakit", Prefix: "{module}_") };

        var layout = StateLayout.Create(StateEngineIds.MsSql, [Module("identity"), Module("runs")], settings: settings);

        Assert.Equal("dbdatakit.identity_identity", layout.Table("identity", "identity"));
        Assert.Equal("dbdatakit.runs_runs", layout.Table("runs", "runs"));
    }

    [Fact]
    public void A_module_setting_wins_over_the_default_field_by_field()
    {
        var settings = new StateNamespaceSettings
        {
            Default = new NamespaceSetting(Schema: "shared"),
            Modules = new Dictionary<string, NamespaceSetting> { ["runs"] = new(Prefix: "r_") },
        };

        var layout = StateLayout.Create(StateEngineIds.Postgres, [Module("identity"), Module("runs")], settings: settings);

        Assert.Equal("shared.identity", layout.Table("identity", "identity"));
        Assert.Equal("shared.r_runs", layout.Table("runs", "runs"));
    }

    [Fact]
    public void An_empty_schema_means_the_engines_own_default_schema()
    {
        var settings = new StateNamespaceSettings { Default = new NamespaceSetting(Schema: "", Prefix: "dbdatakit_{module}_") };

        var layout = StateLayout.Create(StateEngineIds.Postgres, [Module("identity")], settings: settings);

        Assert.Equal("dbdatakit_identity_identity", layout.Table("identity", "identity"));
    }

    [Fact]
    public void The_registry_place_is_configurable()
    {
        var settings = new StateNamespaceSettings { Registry = new NamespaceSetting(Schema: "ops") };

        Assert.Equal("ops.modules", StateLayout.Create(StateEngineIds.Postgres, [Module("identity")], settings: settings).Registry);
    }

    [Fact]
    public void Render_replaces_table_and_index_tokens_and_refuses_ones_it_cannot_place()
    {
        var identity = Module("identity", migrations: ["CREATE TABLE {table:users} (id {{key}} PRIMARY KEY);", "CREATE INDEX {index:users_by_id} ON {table:users}(id);"]);
        var trust = Module("trust", dependsOn: [identity], migrations: ["CREATE TABLE {table:clients} (user_id {{key}} REFERENCES {table:identity.users}(id));"]);
        var layout = StateLayout.Create(StateEngineIds.Sqlite, [trust]);

        Assert.Equal("CREATE INDEX dbdatakit_identity_users_by_id ON dbdatakit_identity_users(id);", layout.Render(identity.Migrations[1], "identity"));
        Assert.Equal("SELECT * FROM dbdatakit_identity_users", layout.Render("SELECT * FROM {table:identity.users}"));
        Assert.Throws<InvalidOperationException>(() => layout.Render("SELECT * FROM {table:users}"));
        Assert.Throws<InvalidOperationException>(() => layout.Render("SELECT * FROM {table:identity.nothing}"));
    }

    [Fact]
    public void A_reference_to_a_module_that_is_not_a_declared_dependency_is_refused_naming_both()
    {
        var identity = Module("identity");
        var trust = Module("trust", migrations: "CREATE TABLE {table:clients} (u {{key}} REFERENCES {table:identity.identity}(id));");

        var ex = Assert.Throws<InvalidOperationException>(() => StateLayout.Create(StateEngineIds.Sqlite, [trust, identity]));

        Assert.Contains("trust", ex.Message);
        Assert.Contains("identity", ex.Message);
        Assert.Contains("dependency", ex.Message);
    }

    [Fact]
    public void A_narrower_module_cannot_be_depended_on_by_a_wider_one()
    {
        var sessions = Module("sessions", StateScopeKind.Group);

        Assert.Throws<ArgumentException>(() => Module("identity", StateScopeKind.Install, [sessions]));
    }

    [Fact]
    public void A_table_name_over_the_engines_limit_names_the_setting()
    {
        var settings = new StateNamespaceSettings { Default = new NamespaceSetting(Prefix: new string('p', 60) + "_") };

        var postgres = Assert.Throws<InvalidOperationException>(() => StateLayout.Create(StateEngineIds.Postgres, [Module("identity")], settings: settings));
        Assert.Contains("state.namespaces.identity.prefix", postgres.Message);
        Assert.Contains("63", postgres.Message);

        // SQL Server allows 128, so the same prefix is fine there
        StateLayout.Create(StateEngineIds.MsSql, [Module("identity")], settings: settings);
    }

    [Fact]
    public void An_invalid_identifier_in_a_setting_is_refused()
    {
        var settings = new StateNamespaceSettings { Default = new NamespaceSetting(Schema: "my schema") };

        var ex = Assert.Throws<InvalidOperationException>(() => StateLayout.Create(StateEngineIds.Postgres, [Module("identity")], settings: settings));

        Assert.Contains("my schema", ex.Message);
        Assert.Contains("schema", ex.Message);
    }

    [Fact]
    public void Two_namespaces_rendering_to_one_table_stop_the_start()
    {
        // one schema and no prefix for both: both modules' `shared` table is dbdatakit.shared
        var a = Module("a", migrations: "CREATE TABLE {table:shared} (id {{key}});");
        var b = Module("b", migrations: "CREATE TABLE {table:shared} (id {{key}});");
        var settings = new StateNamespaceSettings { Default = new NamespaceSetting(Schema: "dbdatakit", Prefix: "") };

        var ex = Assert.Throws<InvalidOperationException>(() => StateLayout.Create(StateEngineIds.Postgres, [a, b], settings: settings));

        Assert.Contains("dbdatakit.shared", ex.Message);
        Assert.Contains("'a'", ex.Message);
        Assert.Contains("'b'", ex.Message);
    }

    [Fact]
    public void A_setting_for_a_module_the_database_does_not_use_is_refused()
    {
        var settings = new StateNamespaceSettings { Modules = new Dictionary<string, NamespaceSetting> { ["idnetity"] = new(Prefix: "x_") } };

        var ex = Assert.Throws<InvalidOperationException>(() => StateLayout.Create(StateEngineIds.Sqlite, [Module("identity")], settings: settings));

        Assert.Contains("idnetity", ex.Message);
        Assert.Contains("identity", ex.Message);
    }

    [Fact]
    public void A_module_name_must_be_lower_snake_case()
    {
        Assert.Throws<ArgumentException>(() => new StateModule("Identity", StateScopeKind.Install, []));
        Assert.Throws<ArgumentException>(() => new StateModule("my-module", StateScopeKind.Install, []));
    }
}
