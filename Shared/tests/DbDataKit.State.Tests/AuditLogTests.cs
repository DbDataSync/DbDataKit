namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q13: the shared audit, on every engine.</summary>
public sealed class AuditLogTests : IDisposable
{
    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine, TimeProvider? time = null) => _engines.Provision(engine).Open([AuditModule.Module], time: time);

    [Fact]
    public void The_module_follows_the_conventions() => Assert.Empty(StateConventions.Violations(AuditModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_entry_names_who_acted_who_asked_what_it_came_through_and_what_it_touched(string engine)
    {
        var log = new AuditLog(Open(engine), "serving");

        var id = log.Record(new AuditEvent("action", "action:submit-claim@3", "claim.submit", AuditEvent.Ok, RequestedBy: "alice", ViaKind: "app", ViaId: "expenses",
            TargetKind: "claim", TargetId: "c-17", Detail: "{\"from\":\"Draft\",\"to\":\"Submitted\"}"));

        var record = Assert.Single(log.Read());
        Assert.Equal(id, record.Id);
        Assert.Equal("serving", record.ServiceId);
        Assert.Equal(("action", "action:submit-claim@3", "claim.submit", "ok"), (record.Entry.ActorKind, record.Entry.ActorId, record.Entry.Verb, record.Entry.Outcome));
        Assert.Equal(("alice", "app", "expenses"), (record.Entry.RequestedBy, record.Entry.ViaKind, record.Entry.ViaId));
        Assert.Equal(("claim", "c-17"), (record.Entry.TargetKind, record.Entry.TargetId));
        Assert.Contains("Submitted", record.Entry.Detail);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Entries_come_newest_first_and_page_by_the_last_id_seen(string engine)
    {
        var log = new AuditLog(Open(engine), "console");
        for (var i = 1; i <= 5; i++) log.Record(new AuditEvent("person", "ada", "definition.publish", TargetKind: "definition", TargetId: $"d{i}"));

        var first = log.Read(new AuditQuery(Limit: 2));
        var next = log.Read(new AuditQuery(BeforeId: first[^1].Id, Limit: 2));

        Assert.Equal(["d5", "d4"], first.Select(r => r.Entry.TargetId));
        Assert.Equal(["d3", "d2"], next.Select(r => r.Entry.TargetId));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_query_narrows_by_actor_target_verb_service_and_time(string engine)
    {
        var clock = new StateTestClock(DateTimeOffset.Parse("2026-11-06T00:00:00Z"));
        var database = Open(engine, clock);
        var sync = new AuditLog(database, "sync");
        var proxy = new AuditLog(database, "proxy");
        sync.Record(new AuditEvent("person", "ada", "task.pause", TargetKind: "task", TargetId: "crm"));
        clock.Advance(TimeSpan.FromDays(1));
        proxy.Record(new AuditEvent("person", "grace", "grant.add", AuditEvent.Refused, TargetKind: "role", TargetId: "admin"));
        proxy.Record(new AuditEvent("agent", "agent:helper", "task.pause", TargetKind: "task", TargetId: "crm"));

        Assert.Equal(3, sync.Read().Count);   // one audit for the install: any service reads every service's entries
        Assert.Single(sync.Read(new AuditQuery(ActorId: "grace")));
        Assert.Equal(2, sync.Read(new AuditQuery(TargetKind: "task", TargetId: "crm")).Count);
        Assert.Equal(2, sync.Read(new AuditQuery(Verb: "task.pause")).Count);
        Assert.Equal(2, sync.Read(new AuditQuery(VerbPrefix: "task.")).Count);       // a prefix, with the LIKE wildcards in it taken literally
        Assert.Empty(sync.Read(new AuditQuery(VerbPrefix: "t_sk.")));
        Assert.Empty(sync.Read(new AuditQuery(VerbPrefix: "%")));
        Assert.Equal(2, sync.Read(new AuditQuery(ServiceId: "proxy")).Count);
        Assert.Equal(2, sync.Read(new AuditQuery(From: DateTimeOffset.Parse("2026-11-07T00:00:00Z"))).Count);
        Assert.Single(sync.Read(new AuditQuery(To: DateTimeOffset.Parse("2026-11-06T12:00:00Z"))));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_entry_recorded_in_a_transaction_stands_or_falls_with_it(string engine)
    {
        var database = Open(engine);
        var log = new AuditLog(database, "console");
        using (var connection = database.OpenConnection())
        {
            using var transaction = connection.BeginTransaction();
            AuditLog.Record(database, connection, transaction, "console", new AuditEvent("person", "ada", "grant.add"));
            transaction.Rollback();
        }

        Assert.Empty(log.Read());
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_audit_is_kept_until_a_limit_is_set(string engine)
    {
        var clock = new StateTestClock(DateTimeOffset.Parse("2026-11-06T00:00:00Z"));
        var database = Open(engine, clock);
        var log = new AuditLog(database, "console");
        log.Record(new AuditEvent("person", "ada", "grant.add"));
        clock.Advance(TimeSpan.FromDays(3000));
        log.Record(new AuditEvent("person", "ada", "grant.remove"));

        new RetentionJob(database).Run();
        Assert.Equal(2, log.Read().Count);

        new RetentionJob(database, new RetentionSettings { Tables = new Dictionary<string, double> { ["audit.entry"] = 365 } }).Run();
        Assert.Equal(["grant.remove"], log.Read().Select(r => r.Entry.Verb));
    }

    [Theory]
    [InlineData("person", "", "a.b", "ok", null)]
    [InlineData("Person", "ada", "a.b", "ok", null)]
    [InlineData("person", "ada", "publish", "ok", null)]
    [InlineData("person", "ada", "Definition.Publish", "ok", null)]
    [InlineData("person", "ada", "a.b", "OK", null)]
    [InlineData("person", "ada", "a.b", "ok", "not json")]
    public void An_entry_that_does_not_say_what_it_must_is_refused(string kind, string actor, string verb, string outcome, string? detail)
    {
        var log = new AuditLog(Open(StateEngineIds.Sqlite), "console");

        Assert.Throws<ArgumentException>(() => log.Record(new AuditEvent(kind, actor, verb, outcome, Detail: detail)));
    }
}
