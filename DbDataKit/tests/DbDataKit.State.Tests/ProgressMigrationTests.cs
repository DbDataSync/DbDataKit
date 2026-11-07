namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q20 (plan 29 section 4.3): a task's progress follows its declaration across versions; the runner plans the change, refuses what the standard rules cannot do, lists what is lost, and applies the rest without code.</summary>
public sealed class ProgressMigrationTests : IDisposable
{
    private static readonly ProgressKey Key = new("pkg", "prod", "task");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private static DeclaredCheckpoint Checkpoint(string name, params (string, DeclaredPart)[] parts) => new(name, parts.ToDictionary(p => p.Item1, p => p.Item2));

    private static ProgressDeclaration Declare(params DeclaredCheckpoint[] checkpoints) => new(checkpoints, []);

    private static DeclaredPart Of(ProgressType type, string? @default = null, string? renamedFrom = null, string? format = null, bool reset = false) => new(type, @default, renamedFrom, format, reset);

    private static Dictionary<string, ProgressValue> Parts(params (string, ProgressValue)[] parts) => parts.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void Each_standard_change_is_planned_without_code_and_only_the_ones_that_lose_progress_are_listed_as_losses()
    {
        var old = Declare(
            Checkpoint("orders", ("at", Of(ProgressType.Int)), ("legacy", Of(ProgressType.Text)), ("seen", Of(ProgressType.Date)), ("name", Of(ProgressType.Text))),
            Checkpoint("gone", ("x", Of(ProgressType.Int))));
        var @new = Declare(Checkpoint("orders",
            ("at", Of(ProgressType.Decimal)),                                       // widened
            ("seen", Of(ProgressType.Time)),                                         // date to time
            ("label", Of(ProgressType.Text, renamedFrom: "name")),                   // renamed
            ("added", Of(ProgressType.Int, @default: "0"))));                         // added
        var plan = ProgressMigration.Plan(old, @new);

        Assert.Contains(plan.Steps, s => s is { Kind: MigrationKind.ConvertValue, Target: "orders.at" });
        Assert.Contains(plan.Steps, s => s is { Kind: MigrationKind.ConvertValue, Target: "orders.seen" });
        Assert.Contains(plan.Steps, s => s is { Kind: MigrationKind.RenameValue, Target: "orders.label" });
        Assert.Contains(plan.Steps, s => s is { Kind: MigrationKind.AddValue, Target: "orders.added" });
        Assert.Equal(["orders.legacy", "gone"], plan.Losses.Select(l => l.Target).ToArray());    // a part dropped and a checkpoint no longer declared: the only losses
        Assert.True(ProgressMigration.Plan(old, old).IsEmpty);
    }

    [Fact]
    public void A_type_change_the_rules_cannot_make_is_refused_naming_the_value_and_text_to_a_time_needs_a_format()
    {
        var old = Declare(Checkpoint("orders", ("at", Of(ProgressType.Time)), ("when", Of(ProgressType.Text))));

        var refused = Assert.Throws<ProgressMigrationException>(() => ProgressMigration.Plan(old, Declare(Checkpoint("orders", ("at", Of(ProgressType.Int)), ("when", Of(ProgressType.Text))))));
        Assert.Contains("orders.at", refused.Message);
        Assert.Contains("from Time to Int", refused.Message);

        var noFormat = Assert.Throws<ProgressMigrationException>(() => ProgressMigration.Plan(old, Declare(Checkpoint("orders", ("at", Of(ProgressType.Time)), ("when", Of(ProgressType.Time))))));
        Assert.Contains("orders.when", noFormat.Message);
        Assert.Contains("format", noFormat.Message);

        var withFormat = ProgressMigration.Plan(old, Declare(Checkpoint("orders", ("at", Of(ProgressType.Time)), ("when", Of(ProgressType.Date, format: "yyyy-MM-dd")))));
        Assert.Single(withFormat.Steps);
        Assert.Equal(MigrationKind.ConvertValue, withFormat.Steps[0].Kind);
    }

    [Fact]
    public void A_key_part_added_is_filled_by_the_declared_rule_or_the_checkpoint_is_reset_and_that_is_a_loss()
    {
        var per1 = new DeclaredCheckpoint("orders", new Dictionary<string, DeclaredPart> { ["at"] = Of(ProgressType.Int) }, ["customer"]);
        var per2 = per1 with { Per = ["customer", "region"] };

        var filled = ProgressMigration.Plan(Declare(per1), Declare(per2 with { KeyFill = "all-regions" }));
        var reset = ProgressMigration.Plan(Declare(per1), Declare(per2));
        var removed = ProgressMigration.Plan(Declare(per2), Declare(per1));

        Assert.Equal(MigrationKind.KeysAdded, Assert.Single(filled.Steps).Kind);
        Assert.Empty(filled.Losses);
        Assert.Single(reset.Losses);
        Assert.Single(removed.Losses);
        Assert.Equal(MigrationKind.KeysRemoved, removed.Steps[0].Kind);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void The_standard_changes_are_applied_to_what_is_stored_with_the_migration_in_the_history(string engine)
    {
        var database = _engines.Provision(engine).Open([ProgressModule.Module], StateScope.Standalone);
        var store = new ProgressStore(database, Key);
        store.Advance("orders", Parts(("at", ProgressValue.OfInt(42)), ("seen", ProgressValue.OfDate(new DateOnly(2026, 11, 4))), ("name", ProgressValue.OfText("crm")), ("when", ProgressValue.OfText("2026-10-31"))), 0);
        store.SetState("cursor", ProgressValue.OfInt(7));
        var old = new ProgressDeclaration(
            [Checkpoint("orders", ("at", Of(ProgressType.Int)), ("seen", Of(ProgressType.Date)), ("name", Of(ProgressType.Text)), ("when", Of(ProgressType.Text)))],
            [new DeclaredState("cursor", ProgressType.Int)]);
        var @new = new ProgressDeclaration(
            [Checkpoint("orders", ("at", Of(ProgressType.Decimal)), ("seen", Of(ProgressType.Time)), ("label", Of(ProgressType.Text, renamedFrom: "name")), ("when", Of(ProgressType.Date, format: "yyyy-MM-dd")), ("added", Of(ProgressType.Int, @default: "5")))],
            [new DeclaredState("cursor", ProgressType.Text)]);

        ProgressMigration.Apply(store, old, @new, actor: "migration:2.0");

        var after = store.GetCheckpoint("orders")!;
        Assert.Equal(42m, after.Parts["at"].AsDecimal);
        Assert.Equal(DateTimeOffset.Parse("2026-11-04T00:00:00Z"), after.Parts["seen"].AsTime);
        Assert.Equal("crm", after.Parts["label"].AsText);
        Assert.Equal(new DateOnly(2026, 10, 31), after.Parts["when"].AsDate);
        Assert.Equal(5L, after.Parts["added"].AsInt);
        Assert.DoesNotContain("name", after.Parts.Keys);
        Assert.Equal("7", store.GetState("cursor")!.AsText);
        Assert.Contains(store.History("orders"), c => c.Actor == "migration:2.0" && c.Part == "at");
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_stored_value_that_does_not_convert_stops_the_whole_change_before_anything_is_written_and_a_loss_needs_confirming(string engine)
    {
        var database = _engines.Provision(engine).Open([ProgressModule.Module], StateScope.Standalone);
        var store = new ProgressStore(database, Key);
        store.Advance("orders", Parts(("at", ProgressValue.OfText("17")), ("when", ProgressValue.OfText("not a date"))), 0);
        store.Advance("orders", Parts(("at", ProgressValue.OfText("18")), ("when", ProgressValue.OfText("2026-01-01"))), 1, itemKey: "b");
        var old = Declare(Checkpoint("orders", ("at", Of(ProgressType.Text)), ("when", Of(ProgressType.Text))));
        var @new = Declare(Checkpoint("orders", ("at", Of(ProgressType.Int)), ("when", Of(ProgressType.Date, format: "yyyy-MM-dd"))));

        var refused = Assert.Throws<ProgressMigrationException>(() => ProgressMigration.Apply(store, old, @new, "migration"));
        Assert.Contains("not a date", refused.Message);
        Assert.Contains("orders.when", refused.Message);
        Assert.Equal("17", store.GetCheckpoint("orders")!.Parts["at"].AsText);                 // nothing changed, "at" included
        Assert.Equal(ProgressType.Text, store.GetCheckpoint("orders")!.Parts["at"].Type);

        // a change that drops a part needs the administrator's word
        var dropping = Declare(Checkpoint("orders", ("at", Of(ProgressType.Text))));
        var unconfirmed = Assert.Throws<ProgressMigrationException>(() => ProgressMigration.Apply(store, old, dropping, "migration"));
        Assert.Contains("loses progress", unconfirmed.Message);
        Assert.Contains("orders.when", unconfirmed.Message);
        Assert.Equal(2, store.GetCheckpoint("orders")!.Parts.Count);
        ProgressMigration.Apply(store, old, dropping, "migration", confirmLosses: true);
        Assert.Single(store.GetCheckpoint("orders")!.Parts);
        // what was dropped is still in the history, so a rollback can restore it
        Assert.Contains(store.History("orders"), c => c.Part == "when" && c.Old == "not a date" && c.New is null && c.Actor == "migration");
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Existing_keys_are_filled_for_a_key_part_the_declaration_adds_or_reset_without_a_rule(string engine)
    {
        var database = _engines.Provision(engine).Open([ProgressModule.Module], StateScope.Standalone);
        var store = new ProgressStore(database, Key);
        store.Advance("orders", Parts(("at", ProgressValue.OfInt(1))), 0, itemKey: "acme");
        store.Advance("orders", Parts(("at", ProgressValue.OfInt(2))), 0, itemKey: "globex");
        var per1 = new DeclaredCheckpoint("orders", new Dictionary<string, DeclaredPart> { ["at"] = Of(ProgressType.Int) }, ["customer"]);
        var per2 = per1 with { Per = ["customer", "region"], KeyFill = "all-regions" };

        ProgressMigration.Apply(store, Declare(per1), Declare(per2), "migration");

        Assert.Equal(["acme|all-regions", "globex|all-regions"], store.ItemKeys("orders").ToArray());
        Assert.Equal(1L, store.GetCheckpoint("orders", "acme|all-regions")!.Parts["at"].AsInt);

        ProgressMigration.Apply(store, Declare(per2), Declare(per2 with { Per = ["customer", "region", "tier"], KeyFill = null }), "migration", confirmLosses: true);
        Assert.Empty(store.ItemKeys("orders"));      // no fill rule: reset
    }
}
