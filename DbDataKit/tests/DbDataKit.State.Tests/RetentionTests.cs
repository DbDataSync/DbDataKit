namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q05: the shared retention job, on every engine, with a clock the test moves.</summary>
public sealed class RetentionTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-06T12:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private static StateModule History() => new(
        "history", StateScopeKind.Install,
        [
            """
            CREATE TABLE {table:event} (id {{uuid}} PRIMARY KEY, happened_at {{time}} NOT NULL);
            CREATE TABLE {table:dead_letter} (id {{uuid}} PRIMARY KEY, created_at {{time}} NOT NULL, status {{key}} NOT NULL);
            CREATE TABLE {table:audit} (id {{uuid}} PRIMARY KEY, recorded_at {{time}} NOT NULL);
            CREATE TABLE {table:setting} (name {{key}} PRIMARY KEY, changed_at {{time}} NOT NULL);
            """
        ],
        retention:
        [
            new RetentionRule("event", "happened_at"),
            new RetentionRule("dead_letter", "created_at", Where: "status <> 'open'"),
            new RetentionRule("audit", "recorded_at", DefaultDays: null),
        ]);

    private (StateDatabase Database, StateMigratorTests.Clock Clock) Open(string engine)
    {
        var clock = new StateMigratorTests.Clock(Start);
        return (_engines.Provision(engine).Open([History(), PayloadStore.Module], time: clock), clock);
    }

    private static void Add(StateDatabase database, string table, DateTimeOffset at, string? status = null)
    {
        using var connection = database.OpenConnection();
        var (timeColumn, extra) = table switch
        {
            "event" => ("happened_at", ""),
            "dead_letter" => ("created_at", ", status"),
            "audit" => ("recorded_at", ""),
            _ => ("changed_at", ""),
        };
        using var insert = table == "setting"
            ? database.Command("history", connection, "INSERT INTO {table:setting} (name, changed_at) VALUES ($id, $at);")
            : database.Command("history", connection, $"INSERT INTO {{table:{table}}} (id, {timeColumn}{extra}) VALUES ($id, $at{(status is null ? "" : ", $status")});");
        insert.Bind(database, "id", table == "setting" ? Guid.NewGuid().ToString() : database.NewId()).Bind(database, "at", at);
        if (status is not null) insert.Bind(database, "status", status);
        insert.ExecuteNonQuery();
    }

    private static long Count(StateDatabase database, string table)
    {
        using var connection = database.OpenConnection();
        using var count = database.Command("history", connection, $"SELECT COUNT(*) FROM {{table:{table}}};");
        return Convert.ToInt64(count.ExecuteScalar());
    }

    [Fact]
    public void The_module_with_rules_follows_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(History()));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Rows_older_than_thirty_days_go_and_newer_ones_stay(string engine)
    {
        var (database, _) = Open(engine);
        Add(database, "event", Start.AddDays(-31));
        Add(database, "event", Start.AddDays(-29));
        Add(database, "event", Start);

        var result = new RetentionJob(database).Run();

        Assert.Equal(1, result.Removed["history.event"]);
        Assert.Equal(2, Count(database, "event"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_fraction_of_a_day_is_hours_and_the_clock_moves_what_counts_as_old(string engine)
    {
        var (database, clock) = Open(engine);
        Add(database, "event", Start.AddHours(-5));
        Add(database, "event", Start.AddHours(-7));
        var job = new RetentionJob(database, new RetentionSettings { Days = 0.25 });

        Assert.Equal(1, job.Run().Removed["history.event"]);

        clock.Advance(TimeSpan.FromHours(2)); // the five-hour-old row is now seven hours old
        Assert.Equal(1, job.Run().Removed["history.event"]);
        Assert.Equal(0, Count(database, "event"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_table_setting_beats_a_module_setting_beats_the_global_one(string engine)
    {
        var (database, _) = Open(engine);
        Add(database, "event", Start.AddDays(-10));
        Add(database, "dead_letter", Start.AddDays(-10), "replayed");
        var settings = new RetentionSettings
        {
            Days = 5,
            Modules = new Dictionary<string, double> { ["history"] = 20 },
            Tables = new Dictionary<string, double> { ["history.dead_letter"] = 7 },
        };

        var result = new RetentionJob(database, settings).Run();

        Assert.False(result.Removed.GetValueOrDefault("history.event") > 0); // the module's 20 days beats the global 5
        Assert.Equal(1, result.Removed["history.dead_letter"]);              // the table's 7 days beats the module's 20
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_audit_is_kept_until_a_limit_is_set_for_it(string engine)
    {
        var (database, _) = Open(engine);
        Add(database, "audit", Start.AddDays(-4000));

        var global = new RetentionJob(database, new RetentionSettings { Days = 1 }).Run();
        Assert.DoesNotContain("history.audit", global.Removed.Keys);
        Assert.Equal(1, Count(database, "audit"));

        var named = new RetentionJob(database, new RetentionSettings { Tables = new Dictionary<string, double> { ["history.audit"] = 365 } }).Run();
        Assert.Equal(1, named.Removed["history.audit"]);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Forever_keeps_a_table_and_a_module_without_a_rule_is_never_touched(string engine)
    {
        var (database, _) = Open(engine);
        Add(database, "event", Start.AddDays(-400));
        Add(database, "setting", Start.AddDays(-400));
        var settings = new RetentionSettings { Tables = new Dictionary<string, double> { ["history.event"] = RetentionSettings.Forever } };

        new RetentionJob(database, settings).Run();

        Assert.Equal(1, Count(database, "event"));
        Assert.Equal(1, Count(database, "setting")); // current state: no rule, no age
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_rules_condition_keeps_what_is_not_settled(string engine)
    {
        var (database, _) = Open(engine);
        Add(database, "dead_letter", Start.AddDays(-90), "open");
        Add(database, "dead_letter", Start.AddDays(-90), "discarded");

        new RetentionJob(database).Run();

        Assert.Equal(1, Count(database, "dead_letter"));
    }

    [Fact]
    public void Settings_that_cannot_be_applied_say_which_setting_and_why()
    {
        using var engines = new StateEngines();
        var database = engines.Provision(StateEngineIds.Sqlite).Open([History()]);

        Assert.Contains("retention.days", Assert.Throws<InvalidOperationException>(() => new RetentionJob(database, new RetentionSettings { Days = 0 })).Message);
        Assert.Contains("'nothing'", Assert.Throws<InvalidOperationException>(() => new RetentionJob(database, new RetentionSettings { Modules = new Dictionary<string, double> { ["nothing"] = 3 } })).Message);
        Assert.Contains("history.setting", Assert.Throws<InvalidOperationException>(() => new RetentionJob(database, new RetentionSettings { Tables = new Dictionary<string, double> { ["history.setting"] = 3 } })).Message);
    }

    // ---- payloads ----

    private static StateModule Owner() => new(
        "docs", StateScopeKind.Install,
        ["CREATE TABLE {table:doc} (id {{uuid}} PRIMARY KEY, body_payload {{uuid}} NULL);"],
        dependsOn: [PayloadStore.Module],
        payloadOwners: [new PayloadOwner("doc")]);

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Payloads_a_dead_writer_left_and_payloads_whose_owner_is_gone_are_removed_with_their_chunks(string engine)
    {
        var clock = new StateMigratorTests.Clock(Start);
        var database = _engines.Provision(engine).Open([Owner()], time: clock);
        var store = new PayloadStore(database, chunkSize: 1024);
        var data = new byte[3000];

        Guid Write(PayloadOptions? options, bool complete)
        {
            using var writer = store.OpenWrite(options);
            writer.Write(data);
            if (complete) writer.Complete();
            return writer.Id;
        }

        var docId = database.NewId();
        using (var connection = database.OpenConnection())
        {
            using var insert = database.Command("docs", connection, "INSERT INTO {table:doc} (id) VALUES ($id);");
            insert.Bind(database, "id", docId);
            insert.ExecuteNonQuery();
        }

        var kept = Write(new PayloadOptions(OwnerModule: "docs", OwnerRow: docId.ToString("D")), complete: true);
        var unowned = Write(null, complete: true);
        var orphan = Write(new PayloadOptions(OwnerModule: "docs", OwnerRow: database.NewId().ToString("D")), complete: true);
        var abandoned = Write(null, complete: false);

        // within the grace period an unowned reference is a writer that has not yet stored its row
        var early = new RetentionJob(database).Run();
        Assert.DoesNotContain("payloads.orphans", early.Removed.Keys);
        Assert.NotNull(store.Get(orphan));

        clock.Advance(TimeSpan.FromHours(2));
        var later = new RetentionJob(database).Run();
        Assert.Equal(1, later.Removed["payloads.orphans"]);
        Assert.Null(store.Get(orphan));
        Assert.NotNull(store.Get(kept));
        Assert.NotNull(store.Get(unowned));
        Assert.NotNull(store.Get(abandoned)); // an abandoned write is removed at the retention age, not at the grace

        clock.Advance(TimeSpan.FromDays(31));
        var result = new RetentionJob(database).Run();
        Assert.Equal(1, result.Removed["payloads.payload"]);
        Assert.Null(store.Get(abandoned));
        Assert.NotNull(store.Get(unowned));
        Assert.NotNull(store.Get(kept));

        using var verify = database.OpenConnection();
        using var chunks = database.Command("payloads", verify, "SELECT COUNT(*) FROM {table:chunk};");
        Assert.Equal(6L, Convert.ToInt64(chunks.ExecuteScalar())); // the two payloads that remain keep their three chunks each
    }
}
