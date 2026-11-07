namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q06: the <c>state:</c> section of server.yaml, strictly, and the opener that builds a database from modules.</summary>
public sealed class StateSettingsReaderTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("state-settings").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static Dictionary<string, object?> Map(params (string Key, object? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);

    private static StateModule Things() => new("things", StateScopeKind.Install, ["CREATE TABLE {table:thing} (id {{key}} PRIMARY KEY);"]);

    [Fact]
    public void Nothing_is_the_defaults_SQLite_and_thirty_days()
    {
        var settings = StateSettingsReader.Read(null);

        Assert.Equal(StateEngineIds.Sqlite, settings.EngineId);
        Assert.Null(settings.Retention.Days);
        Assert.Equal("dbdatakit_things_thing", StateLayout.Create(StateEngineIds.Sqlite, [Things()], settings: settings.Namespaces).Table("things", "thing"));
    }

    [Fact]
    public void The_whole_section_is_read()
    {
        var section = Map(
            ("engine", "postgres"),
            ("connection", "Host=db;Database=x"),
            ("registry", Map(("schema", "ops"))),
            ("namespaces", Map(
                ("default", Map(("schema", "dbdatakit"), ("prefix", "{module}_"))),
                ("things", Map(("schema", "own"))))),
            ("retention", Map(
                ("days", 0.25),
                ("modules", Map(("runs", 7))),
                ("tables", Map(("audit.entry", "forever"), ("runs.run", "14"))))));

        var settings = StateSettingsReader.Read(section);

        Assert.Equal(StateEngineIds.Postgres, settings.EngineId);
        Assert.Equal("Host=db;Database=x", settings.ConnectionString);
        var layout = StateLayout.Create(StateEngineIds.Postgres, [Things()], settings: settings.Namespaces);
        Assert.Equal("own.things_thing", layout.Table("things", "thing"));
        Assert.Equal("ops.modules", layout.Registry);
        Assert.Equal(0.25, settings.Retention.Days);
        Assert.Equal(7, settings.Retention.Modules["runs"]);
        Assert.Equal(RetentionSettings.Forever, settings.Retention.Tables["audit.entry"]);
        Assert.Equal(14, settings.Retention.Tables["runs.run"]);
    }

    [Theory]
    [InlineData("engin", "state.engin", "engine")]
    public void A_misspelt_key_is_named_with_the_one_that_was_meant(string key, string path, string meant)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StateSettingsReader.Read(Map((key, "x"))));

        Assert.Contains(path, ex.Message);
        Assert.Contains($"did you mean '{meant}'", ex.Message);
    }

    [Fact]
    public void A_namespace_can_name_a_sqlite_file_of_its_own()
    {
        var settings = StateSettingsReader.Read(Map(("namespaces", Map(("things", Map(("file", "things.db")))))));

        Assert.Equal("things.db", settings.Namespaces.Modules["things"].File);
        Assert.Equal("f_things.dbdatakit_things_thing", StateLayout.Create(StateEngineIds.Sqlite, [Things()], settings: settings.Namespaces).Table("things", "thing"));
    }

    [Fact]
    public void A_misspelt_namespace_key_names_its_whole_path()
    {
        var section = Map(("namespaces", Map(("identity", Map(("shcema", "x"))))));

        var ex = Assert.Throws<InvalidOperationException>(() => StateSettingsReader.Read(section));

        Assert.Contains("state.namespaces.identity.shcema", ex.Message);
        Assert.Contains("did you mean 'schema'", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("soon")]
    public void A_retention_figure_that_is_not_days_above_zero_or_forever_is_refused_naming_it(string value)
    {
        var section = Map(("retention", Map(("tables", Map(("runs.run", value))))));

        var ex = Assert.Throws<InvalidOperationException>(() => StateSettingsReader.Read(section));

        Assert.Contains("state.retention.tables.runs.run", ex.Message);
        Assert.Contains("forever", ex.Message);
    }

    [Fact]
    public void A_single_value_where_a_set_of_keys_belongs_is_refused()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StateSettingsReader.Read(Map(("namespaces", "all of them"))));

        Assert.Contains("state.namespaces", ex.Message);
        Assert.Contains("map", ex.Message);
    }

    [Fact]
    public void The_opener_builds_a_database_from_modules_in_the_namespaces_the_settings_name()
    {
        var settings = new StateSettings { Namespaces = new StateNamespaceSettings { Default = new NamespaceSetting(Prefix: "mine_") } };

        var database = StateOpener.Open(settings, Path.Combine(_directory, "state.db"), [Things()]);

        Assert.Equal("mine_thing", database.Layout!.Table("things", "thing"));
        Assert.Equal("dbdatakit_modules", database.Layout.Registry);
    }

    [Fact]
    public void A_setting_for_a_module_the_service_does_not_use_stops_the_start_naming_it()
    {
        var settings = new StateSettings { Namespaces = new StateNamespaceSettings { Modules = new Dictionary<string, NamespaceSetting> { ["idnetity"] = new("x") } } };

        var ex = Assert.Throws<InvalidOperationException>(() => StateOpener.Open(settings, Path.Combine(_directory, "state.db"), [Things()]));

        Assert.Contains("idnetity", ex.Message);
    }

    [Fact]
    public void A_server_engine_without_a_connection_string_still_says_what_is_missing()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => StateOpener.Open(new StateSettings("postgres"), Path.Combine(_directory, "x.db"), [Things()]));

        Assert.Contains("needs a connection string", ex.Message);
    }
}
