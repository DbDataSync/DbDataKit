namespace DbDataKit.Access.Tests;

/// <summary>Plan 22a GP10: the resolver gives what applies to a caller and a fingerprint of it, and tells how old each rules table is.</summary>
public sealed class AccessResolverTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Ledger : IRulesTableLedger
    {
        public readonly Dictionary<string, RulesTableRecord> Rows = new();

        public RulesTableRecord Touch(string key, DateTimeOffset now) => Rows.TryGetValue(key, out var row) ? row : Rows[key] = new RulesTableRecord(key, now, null);

        public void Changed(string key, DateTimeOffset at, string product, string reason)
        {
            var row = Rows.TryGetValue(key, out var known) ? known : new RulesTableRecord(key, at, null);
            Rows[key] = row with { ChangedAt = row.ChangedAt is { } last && last > at ? last : at };
        }
    }

    private sealed class Subjects : IRulesSubjects
    {
        public readonly Dictionary<string, string[]> Rows = new() { ["region_access"] = ["East", "west", "ann@example.com"], ["closed_accounts"] = ["Auditors"] };
        public int Reads;
        public bool Broken;

        public IReadOnlyCollection<string> Names(string table, string column)
        {
            Reads++;
            if (Broken) throw new InvalidOperationException("no such table");
            return Rows[table];
        }
    }

    private sealed class Alerts : IAccessAlerts
    {
        public readonly List<RulesTableStatus> Raised = [];

        public void RulesTableStale(RulesTableStatus status) => Raised.Add(status);
    }

    private readonly Clock _clock = new(DateTimeOffset.Parse("2026-11-12T10:00:00Z"));
    private readonly Ledger _ledger = new();
    private readonly Subjects _subjects = new();
    private readonly Alerts _alerts = new();

    private (AccessResolver Resolver, AccessScope Scope) Make(string yaml = AccessSectionTests.Complete)
    {
        var section = AccessSectionTests.Read(yaml, out var diagnostics, Fakes.Target())!;
        Assert.Empty(diagnostics);
        return (new AccessResolver(_ledger, _alerts, _clock), new AccessScope("semantic-model/sales", section, t => "warehouse/" + t, _subjects));
    }

    private static AccessCaller Caller(string id, params string[] groups) => new(id, id + "@example.com", groups);

    [Fact]
    public void Names_match_ignoring_case_by_kind_and_are_spelled_as_the_table_spells_them()
    {
        var (resolver, scope) = Make();
        var decision = resolver.Resolve(Caller("u1", "EAST", "sales"), scope);
        var rule = decision.DatasetOf("sales")!.Groups[0].Rules.Single(r => r.Rule.Id == "by_region");
        Assert.Equal(["East"], rule.Names);
        Assert.Empty(decision.DatasetOf("sales")!.Groups[0].Rules.Single(r => r.Rule.Id == "not_closed").Names);

        // a rule of a person matches the id or the name, never a group
        var person = Make("""
            row:
              - dataset: sales
                groups:
                  - id: g
                    rules:
                      - { id: r, effect: include, table: region_access, subject: who, subject_kind: person, columns: { region: region_code } }
            """);
        Assert.Equal(["ann@example.com"], person.Resolver.Resolve(new AccessCaller("u2", "Ann@Example.com", ["west"]), person.Scope).DatasetOf("sales")!.Groups[0].Rules[0].Names);
        Assert.Empty(person.Resolver.Resolve(new AccessCaller("u2", "bob", ["ann@example.com"]), person.Scope).DatasetOf("sales")!.Groups[0].Rules[0].Names);
    }

    [Fact]
    public void Two_callers_with_the_same_effective_rules_share_a_fingerprint_and_different_ones_do_not()
    {
        var (resolver, scope) = Make();
        var one = resolver.Resolve(Caller("u1", "east"), scope).Fingerprint;
        var other = resolver.Resolve(Caller("u2", "EAST", "unrelated"), scope).Fingerprint;
        var west = resolver.Resolve(Caller("u3", "west"), scope).Fingerprint;
        var none = resolver.Resolve(Caller("u4", "nobody"), scope).Fingerprint;
        var none2 = resolver.Resolve(Caller("u5", "somebody-else"), scope).Fingerprint;
        Assert.Equal(one, other);
        Assert.NotEqual(one, west);
        Assert.NotEqual(one, none);
        Assert.Equal(none, none2);
    }

    [Fact]
    public void A_refresh_changes_every_fingerprint_and_the_subjects_are_read_once_per_generation()
    {
        var (resolver, scope) = Make();
        var before = resolver.Resolve(Caller("u1", "east"), scope).Fingerprint;
        resolver.Resolve(Caller("u2", "west"), scope);
        Assert.Equal(2, _subjects.Reads);   // the two tables, once, however many callers ask

        _clock.Now += TimeSpan.FromMinutes(5);
        _ledger.Changed("warehouse/region_access", _clock.Now, "build", "data.changed");
        _subjects.Rows["region_access"] = ["east", "north"];
        var after = resolver.Resolve(Caller("u1", "east"), scope);
        Assert.NotEqual(before, after.Fingerprint);
        Assert.Equal(["east"], after.DatasetOf("sales")!.Groups[0].Rules[0].Names);
        Assert.Equal(3, _subjects.Reads);   // only the table that was refreshed is read again
    }

    [Fact]
    public void A_table_is_fresh_then_stale_after_half_its_age_with_one_alert_then_expired_and_the_dataset_is_refused_to_who_has_rules()
    {
        var (resolver, scope) = Make();   // region_access has 30 minutes, closed_accounts 2 hours
        var east = Caller("u1", "east");
        Assert.Equal(RulesTableState.Fresh, resolver.Resolve(east, scope).Tables.Single(t => t.Table == "region_access").State);
        Assert.Empty(_alerts.Raised);

        _clock.Now += TimeSpan.FromMinutes(16);
        for (var i = 0; i < 5; i++) Assert.Equal(RulesTableState.Stale, resolver.Resolve(east, scope).Tables.Single(t => t.Table == "region_access").State);
        var alert = Assert.Single(_alerts.Raised);
        Assert.Equal("region_access", alert.Table);
        Assert.Null(resolver.Resolve(east, scope).AnyRefusal);   // the last good rules are served

        _clock.Now += TimeSpan.FromMinutes(15);
        var expired = resolver.Resolve(east, scope);
        Assert.Equal(RulesTableState.Expired, expired.Tables.Single(t => t.Table == "region_access").State);
        Assert.Contains("region_access", expired.DatasetOf("sales")!.Refusal);
        Assert.Contains("minutes ago", expired.DatasetOf("sales")!.Refusal);
        // a person the table has no row for is not refused: they see nothing anyway
        Assert.Null(resolver.Resolve(Caller("u9", "nobody"), scope).DatasetOf("sales")!.Refusal);
        Assert.Single(_alerts.Raised);

        // a refresh makes it fresh again, and a later stale age raises a new alert
        _ledger.Changed("warehouse/region_access", _clock.Now, "build", "x");
        Assert.Null(resolver.Resolve(east, scope).AnyRefusal);
        _clock.Now += TimeSpan.FromMinutes(20);
        resolver.Resolve(east, scope);
        Assert.Equal(2, _alerts.Raised.Count);
    }

    [Fact]
    public void A_table_never_refreshed_counts_from_the_day_it_was_first_served()
    {
        var (resolver, scope) = Make();
        resolver.Resolve(Caller("u1", "east"), scope);
        Assert.Equal(_clock.Now, _ledger.Rows["warehouse/region_access"].FirstSeen);
        _clock.Now += TimeSpan.FromHours(3);
        Assert.Equal(RulesTableState.Expired, resolver.Resolve(Caller("u1", "east"), scope).Tables.Single(t => t.Table == "closed_accounts").State);
    }

    [Fact]
    public void An_unreadable_rules_table_refuses_the_dataset()
    {
        var (resolver, scope) = Make();
        _subjects.Broken = true;
        var decision = resolver.Resolve(Caller("u1", "east"), scope);
        Assert.Contains("cannot be read", decision.DatasetOf("sales")!.Refusal);
    }

    [Fact]
    public void Column_rules_that_name_the_caller_are_listed_and_change_the_fingerprint()
    {
        var (resolver, scope) = Make();
        var support = resolver.Resolve(Caller("u1", "support"), scope);
        var plain = resolver.Resolve(Caller("u2", "nobody"), scope);
        Assert.Equal("email", Assert.Single(support.Columns).Column);
        Assert.Empty(plain.Columns);
        Assert.NotEqual(support.Fingerprint, plain.Fingerprint);
        Assert.Equal("ann@example.com", Caller("ann@example.com").Id);
        var ann = resolver.Resolve(new AccessCaller("ann@example.com", "Ann", []), scope);
        Assert.Equal("salary", Assert.Single(ann.Columns).Column);
    }

    [Fact]
    public void A_section_with_no_rule_gives_the_same_decision_to_everyone()
    {
        var (resolver, _) = Make();
        var empty = new AccessScope("semantic-model/x", new AccessSection(AccessSection.DefaultMaxAge, [], []), t => t, _subjects);
        Assert.Same(AccessDecision.None, resolver.Resolve(Caller("u1", "east"), empty));
    }
}
