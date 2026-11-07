namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q03: the conventions as a check, and the convention types as values, on every engine.</summary>
public sealed class StateConventionsTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private static StateModule Module(params string[] migrations) => new("sample", StateScopeKind.Install, migrations);

    [Fact]
    public void A_module_that_follows_the_conventions_has_no_violations()
    {
        var module = Module(
            """
            -- ids are UUIDs; times are *_at; a document is json; the name is a key
            CREATE TABLE {table:thing} (
                id          {{uuid}} PRIMARY KEY,
                name        {{key}} NOT NULL,
                created_at  {{time}} NOT NULL,
                doc         {{json}} NULL,
                active      {{bool}} NOT NULL,
                row_version {{int}} NOT NULL,
                UNIQUE (name)
            );
            CREATE INDEX {index:thing_by_created} ON {table:thing}(created_at);
            """,
            "ALTER TABLE {table:thing} {{addcolumn}} seen_at {{time}} NULL;");

        Assert.Empty(StateConventions.Violations(module));
    }

    [Theory]
    [InlineData("CREATE TABLE {table:t} (Id {{key}} PRIMARY KEY);", "t.Id", "lower case")]
    [InlineData("CREATE TABLE {table:t} (id {{key}} PRIMARY KEY, created {{time}} NOT NULL);", "t.created", "*_at")]
    [InlineData("CREATE TABLE {table:t} (id {{key}} PRIMARY KEY, created_at {{key}} NOT NULL);", "t.created_at", "{{time}}")]
    [InlineData("CREATE TABLE {table:t} (id {{key}} PRIMARY KEY, note TEXT NULL);", "t.note", "TEXT")]
    [InlineData("CREATE TABLE {table:t} (id {{key}} PRIMARY KEY, n INTEGER NOT NULL);", "t.n", "INTEGER")]
    [InlineData("CREATE TABLE {table:t} (id {{text}} PRIMARY KEY);", "t.id", "{{key}}")]
    [InlineData("CREATE TABLE {table:t} (id {{key}} PRIMARY KEY, name {{text}} NOT NULL, UNIQUE (name));", "t.name", "{{key}}")]
    [InlineData("CREATE TABLE {table:t} (id {{key}} PRIMARY KEY, name {{text}} NOT NULL); CREATE INDEX {index:i} ON {table:t}(name);", "t.name", "index")]
    [InlineData("CREATE TABLE {table:t} (id {{key}} PRIMARY KEY, row_version {{key}} NOT NULL);", "t.row_version", "{{int}}")]
    [InlineData("CREATE TABLE {table:t} (id {{key}} PRIMARY KEY); ALTER TABLE {table:t} {{addcolumn}} Seen_At {{time}} NULL;", "t.Seen_At", "lower case")]
    public void A_broken_convention_is_named_by_table_and_column(string migration, string where, string rule)
    {
        var violations = StateConventions.Violations(Module(migration));

        Assert.Contains(violations, v => v.Contains(where, StringComparison.Ordinal) && v.Contains(rule, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_convention_types_round_trip_the_same_on_every_engine(string engine)
    {
        var module = Module(
            """
            CREATE TABLE {table:thing} (
                id          {{uuid}} PRIMARY KEY,
                created_at  {{time}} NOT NULL,
                doc         {{json}} NULL,
                active      {{bool}} NOT NULL,
                bytes       {{blob}} NULL,
                row_version {{int}} NOT NULL
            );
            """);
        var database = _engines.Provision(engine).Open([module]);
        var id = database.NewId();
        var at = new DateTimeOffset(2026, 11, 6, 9, 15, 30, 123, TimeSpan.FromHours(-5)).AddTicks(4560);
        byte[] bytes = [0, 1, 2, 255];

        using (var connection = database.OpenConnection())
        {
            using var insert = database.Command("sample", connection, "INSERT INTO {table:thing} (id, created_at, doc, active, bytes, row_version) VALUES ($id, $at, CAST($doc AS {{json}}), $active, $bytes, 1);");
            insert.Bind(database, "id", id).Bind(database, "at", at).Bind(database, "doc", "{\"a\":1}").Bind(database, "active", true).Bind(database, "bytes", bytes);
            insert.ExecuteNonQuery();

            using var select = database.Command("sample", connection, "SELECT id, created_at, doc, active, bytes FROM {table:thing} WHERE id = $id AND created_at = $at;");
            select.Bind(database, "id", id).Bind(database, "at", at);
            using var reader = select.ExecuteReader();
            Assert.True(reader.Read());
            Assert.Equal(id, reader.Uuid(0));
            // an instant is the same instant, in UTC, to the tick PostgreSQL keeps (microseconds)
            Assert.Equal(at.UtcDateTime.Ticks / 10, reader.Time(1).UtcDateTime.Ticks / 10);
            Assert.Equal(TimeSpan.Zero, reader.Time(1).Offset);
            Assert.Contains("\"a\"", reader.GetString(2));
            Assert.True(reader.Bool(3));
            Assert.Equal(bytes, (byte[])reader.GetValue(4));
        }
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Times_order_as_time_does_so_a_comparison_in_sql_means_what_it_says(string engine)
    {
        var module = Module("CREATE TABLE {table:event} (id {{uuid}} PRIMARY KEY, at {{key}} NULL, happened_at {{time}} NOT NULL);");
        var database = _engines.Provision(engine).Open([module]);
        // the awkward ones for text: whole seconds, then a fraction, then a later hour, across a day boundary
        DateTimeOffset[] times =
        [
            DateTimeOffset.Parse("2026-01-01T23:59:59Z"),
            DateTimeOffset.Parse("2026-01-01T23:59:59.5Z"),
            DateTimeOffset.Parse("2026-01-02T00:00:00Z"),
            DateTimeOffset.Parse("2026-01-02T01:00:00+05:00"),   // 20:00 the day before in UTC: the earliest
        ];
        using var connection = database.OpenConnection();
        foreach (var time in times)
        {
            using var insert = database.Command("sample", connection, "INSERT INTO {table:event} (id, happened_at) VALUES ($id, $at);");
            insert.Bind(database, "id", database.NewId()).Bind(database, "at", time);
            insert.ExecuteNonQuery();
        }

        using var select = database.Command("sample", connection, "SELECT happened_at FROM {table:event} ORDER BY happened_at;");
        var read = new List<DateTimeOffset>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read()) read.Add(reader.Time(0));
        }

        Assert.Equal(times.OrderBy(t => t.UtcDateTime).Select(t => t.UtcDateTime), read.Select(t => t.UtcDateTime));

        using var count = database.Command("sample", connection, "SELECT COUNT(*) FROM {table:event} WHERE happened_at < $cutoff;");
        count.Bind(database, "cutoff", DateTimeOffset.Parse("2026-01-01T23:59:59.25Z"));
        Assert.Equal(2L, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Fact]
    public void A_table_named_with_a_word_one_engine_reserves_is_refused_on_every_engine()
    {
        var module = Module("CREATE TABLE {table:checkpoint} (id {{key}} PRIMARY KEY);");

        var ex = Assert.Throws<InvalidOperationException>(() => StateLayout.Create(StateEngineIds.Sqlite, [module], settings: new StateNamespaceSettings { Default = new NamespaceSetting(Prefix: "") }));

        Assert.Contains("checkpoint", ex.Message);
        Assert.Contains("reserved", ex.Message);
        Assert.Contains(StateConventions.Violations(module), v => v.Contains("checkpoint") && v.Contains("reserved"));
        // with SQLite's default prefix the rendered name is fine, which is why the module's own convention test is the early warning
        StateLayout.Create(StateEngineIds.Sqlite, [module]);
    }

    [Fact]
    public void A_column_named_with_a_reserved_word_is_a_convention_violation()
    {
        var violations = StateConventions.Violations(Module("CREATE TABLE {table:t} (id {{key}} PRIMARY KEY, \"user\" {{key}} NULL, order {{int}} NULL);".Replace("\"user\"", "user")));

        Assert.Contains(violations, v => v.Contains("t.user") && v.Contains("reserved"));
        Assert.Contains(violations, v => v.Contains("t.order") && v.Contains("reserved"));
    }

    [Fact]
    public void New_ids_are_version_7_and_follow_the_clock()
    {
        var clock = new StateTestClock(DateTimeOffset.Parse("2026-11-06T00:00:00Z"));
        using var engines = new StateEngines();
        var database = engines.Provision(StateEngineIds.Sqlite).Open([Module("CREATE TABLE {table:t} (id {{uuid}} PRIMARY KEY);")], time: clock);

        var first = database.NewId();
        clock.Advance(TimeSpan.FromMinutes(5));
        var second = database.NewId();

        Assert.Equal(7, first.Version);
        Assert.True(string.CompareOrdinal(first.ToString(), second.ToString()) < 0);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_versioned_update_applies_once_and_the_second_writer_is_refused(string engine)
    {
        var module = Module("CREATE TABLE {table:position} (id {{key}} PRIMARY KEY, value {{key}} NOT NULL, row_version {{int}} NOT NULL);");
        var database = _engines.Provision(engine).Open([module]);
        using var connection = database.OpenConnection();
        using (var insert = database.Command("sample", connection, "INSERT INTO {table:position} (id, value, row_version) VALUES ('c', 'a', 1);"))
            insert.ExecuteNonQuery();

        bool Move(string to, long expected) => database.UpdateVersioned("sample", connection, null, "position", "value = $value", "id = 'c'", expected, c => c.Bind(database, "value", to));

        Assert.True(Move("b", 1));
        Assert.False(Move("c", 1)); // the other writer still holds version 1: refused
        Assert.True(Move("c", 2));

        using var read = database.Command("sample", connection, "SELECT value, row_version FROM {table:position};");
        using var reader = read.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Equal("c", reader.GetString(0));
        Assert.Equal(3L, reader.Int64(1));
    }
}
