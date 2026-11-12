namespace DbDataKit.State.Tests;

/// <summary>Plan 29b slice C02: the writer of the <c>state:</c> section, which keeps the rest of the file and never leaves a half-written or invalid one.</summary>
public sealed class StateSettingsWriterTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("state-writer").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private string File(string text)
    {
        var path = Path.Combine(_directory, "server.yaml");
        System.IO.File.WriteAllText(path, text);
        return path;
    }

    private static StateSettings Everything() => new("postgres", "Host=db;Database=dbdatakit;Username=svc;Password=secret:state-db;Application Name=\"a: b\"")
    {
        Namespaces = new StateNamespaceSettings
        {
            Registry = new NamespaceSetting(Schema: "ops"),
            Default = new NamespaceSetting("dbdatakit", "{module}_"),
            Modules = new Dictionary<string, NamespaceSetting> { ["identity"] = new(Schema: "ident"), ["runs"] = new(File: "runs.db"), ["audit"] = new() },
        },
        Retention = new RetentionSettings
        {
            Days = 0.25,
            Modules = new Dictionary<string, double> { ["runs"] = 7 },
            Tables = new Dictionary<string, double> { ["audit.entry"] = RetentionSettings.Forever, ["runs.run"] = 14.5 },
        },
    };

    private const string Existing = """
        # The server's settings, edited by hand.
        listen: 127.0.0.1:5000   # where it listens

        state:
          # the old state, to be replaced
          engine: sqlite
          retention: { days: 5 }
        # about the logging
        logging:
          level: debug   # noisy
        """ + "\n";

    [Fact]
    public void Every_setting_round_trips()
    {
        var path = File(Existing);
        var settings = Everything();

        new StateSettingsWriter().Write(path, settings);
        var read = StateSettingsWriter.Load(path);

        Assert.Equal(settings.Engine, read.Engine);
        Assert.Equal(settings.ConnectionString, read.ConnectionString);
        Assert.Equal(settings.Namespaces.Registry, read.Namespaces.Registry);
        Assert.Equal(settings.Namespaces.Default, read.Namespaces.Default);
        Assert.Equal(settings.Namespaces.Modules.OrderBy(p => p.Key), read.Namespaces.Modules.OrderBy(p => p.Key));
        Assert.Equal(0.25, read.Retention.Days);
        Assert.Equal(settings.Retention.Modules, read.Retention.Modules);
        Assert.Equal(settings.Retention.Tables, read.Retention.Tables);
    }

    [Fact]
    public void Defaults_and_sqlite_round_trip()
    {
        var path = Path.Combine(_directory, "new", "server.yaml");

        new StateSettingsWriter().Write(path, new StateSettings());

        Assert.Equal("state: {}\n", System.IO.File.ReadAllText(path));
        Assert.True(StateSettingsWriter.Load(path).IsSqlite);
        new StateSettingsWriter().Write(path, new StateSettings("sqlite"));
        Assert.Equal("sqlite", StateSettingsWriter.Load(path).Engine);
    }

    [Fact]
    public void A_missing_file_is_created()
    {
        var path = Path.Combine(_directory, "fresh.yaml");

        new StateSettingsWriter().Write(path, new StateSettings("postgres", "Host=db"));

        Assert.Equal("state:\n  engine: postgres\n  connection: \"Host=db\"\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public void The_rest_of_the_file_and_its_comments_survive_and_only_the_state_block_changes()
    {
        var path = File(Existing);

        new StateSettingsWriter().Write(path, new StateSettings("mssql", "Server=s;Database=d;Password=env:DB_PASSWORD"));

        var text = System.IO.File.ReadAllText(path);
        Assert.Equal("""
            # The server's settings, edited by hand.
            listen: 127.0.0.1:5000   # where it listens

            state:
              engine: mssql
              connection: "Server=s;Database=d;Password=env:DB_PASSWORD"
            # about the logging
            logging:
              level: debug   # noisy
            """ + "\n", text);
    }

    [Fact]
    public void A_section_is_added_at_the_end_when_the_file_has_none_and_a_file_without_a_final_newline_is_kept()
    {
        var path = File("a: 1  # one\nb: 2");

        new StateSettingsWriter().Write(path, new StateSettings("postgres", "Host=db"));

        Assert.Equal("a: 1  # one\nb: 2\nstate:\n  engine: postgres\n  connection: \"Host=db\"\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public void Line_endings_are_kept()
    {
        var path = File("a: 1\r\nstate:\r\n  engine: sqlite\r\nz: 2\r\n");

        new StateSettingsWriter().Write(path, new StateSettings("postgres", "Host=db"));

        Assert.Equal("a: 1\r\nstate:\r\n  engine: postgres\r\n  connection: \"Host=db\"\r\nz: 2\r\n", System.IO.File.ReadAllText(path));
    }

    [Fact]
    public void Remove_takes_out_only_the_section()
    {
        var path = File(Existing);

        Assert.True(new StateSettingsWriter().Remove(path));

        Assert.Equal("# The server's settings, edited by hand.\nlisten: 127.0.0.1:5000   # where it listens\n\n# about the logging\nlogging:\n  level: debug   # noisy\n", System.IO.File.ReadAllText(path));
        Assert.False(new StateSettingsWriter().Remove(path));
        Assert.False(new StateSettingsWriter().Remove(Path.Combine(_directory, "absent.yaml")));
    }

    [Theory]
    [InlineData("postgres", null, "state.connection")]
    [InlineData("oracle", null, "state.engine")]
    [InlineData("sqlite", null, "state.retention.days")]
    public void An_invalid_setting_is_refused_naming_the_file_and_the_key_and_the_file_is_untouched(string engine, string? connection, string key)
    {
        var path = File(Existing);
        var before = System.IO.File.ReadAllBytes(path);
        var settings = new StateSettings(engine, connection) { Retention = key == "state.retention.days" ? new RetentionSettings { Days = 0 } : new RetentionSettings() };

        var ex = Assert.Throws<InvalidOperationException>(() => new StateSettingsWriter().Write(path, settings));

        Assert.Contains(path, ex.Message);
        Assert.Contains(key, ex.Message);
        Assert.Equal(before, System.IO.File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void A_table_retention_and_a_module_named_default_are_refused_the_same_way()
    {
        var path = File(Existing);
        var before = System.IO.File.ReadAllBytes(path);
        var negative = new StateSettings { Retention = new RetentionSettings { Tables = new Dictionary<string, double> { ["runs.run"] = -2 } } };
        var named = new StateSettings { Namespaces = new StateNamespaceSettings { Modules = new Dictionary<string, NamespaceSetting> { ["default"] = new() } } };

        Assert.Contains("state.retention.tables.runs.run", Assert.Throws<InvalidOperationException>(() => new StateSettingsWriter().Write(path, negative)).Message);
        Assert.Contains("state.namespaces.default", Assert.Throws<InvalidOperationException>(() => new StateSettingsWriter().Write(path, named)).Message);
        Assert.Equal(before, System.IO.File.ReadAllBytes(path));
    }

    [Fact]
    public void A_file_that_is_not_valid_yaml_is_refused_and_left_as_it_is()
    {
        var path = File("a: [1, 2\nstate:\n  engine: sqlite\n");
        var before = System.IO.File.ReadAllBytes(path);

        var ex = Assert.Throws<InvalidOperationException>(() => new StateSettingsWriter().Write(path, new StateSettings(StateEngineIds.Sqlite)));

        Assert.Contains(path, ex.Message);
        Assert.Equal(before, System.IO.File.ReadAllBytes(path));
    }

    [Fact]
    public void A_failure_between_the_temporary_file_and_the_rename_leaves_the_original_and_no_temporary_file()
    {
        var path = File(Existing);
        var before = System.IO.File.ReadAllBytes(path);
        string? temp = null;
        var writer = new StateSettingsWriter((from, _) =>
        {
            temp = from;
            Assert.True(System.IO.File.Exists(from));
            Assert.NotEqual(before, System.IO.File.ReadAllBytes(from));
            throw new IOException("the disk went away");
        });

        Assert.Throws<IOException>(() => writer.Write(path, new StateSettings("postgres", "Host=db")));

        Assert.Equal(before, System.IO.File.ReadAllBytes(path));
        Assert.NotNull(temp);
        Assert.Equal(_directory, Path.GetDirectoryName(temp));
        Assert.False(System.IO.File.Exists(temp));
    }

    [Fact]
    public void Secret_and_environment_references_are_written_verbatim_and_never_resolved_or_quoted_in_messages()
    {
        var path = File(Existing);
        const string connection = "Host=db;Username=svc;Password=secret:state-db;Pwd2=env:STATE_PASSWORD";

        new StateSettingsWriter().Write(path, new StateSettings("postgres", connection));

        Assert.Contains($"connection: \"{connection}\"", System.IO.File.ReadAllText(path));
        Assert.Equal(connection, StateSettingsWriter.Load(path).ConnectionString);
        var ex = Assert.Throws<InvalidOperationException>(() => new StateSettingsWriter().Write(path, new StateSettings("oracle", connection)));
        Assert.DoesNotContain("state-db", ex.Message);
    }

    [Fact]
    public void A_connection_with_quotes_and_backslashes_survives()
    {
        var path = File(Existing);
        const string connection = "Server=s;Password=\"p\\w\";Note=it's";

        new StateSettingsWriter().Write(path, new StateSettings("mssql", connection));

        Assert.Equal(connection, StateSettingsWriter.Load(path).ConnectionString);
    }
}
