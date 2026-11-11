using DbDataKit.State.Snapshots;

namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 27a AG06: the <c>agents</c> module on every state engine (SQLite always; SQL Server and PostgreSQL when <c>DBDATAKIT_TEST_SQLSERVER</c> and <c>DBDATAKIT_TEST_POSTGRES</c> name them): its conventions, layout and retention (what was said goes after
/// 30 days while the audit rows stay), every row naming the person, the agent and its version, and an approval that is answered once, by the person it is for.
/// </summary>
public sealed class AgentStoreTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-11T09:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    public static TheoryData<string> Engines => StateEngines.Engines;

    private (AgentStore Store, StateDatabase Database, StateTestClock Clock) Open(string engine)
    {
        var clock = new StateTestClock(Start);
        var database = _engines.Provision(engine).Open([AgentsModule.Module], time: clock);
        return (new AgentStore(database), database, clock);
    }

    private static long Count(StateDatabase database, string table)
    {
        using var connection = database.OpenConnection();
        using var count = database.Command(AgentsModule.Name, connection, $"SELECT COUNT(*) FROM {{table:{table}}};");
        return Convert.ToInt64(count.ExecuteScalar());
    }

    private static void Conversation(AgentStore store, string id = "c1") => Assert.True(store.Start(id, "build-helper", "v3", "ada", "Ada Lovelace", title: "Add a measure"));

    [Fact]
    public void The_module_follows_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(AgentsModule.Module));
        Assert.Empty(StateConventions.Violations(AgentsModule.GroupModule));
    }

    [Fact]
    public void Only_what_was_said_has_a_retention_rule_and_the_audit_has_none()
    {
        var rule = Assert.Single(AgentsModule.Module.Retention);
        Assert.Equal(("turn_text", "created_at", 30d), (rule.Table, rule.TimeColumn, rule.DefaultDays));
        Assert.Equal(["agent_token", "approval", "audit_event", "conversation", "turn_record", "turn_text"], StateTableDefinitions.Read(AgentsModule.Module).Select(t => t.Name).Order());
        Assert.All(StateTableDefinitions.Read(AgentsModule.Module), t => Assert.NotEmpty(t.PrimaryKey));
        Assert.Equal(StateScopeKind.Install, AgentsModule.Module.Scope);
        Assert.Equal(StateScopeKind.Group, AgentsModule.GroupModule.Scope);
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_layout_has_every_table_in_the_namespace_of_the_module(string engine)
    {
        var (_, database, _) = Open(engine);
        Assert.Equal("agents", database.Layout!.Namespace(AgentsModule.Name).Module.Name);
        Assert.Equal(2, Assert.Single(database.RegisteredModules(), m => m.Module == "agents").Version);
        foreach (var table in new[] { "conversation", "turn_text", "turn_record", "audit_event", "approval", "agent_token" }) Assert.Equal(0, Count(database, table));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_conversation_is_listed_for_its_person_and_agent_and_keeps_the_version_it_started_with(string engine)
    {
        var (store, _, clock) = Open(engine);
        Conversation(store);
        Assert.False(store.Start("c1", "other", "v9", "bob", "Bob"), "the same id twice changes nothing");
        clock.Advance(TimeSpan.FromMinutes(5));
        store.Start("c2", "build-helper", "v3", "bob", "Bob", origin: "mcp");
        store.SetStatus("c1", "limit-reached", "This conversation reached its limit of 5 tool calls.");

        var mine = Assert.Single(store.Conversations(person: "ada"));
        Assert.Equal(("build-helper", "v3", "limit-reached", "Add a measure"), (mine.Agent, mine.AgentVersion, mine.Status, mine.Title));
        Assert.Equal("This conversation reached its limit of 5 tool calls.", mine.StopReason);
        Assert.Equal(["c2", "c1"], store.Conversations(agent: "build-helper").Select(c => c.Id));
        Assert.Equal("mcp", store.Find("c2")!.Origin);
        Assert.Null(store.Find("nope"));
        Assert.Throws<ArgumentException>(() => store.SetStatus("c1", "weird"));
        Assert.Throws<ArgumentException>(() => store.Start("c3", "a", "v", "p", "P", origin: "carrier-pigeon"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Text_is_numbered_in_order_and_a_turn_record_keeps_tokens_cost_and_what_the_turn_was_given(string engine)
    {
        var (store, _, _) = Open(engine);
        Conversation(store);
        Assert.Equal(1, store.AppendText("c1", "user", "add a revenue measure"));
        Assert.Equal(2, store.AppendText("c1", "assistant", "done"));
        store.RecordTurn("c1", 1, "ada", "build-helper", "v3", "gpt-x", 1200, 300, 0.0123m, "abcd1234", "platform/platform-rules@platform-rules/1, agent/instructions@a1");

        Assert.Equal(["user", "assistant"], store.Text("c1").Select(t => t.Role));
        var turn = Assert.Single(store.Turns("c1"));
        Assert.Equal((1200L, 300L, 0.0123m, "gpt-x"), (turn.InputTokens, turn.OutputTokens, turn.Cost, turn.Model));
        Assert.Equal("platform/platform-rules@platform-rules/1, agent/instructions@a1", turn.ContextRecord);
        Assert.Equal(("ada", "build-helper", "v3"), (turn.Person, turn.Agent, turn.AgentVersion));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Usage_is_summed_per_person_agent_and_install(string engine)
    {
        var (store, _, _) = Open(engine);
        Conversation(store);
        store.RecordTurn("c1", 1, "ada", "build-helper", "v3", "m", 100, 10, 0.5m, null, null);
        store.RecordTurn("c1", 2, "ada", "build-helper", "v3", "m", 200, 20, 0.25m, null, null);
        store.RecordTurn("c9", 1, "bob", "sales-helper", "v1", "m", 50, 5, 1m, null, null);

        var byPerson = store.Usage("person").ToDictionary(u => u.Key);
        Assert.Equal((2, 300L, 30L, 0.75m), (byPerson["ada"].Turns, byPerson["ada"].InputTokens, byPerson["ada"].OutputTokens, byPerson["ada"].Cost));
        Assert.Equal(1m, byPerson["bob"].Cost);
        Assert.Equal(2, store.Usage("agent").Count);
        var install = Assert.Single(store.Usage("install"));
        Assert.Equal((3, 1.75m), (install.Turns, install.Cost));
        Assert.Throws<ArgumentException>(() => store.Usage("colour"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void Audit_rows_name_the_person_the_agent_and_its_version_and_filter(string engine)
    {
        var (store, _, clock) = Open(engine);
        store.RecordAudit("c1", 1, "tool-called", "ada", "build-helper", "v3", "console", "build_project_compile", "{\"args\":[\"x\"]}", null);
        clock.Advance(TimeSpan.FromSeconds(1));
        store.RecordAudit(null, null, "approval-asked", "bob", "claude-code 2.1", "2.1", "mcp", "build_model_add", "{}", "command model add  [writes-definitions]");

        var all = store.Audit();
        Assert.Equal(["approval-asked", "tool-called"], all.Select(a => a.Kind));
        Assert.All(all, a => { Assert.NotEmpty(a.Person); Assert.NotEmpty(a.Agent); Assert.NotEmpty(a.AgentVersion); });
        Assert.Equal("bob", Assert.Single(store.Audit(origin: "mcp")).Person);
        Assert.Equal("ada", Assert.Single(store.Audit(conversationId: "c1")).Person);
        Assert.Single(store.Audit(agent: "build-helper", kind: "tool-called"));
        Assert.Empty(store.Audit(person: "nobody"));
        Assert.Equal(1, store.Audit(conversationId: "c1")[0].Turn);
        Assert.Null(store.Audit(person: "bob")[0].Turn);
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_approval_is_answered_once_and_only_by_the_person_it_is_for(string engine)
    {
        var (store, _, clock) = Open(engine);
        var id = store.RequestApproval("c1", "ada", "build-helper", "v3", "console", "build_model_add", "command model add orders  [writes-definitions]", "{\"args\":[\"orders\"]}", "writes-definitions");
        Assert.Equal("pending", store.Approval(id)!.Status);
        Assert.Single(store.Approvals(pendingOnly: true, person: "ada"));
        Assert.Empty(store.Approvals(pendingOnly: true, person: "bob"));

        Assert.False(store.Answer(id, true, "bob"), "someone else's step is not bob's to approve");
        clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True(store.Answer(id, true, "ada", "looks right"));
        Assert.False(store.Answer(id, false, "ada"), "the first answer stands");
        var row = store.Approval(id)!;
        Assert.Equal(("approved", "ada", "looks right"), (row.Status, row.AnsweredBy, row.Note));
        Assert.Equal(Start.AddSeconds(30), row.AnsweredAt);
        Assert.Empty(store.Approvals(pendingOnly: true));
        Assert.Single(store.Approvals(pendingOnly: false));
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_administrator_may_answer_for_another_person_and_the_row_shows_who_did(string engine)
    {
        var (store, _, _) = Open(engine);
        var id = store.RequestApproval(null, "ada", "claude-code", "2.1", "mcp", "t", "p", null, "writes-data");
        Assert.True(store.Answer(id, false, "root", "not now", anyPerson: true));
        var row = store.Approval(id)!;
        Assert.Equal(("denied", "root"), (row.Status, row.AnsweredBy));
        Assert.Equal("ada", row.Person);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_step_nobody_answered_expires_and_an_expired_step_cannot_be_approved_later(string engine)
    {
        var (store, _, clock) = Open(engine);
        var old = store.RequestApproval(null, "ada", "a", "v", "mcp", "t", "p", null, "writes-data");
        clock.Advance(TimeSpan.FromHours(2));
        var fresh = store.RequestApproval(null, "ada", "a", "v", "mcp", "t", "p", null, "writes-data");

        Assert.Equal(1, store.ExpireApprovals(TimeSpan.FromHours(1)));
        Assert.Equal("expired", store.Approval(old)!.Status);
        Assert.False(store.Answer(old, true, "ada"));
        Assert.Equal("pending", store.Approval(fresh)!.Status);
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_text_older_than_thirty_days_goes_while_the_audit_rows_the_turn_records_and_the_conversation_stay(string engine)
    {
        var (store, database, clock) = Open(engine);
        Conversation(store);
        store.AppendText("c1", "user", "secret plans");
        store.RecordTurn("c1", 1, "ada", "build-helper", "v3", "m", 10, 1, 0.01m, "p", "agent/x@1");
        store.RecordAudit("c1", 1, "tool-called", "ada", "build-helper", "v3", "console", "build_x", "{}", null);
        var approval = store.RequestApproval("c1", "ada", "build-helper", "v3", "console", "build_x", "p", null, "writes-data");
        clock.Advance(TimeSpan.FromDays(31));

        var removed = new RetentionJob(database).Run();

        Assert.Equal(1, removed.Removed["agents.turn_text"]);
        Assert.Empty(store.Text("c1"));
        Assert.Single(store.Turns("c1"));
        Assert.Single(store.Audit(conversationId: "c1"));
        Assert.NotNull(store.Approval(approval));
        Assert.NotNull(store.Find("c1"));
        Assert.Equal(0, Count(database, "turn_text"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_database_written_by_a_newer_agents_module_is_refused_naming_the_module_and_both_versions(string engine)
    {
        var location = _engines.Provision(engine);
        var newer = new StateModule("agents", StateScopeKind.Install, [.. AgentsModule.Module.Migrations, "ALTER TABLE {table:approval} {{addcolumn}} unit {{key}} NULL;"], retention: AgentsModule.Module.Retention);
        location.Open([newer], appliedBy: "newer-agents 3.0");

        var ex = Assert.Throws<InvalidOperationException>(() => location.Open([AgentsModule.Module]));

        Assert.Contains("'agents", ex.Message);
        Assert.Contains("version 3", ex.Message);
        Assert.Contains("version 2", ex.Message);
        Assert.Contains("newer-agents 3.0", ex.Message);
    }

    [Theory, MemberData(nameof(Engines))]
    public void An_outside_agents_token_is_the_persons_shown_once_and_ends_when_it_expires_or_is_revoked(string engine)
    {
        var (store, database, clock) = Open(engine);

        var (row, secret) = store.CreateToken("ada", "ui mcp", TimeSpan.FromDays(10));
        Assert.StartsWith("ddka_" + row.Id + "_", secret);
        Assert.Equal("ada", store.VerifyToken(secret));
        Assert.NotNull(Assert.Single(store.Tokens("ada")).LastUsedAt);
        Assert.Empty(store.Tokens("grace"));
        Assert.Null(store.VerifyToken(secret + "x"));
        Assert.Null(store.VerifyToken("ddka_nonsense"));
        Assert.Null(store.VerifyToken("ddk_" + row.Id + "_" + secret));
        // only a hash is kept: the secret is in no row
        using (var connection = database.OpenConnection())
        {
            using var command = database.Command(AgentsModule.Name, connection, "SELECT secret_hash FROM {table:agent_token};");
            Assert.DoesNotContain(secret[(secret.LastIndexOf('_') + 1)..], Convert.ToString(command.ExecuteScalar()));
        }
        Assert.False(store.RevokeToken(row.Id, "grace"));      // another person's token is not theirs to revoke
        clock.Advance(TimeSpan.FromDays(11));
        Assert.Null(store.VerifyToken(secret));                 // expired
        var (second, secondSecret) = store.CreateToken("ada", "other");
        Assert.Equal("ada", store.VerifyToken(secondSecret));
        Assert.True(store.RevokeToken(second.Id, "ada"));
        Assert.False(store.RevokeToken(second.Id, "ada"));
        Assert.Null(store.VerifyToken(secondSecret));
        Assert.Throws<ArgumentException>(() => store.CreateToken("ada", " "));
        Assert.Throws<ArgumentException>(() => store.CreateToken("ada", "x", TimeSpan.FromDays(400)));
    }
}
