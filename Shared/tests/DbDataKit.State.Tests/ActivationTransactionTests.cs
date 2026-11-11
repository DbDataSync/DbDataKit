namespace DbDataKit.State.Tests;

/// <summary>
/// Plan 28a R18 on the state layer, on every engine: the pointer of a definition, the progress migration and the projection move in ONE transaction; a failure moves nothing; two activations at once migrate once; a rollback
/// puts back what the migration changed, from the history it kept (checkpoint parts with their types, and state values).
/// </summary>
public sealed class ActivationTransactionTests : IDisposable
{
    private const string Kind = "integration";
    private const string Name = "nightly";
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-06T12:00:00Z");

    private readonly StateEngines _engines = new();

    public void Dispose() => _engines.Dispose();

    private static DeclaredPart Of(ProgressType type) => new(type);

    private static ProgressDeclaration Old() => new(
        [new DeclaredCheckpoint("orders", new Dictionary<string, DeclaredPart> { ["at"] = Of(ProgressType.Int), ["legacy"] = Of(ProgressType.Text), ["seen"] = Of(ProgressType.Date) })],
        [new DeclaredState("cursor", ProgressType.Int), new DeclaredState("note", ProgressType.Text)]);

    // "legacy" is dropped, "at" widened to a decimal, "seen" widened to a time, the state "note" is no longer declared and "cursor" becomes text
    private static ProgressDeclaration New() => new(
        [new DeclaredCheckpoint("orders", new Dictionary<string, DeclaredPart> { ["at"] = Of(ProgressType.Decimal), ["seen"] = Of(ProgressType.Time) })],
        [new DeclaredState("cursor", ProgressType.Text)]);

    private (DefinitionPublisher Publisher, StateDatabase Database) Open(string engine)
    {
        var database = _engines.Provision(engine).Open([DefinitionsModule.Module, PayloadStore.Module, ProgressModule.Module, RunnerModule.Module], new StateScope(Group: "main"), time: new StateTestClock(Start));
        var publisher = new DefinitionPublisher(database, null);
        publisher.Publish(new PublishRequest(Kind, Name, "manifest one", "ada", "prod", PublishMode.Serve));
        publisher.Publish(new PublishRequest(Kind, Name, "manifest two", "ada", "prod", PublishMode.Stage));
        publisher.Status.Report("r1", Kind, Name, "prod", 2, InstanceStates.Ready);
        return (publisher, database);
    }

    private static ProgressStore Seed(StateDatabase database)
    {
        var store = new ProgressStore(database, new ProgressKey(Name, "prod", "orders"));
        store.Advance("orders", new Dictionary<string, ProgressValue> { ["at"] = ProgressValue.OfInt(42), ["legacy"] = ProgressValue.OfText("keep me"), ["seen"] = ProgressValue.OfDate(new DateOnly(2026, 11, 4)) }, 0);
        store.SetState("cursor", ProgressValue.OfInt(7));
        store.SetState("note", ProgressValue.OfText("hello"));
        return store;
    }

    private static string Tag(int from, int to) => $"migration:{Name}@{from}->{to}#{Guid.CreateVersion7():N}";

    private static void Migrate(StateDatabase database, DefinitionChange change, string actor) =>
        ProgressMigration.Apply(new ProgressStore(database, new ProgressKey(Name, "prod", "orders")).Within(change.Connection, change.Transaction), Old(), New(), actor, confirmLosses: true);

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_failure_in_the_work_that_goes_with_the_pointer_moves_nothing_and_without_it_pointer_and_progress_move_together(string engine)
    {
        var (publisher, database) = Open(engine);
        var store = Seed(database);

        var failure = Assert.Throws<InvalidOperationException>(() => publisher.ActivateIfAllReady(Kind, Name, ["r1"], "runner:r1", "prod", change =>
        {
            Migrate(database, change, Tag(1, 2));
            throw new InvalidOperationException("the projection could not be written");
        }));
        Assert.Contains("projection", failure.Message);
        Assert.Equal(1, publisher.Head(Kind, Name)!.Active);                        // the pointer did not move
        Assert.Equal(2, publisher.Head(Kind, Name)!.Staged);
        Assert.Equal(3, store.GetCheckpoint("orders")!.Parts.Count);                  // nor did the progress
        Assert.Equal(7L, store.GetState("cursor")!.AsInt);
        Assert.DoesNotContain(store.History("orders"), c => c.Actor?.StartsWith("migration:", StringComparison.Ordinal) == true);

        var result = publisher.ActivateIfAllReady(Kind, Name, ["r1"], "runner:r1", "prod", change => Migrate(database, change, Tag(1, 2)));

        Assert.True(result.Activated);
        Assert.Equal(2, publisher.Head(Kind, Name)!.Active);
        var after = store.GetCheckpoint("orders")!;
        Assert.Equal(42m, after.Parts["at"].AsDecimal);
        Assert.DoesNotContain("legacy", after.Parts.Keys);
        Assert.Equal("7", store.GetState("cursor")!.AsText);
        Assert.Null(store.GetState("note"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public async Task Two_activations_at_once_migrate_once_and_the_loser_sees_it_already_done(string engine)
    {
        var (publisher, database) = Open(engine);
        Seed(database);
        var migrations = 0;
        using var barrier = new Barrier(2);

        // the loser either finds the staged version gone (the winner committed before it looked) or loses the race for the pointer: neither migrates
        ActivationResult? Run(string who)
        {
            try
            {
                return publisher.ActivateIfAllReady(Kind, Name, ["r1"], who, "prod", change =>
                {
                    Interlocked.Increment(ref migrations);
                    Migrate(database, change, Tag(1, 2));
                });
            }
            catch (DefinitionNotFoundException)
            {
                return null;
            }
        }

        var tasks = new[] { "runner:a", "runner:b" }.Select(who => Task.Run(() => { barrier.SignalAndWait(TimeSpan.FromSeconds(10)); return Run(who); })).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.Equal(1, migrations);
        Assert.Single(results, r => r is { Activated: true });
        Assert.Single(results, r => r is null || r.AlreadyDone);
        Assert.Equal(2, publisher.Head(Kind, Name)!.Active);
        Assert.Single(publisher.EventsOf(Kind, Name), e => e.Action == "activate");
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_rollback_puts_the_progress_back_from_the_history_in_one_transaction_with_the_pointer(string engine)
    {
        var (publisher, database) = Open(engine);
        var store = Seed(database);
        var actor = Tag(1, 2);
        publisher.ActivateIfAllReady(Kind, Name, ["r1"], "runner:r1", "prod", change => Migrate(database, change, actor));
        Assert.DoesNotContain("legacy", store.GetCheckpoint("orders")!.Parts.Keys);

        // a rollback whose restore fails moves nothing
        Assert.Throws<InvalidOperationException>(() => publisher.Rollback(Kind, Name, "ada", "prod", inTransaction: change =>
        {
            ProgressMigration.Restore(new ProgressStore(database, new ProgressKey(Name, "prod", "orders")).Within(change.Connection, change.Transaction), $"migration:{Name}@1->2#", "rollback");
            throw new InvalidOperationException("nope");
        }));
        Assert.Equal(2, publisher.Head(Kind, Name)!.Active);
        Assert.DoesNotContain("legacy", store.GetCheckpoint("orders")!.Parts.Keys);
        Assert.Null(store.GetState("note"));

        var restored = 0;
        publisher.Rollback(Kind, Name, "ada", "prod", inTransaction: change =>
            restored = ProgressMigration.Restore(new ProgressStore(database, new ProgressKey(Name, "prod", "orders")).Within(change.Connection, change.Transaction), $"migration:{Name}@1->2#", "rollback"));

        Assert.Equal(1, publisher.Head(Kind, Name)!.Active);
        Assert.True(restored >= 3);
        var back = store.GetCheckpoint("orders")!;
        Assert.Equal(ProgressType.Int, back.Parts["at"].Type);                         // converted back to what it was, in its own type
        Assert.Equal(42L, back.Parts["at"].AsInt);
        Assert.Equal("keep me", back.Parts["legacy"].AsText);                           // the dropped value is back
        Assert.Equal(new DateOnly(2026, 11, 4), back.Parts["seen"].AsDate);
        Assert.Equal(7L, store.GetState("cursor")!.AsInt);
        Assert.Equal("hello", store.GetState("note")!.AsText);                          // a state value that was removed is back too
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_rewind_across_a_type_change_reads_the_old_value_in_its_old_type(string engine)
    {
        var (_, database) = Open(engine);
        var store = Seed(database);
        ProgressMigration.Apply(store, Old(), New(), "migration:x", confirmLosses: true);
        var first = store.History("orders").Where(c => c.Actor == "migration:x" && c.Part == "at").OrderBy(c => c.Id).ToList();
        Assert.Equal([ProgressType.Int, ProgressType.Decimal], first.Select(c => c.Type).ToArray());      // a removal in the old type, then an addition in the new

        Assert.True(store.Rewind("orders", first[0].Id, "ada"));

        Assert.Equal(42L, store.GetCheckpoint("orders")!.Parts["at"].AsInt);
    }

    [Fact]
    public void The_activation_hold_expires_with_its_lease_and_is_released_by_its_holder_only()
    {
        var clock = new StateTestClock(Start);
        var database = _engines.Provision(StateEngineIds.Sqlite).Open([DefinitionsModule.Module], new StateScope(Group: "main"), time: clock);
        var holds = new ActivationHolds(database);

        holds.Hold(Kind, Name, "prod", "runner:a", TimeSpan.FromSeconds(30));
        Assert.True(holds.IsHeld(Kind, Name, "prod"));
        Assert.False(holds.IsHeld(Kind, Name, "dev"));
        Assert.False(holds.Release(Kind, Name, "prod", "runner:b"));
        Assert.True(holds.IsHeld(Kind, Name, "prod"));
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.False(holds.IsHeld(Kind, Name, "prod"));                        // the holder died: the hold ran out

        holds.Hold(Kind, Name, "prod", "runner:a", TimeSpan.FromSeconds(30));
        Assert.True(holds.Release(Kind, Name, "prod", "runner:a"));
        Assert.False(holds.IsHeld(Kind, Name, "prod"));
    }

    [Fact]
    public void The_definitions_and_progress_modules_still_follow_the_conventions()
    {
        Assert.Empty(StateConventions.Violations(DefinitionsModule.Module));
        Assert.Empty(StateConventions.Violations(ProgressModule.Module));
    }
}
