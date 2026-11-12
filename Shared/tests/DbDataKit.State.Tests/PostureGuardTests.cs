namespace DbDataKit.State.Tests;

/// <summary>Plan 28a R14: a loosened default needs a recorded, confirmed, audited choice; a default needs none. On every engine.</summary>
public sealed class PostureGuardTests : IDisposable
{
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-07T10:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine) => _engines.Provision(engine).Open([RunnerModule.Module, AuditModule.Module], StateScope.Standalone, time: _clock);

    private static PostureGuard Guard(StateDatabase database) => new(database, "console");

    public static TheoryData<string> Engines => StateEngines.Engines;

    [Fact]
    public void Every_setting_has_a_name_a_default_and_a_one_sentence_effect()
    {
        Assert.Equal(7, PostureSetting.All.Count);
        Assert.Equal(PostureSetting.All.Count, PostureSetting.All.Select(s => s.Name).Distinct().Count());
        foreach (var setting in PostureSetting.All)
        {
            Assert.Matches("^[a-z_]+$", setting.Name);
            Assert.False(string.IsNullOrWhiteSpace(setting.Default));
            Assert.EndsWith(".", setting.Effect);
            Assert.Equal(setting, PostureSetting.Find(setting.Name));
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public void Each_loosened_default_without_a_choice_is_refused_with_a_message_that_names_the_effect(string engine)
    {
        var guard = Guard(Open(engine));

        foreach (var setting in PostureSetting.All)
        {
            var refused = Assert.Throws<PostureRefusedException>(() => guard.Require(setting, "orders"));
            Assert.Contains(setting.Name, refused.Message);
            Assert.Contains(setting.Effect, refused.Message);
            Assert.Contains("orders", refused.Message);
            Assert.Contains("administrator", refused.Message);
            Assert.Contains("audit", refused.Message);
            Assert.Equal(setting, refused.Setting);
            Assert.False(guard.IsAllowed(setting, "orders"));
        }
    }

    [Theory, MemberData(nameof(Engines))]
    public void With_a_confirmed_choice_it_passes_and_the_choice_is_recorded(string engine)
    {
        var guard = Guard(Open(engine));

        var choice = guard.Choose(PostureSetting.LongerLimits, "orders", "ada", "nightly load needs 6 GB", confirmed: true);

        guard.Require(PostureSetting.LongerLimits, "orders");
        Assert.Equal(("orders", "longer_limits", "ada", Start, "nightly load needs 6 GB"), (choice.Scope, choice.Setting, choice.ChosenBy, choice.ChosenAt, choice.Note));
        Assert.Throws<PostureRefusedException>(() => guard.Require(PostureSetting.RunInProcess, "orders")); // only what was chosen
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_choice_that_is_not_confirmed_is_refused_and_leaves_nothing_behind(string engine)
    {
        var database = Open(engine);
        var guard = Guard(database);

        var refused = Assert.Throws<PostureRefusedException>(() => guard.Choose(PostureSetting.PrivilegedAccount, "orders", "ada", null, confirmed: false));

        Assert.Contains(PostureSetting.PrivilegedAccount.Effect, refused.Message);
        Assert.Empty(guard.Choices.List());
        Assert.Empty(new AuditLog(database, "console").Read());
    }

    [Theory, MemberData(nameof(Engines))]
    public void A_package_scoped_choice_does_not_apply_to_another_package_and_a_star_choice_applies_to_all(string engine)
    {
        var guard = Guard(Open(engine));
        guard.Choose(PostureSetting.RunInProcess, "orders", "ada", null, confirmed: true);

        guard.Require(PostureSetting.RunInProcess, "orders");
        Assert.Throws<PostureRefusedException>(() => guard.Require(PostureSetting.RunInProcess, "billing"));

        guard.Choose(PostureSetting.PerPackageAccount, RunnerModule.AllPackages, "grace", "all of them", confirmed: true);
        guard.Require(PostureSetting.PerPackageAccount, "orders");
        guard.Require(PostureSetting.PerPackageAccount, "billing");
        guard.Require(PostureSetting.PerPackageAccount, "a-package-nobody-has-heard-of");
    }

    [Theory, MemberData(nameof(Engines))]
    public void Revoke_refuses_again_and_revoking_nothing_says_so(string engine)
    {
        var guard = Guard(Open(engine));
        guard.Choose(PostureSetting.RecompileOnInstance, "orders", "ada", null, confirmed: true);
        guard.Require(PostureSetting.RecompileOnInstance, "orders");

        Assert.True(guard.Revoke(PostureSetting.RecompileOnInstance, "orders", "grace"));
        Assert.Throws<PostureRefusedException>(() => guard.Require(PostureSetting.RecompileOnInstance, "orders"));
        Assert.False(guard.Revoke(PostureSetting.RecompileOnInstance, "orders", "grace"));
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_audit_names_who_and_when_for_a_choice_and_for_a_revoke(string engine)
    {
        var database = Open(engine);
        var guard = Guard(database);

        guard.Choose(PostureSetting.AcceptProductionOnConsoleRunner, RunnerModule.AllPackages, "ada", "small install", confirmed: true);
        _clock.Advance(TimeSpan.FromHours(2));
        guard.Revoke(PostureSetting.AcceptProductionOnConsoleRunner, RunnerModule.AllPackages, "grace");
        guard.Revoke(PostureSetting.AcceptProductionOnConsoleRunner, RunnerModule.AllPackages, "grace"); // nothing to revoke: nothing to audit

        var entries = new AuditLog(database, "console").Read(new AuditQuery(VerbPrefix: "runner.posture."));
        Assert.Equal(2, entries.Count);
        var revoke = entries[0];
        var choose = entries[1];
        Assert.Equal(("runner.posture.choose", "ada", "accept_production_on_console_runner", Start, "console"), (choose.Entry.Verb, choose.Entry.ActorId, choose.Entry.TargetId, choose.At, choose.ServiceId));
        Assert.Equal(("runner.posture.revoke", "grace", Start.AddHours(2)), (revoke.Entry.Verb, revoke.Entry.ActorId, revoke.At));
        Assert.Equal(AuditEvent.Ok, choose.Entry.Outcome);
        using var detail = System.Text.Json.JsonDocument.Parse(choose.Entry.Detail!); // PostgreSQL's jsonb reformats the text, so read it, not match it
        Assert.Equal("*", detail.RootElement.GetProperty("package").GetString());
        Assert.Equal("small install", detail.RootElement.GetProperty("note").GetString());
    }

    [Theory, MemberData(nameof(Engines))]
    public void Choosing_again_replaces_the_choice_and_both_are_in_the_audit(string engine)
    {
        var database = Open(engine);
        var guard = Guard(database);
        guard.Choose(PostureSetting.LongerLimits, "orders", "ada", null, confirmed: true);
        _clock.Advance(TimeSpan.FromDays(1));

        var again = guard.Choose(PostureSetting.LongerLimits, "orders", "grace", "renewed", confirmed: true);

        Assert.Equal("grace", again.ChosenBy);
        Assert.Single(guard.Choices.List("orders"));
        Assert.Equal(2, new AuditLog(database, "console").Read(new AuditQuery(Verb: PostureGuard.ChooseVerb)).Count);
    }

    [Theory, MemberData(nameof(Engines))]
    public void The_choice_and_its_audit_entry_stand_or_fall_together(string engine)
    {
        var database = Open(engine);
        var guard = Guard(database);
        // an audit entry that cannot be written (the actor kind is fixed, the verb is valid) cannot be provoked from outside, so drop the audit table's insert target instead
        using (var connection = database.OpenConnection())
        using (var drop = database.Command(AuditModule.Name, connection, "DROP TABLE {table:entry};"))
            drop.ExecuteNonQuery();

        Assert.ThrowsAny<Exception>(() => guard.Choose(PostureSetting.LongerLimits, "orders", "ada", null, confirmed: true));

        Assert.Empty(guard.Choices.List()); // rolled back with the audit write
    }
}
