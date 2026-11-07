using DbDataKit.Monitoring;

namespace DbDataKit.State.Tests;

/// <summary>Plan 29a Q20: the progress store of a task, on every engine: checkpoints that move only with the work, typed values, history and rewind, the file ledger, idempotency keys, dead letters and the inbox.</summary>
public sealed class ProgressStoreTests : IDisposable
{
    private static readonly ProgressKey Key = new("orders-pkg", "prod", "sync-orders");
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-11-07T09:00:00Z");

    private readonly StateEngines _engines = new();
    private readonly StateTestClock _clock = new(Start);

    public void Dispose() => _engines.Dispose();

    private StateDatabase Open(string engine) => _engines.Provision(engine).Open([ProgressModule.Module, RunsModule.Module], StateScope.Standalone, time: _clock);

    private static Dictionary<string, ProgressValue> Parts(params (string, ProgressValue)[] parts) => parts.ToDictionary(p => p.Item1, p => p.Item2);

    [Fact]
    public void The_module_follows_the_conventions() => Assert.Empty(StateConventions.Violations(ProgressModule.Module));

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_checkpoint_keeps_each_part_as_the_type_it_was_given(string engine)
    {
        var store = new ProgressStore(Open(engine), Key);
        var payload = Guid.NewGuid();
        var time = DateTimeOffset.Parse("2026-11-04T09:15:00.1234567+02:00");
        var parts = Parts(
            ("modified", ProgressValue.OfTime(time)), ("last_id", ProgressValue.OfInt(1_042_377)), ("token", ProgressValue.OfText("délta-token/with=chars")),
            ("amount", ProgressValue.OfDecimal(123456789.0123456789m)), ("day", ProgressValue.OfDate(new DateOnly(2026, 11, 4))), ("done", ProgressValue.OfBool(true)),
            ("blob", ProgressValue.OfBytes(payload)), ("extra", ProgressValue.OfJson("{\"a\":[1,2]}")));

        Assert.Null(store.GetCheckpoint("orders"));
        Assert.True(store.Advance("orders", parts, expectedVersion: 0));

        var read = store.GetCheckpoint("orders")!;
        Assert.Equal(1, read.Version);
        Assert.Equal(time.UtcDateTime.Ticks / 10, read.Parts["modified"].AsTime.UtcDateTime.Ticks / 10);   // to the microsecond PostgreSQL keeps
        Assert.Equal(1_042_377L, read.Parts["last_id"].AsInt);
        Assert.Equal("délta-token/with=chars", read.Parts["token"].AsText);
        Assert.Equal(123456789.0123456789m, read.Parts["amount"].AsDecimal);
        Assert.Equal(new DateOnly(2026, 11, 4), read.Parts["day"].AsDate);
        Assert.True(read.Parts["done"].AsBool);
        Assert.Equal(payload, read.Parts["blob"].AsPayload);
        Assert.Contains("\"a\"", read.Parts["extra"].AsText);
        Assert.Equal(ProgressType.Decimal, read.Parts["amount"].Type);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Two_overlapping_runs_cannot_both_move_a_checkpoint_the_second_is_refused_and_reads_again(string engine)
    {
        var database = Open(engine);
        var first = new ProgressStore(database, Key);
        var second = new ProgressStore(database, Key);

        Assert.True(first.Advance("orders", Parts(("at", ProgressValue.OfInt(1))), 0));
        Assert.False(second.Advance("orders", Parts(("at", ProgressValue.OfInt(99))), 0));            // it read "nothing yet" and so lost
        Assert.Equal(1L, first.GetCheckpoint("orders")!.Parts["at"].AsInt);

        var again = second.GetCheckpoint("orders")!;
        Assert.True(second.Advance("orders", Parts(("at", ProgressValue.OfInt(2))), again.Version));
        Assert.False(first.Advance("orders", Parts(("at", ProgressValue.OfInt(3))), 1));              // stale: it still holds version 1
        Assert.Equal(2L, first.GetCheckpoint("orders")!.Parts["at"].AsInt);
        Assert.Equal(2, first.GetCheckpoint("orders")!.Version);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_checkpoint_moves_in_the_same_step_as_the_runs_outcome_and_a_failure_leaves_both_where_they_were(string engine)
    {
        var database = Open(engine);
        var store = new ProgressStore(database, Key);
        var runs = new RunStore(database, "runner");
        var run = runs.Start("sync-orders");

        Assert.True(store.Advance("orders", Parts(("at", ProgressValue.OfInt(10))), 0, run, alsoWrite: (connection, transaction) => runs.Finish(run, RunOutcome.Succeeded, rows: 10, connection: connection, transaction: transaction)));
        Assert.Equal(RunOutcome.Succeeded, runs.Get(run)!.Run.Outcome);
        Assert.Equal(10L, store.GetCheckpoint("orders")!.Parts["at"].AsInt);

        // the second run fails to record its outcome: the checkpoint must not have moved
        var second = runs.Start("sync-orders");
        Assert.Throws<InvalidOperationException>(() => store.Advance("orders", Parts(("at", ProgressValue.OfInt(20))), 1, second, alsoWrite: (_, _) => throw new InvalidOperationException("the outcome could not be written")));

        Assert.Equal(10L, store.GetCheckpoint("orders")!.Parts["at"].AsInt);
        Assert.Equal(1, store.GetCheckpoint("orders")!.Version);
        Assert.Equal(RunOutcome.Running, runs.Get(second)!.Run.Outcome);
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_checkpoint_has_a_history_and_is_rewound_as_a_new_version_that_can_itself_be_undone(string engine)
    {
        var store = new ProgressStore(Open(engine), Key);
        var run = Guid.NewGuid();
        store.Advance("orders", Parts(("at", ProgressValue.OfInt(1)), ("id", ProgressValue.OfText("a"))), 0, run);
        _clock.Advance(TimeSpan.FromDays(1));
        store.Advance("orders", Parts(("at", ProgressValue.OfInt(2)), ("id", ProgressValue.OfText("a"))), 1, run);   // only "at" changed
        _clock.Advance(TimeSpan.FromDays(1));
        store.Advance("orders", Parts(("at", ProgressValue.OfInt(3)), ("id", ProgressValue.OfText("b"))), 2, run);

        var history = store.History("orders");
        Assert.Equal(new[] { "at", "id", "at" }.Order(), history.Where(c => c.HeadVersion >= 2).Select(c => c.Part).Order());   // no row for what did not change
        Assert.Equal(3, history.Select(c => c.HeadVersion).Max());
        Assert.Equal(run.ToString("D"), history[0].RunId);

        // "reload everything since the second day": rewind to before the change that made version 2
        var second = history.First(c => c.HeadVersion == 2 && c.Part == "at");
        Assert.True(store.Rewind("orders", second.Id, actor: "ada"));

        var after = store.GetCheckpoint("orders")!;
        Assert.Equal(4, after.Version);
        Assert.Equal(1L, after.Parts["at"].AsInt);
        Assert.Equal("a", after.Parts["id"].AsText);
        Assert.All(store.History("orders").Where(c => c.HeadVersion == 4), c => Assert.Equal("ada", c.Actor));

        // and the rewind is itself a change that can be rewound
        var rewind = store.History("orders").First(c => c.HeadVersion == 4 && c.Part == "at");
        Assert.True(store.Rewind("orders", rewind.Id, actor: "ada"));
        Assert.Equal(3L, store.GetCheckpoint("orders")!.Parts["at"].AsInt);
        Assert.False(store.Rewind("orders", 999_999, "ada"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_checkpoint_is_per_channel_task_and_item_key_so_a_preview_never_moves_production(string engine)
    {
        var database = Open(engine);
        var production = new ProgressStore(database, Key);
        var preview = new ProgressStore(database, Key with { Channel = "preview" });
        var other = new ProgressStore(database, Key with { Task = "another" });

        production.Advance("orders", Parts(("at", ProgressValue.OfInt(100))), 0);
        production.Advance("orders", Parts(("at", ProgressValue.OfInt(7))), 0, itemKey: "customer-a");

        Assert.Null(preview.GetCheckpoint("orders"));
        Assert.Null(other.GetCheckpoint("orders"));
        Assert.True(preview.Advance("orders", Parts(("at", ProgressValue.OfInt(1))), 0));          // a preview starts from scratch
        Assert.Equal(100L, production.GetCheckpoint("orders")!.Parts["at"].AsInt);
        Assert.Equal(7L, production.GetCheckpoint("orders", "customer-a")!.Parts["at"].AsInt);
        Assert.Null(production.GetCheckpoint("orders", "customer-b"));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_state_value_is_typed_and_replaced_only_while_it_is_still_the_version_read(string engine)
    {
        var store = new ProgressStore(Open(engine), Key);

        Assert.Null(store.GetState("last_token"));
        Assert.True(store.SetState("last_token", ProgressValue.OfText("abc"), expectedVersion: 0));
        Assert.False(store.SetState("last_token", ProgressValue.OfText("zzz"), expectedVersion: 0));    // it already exists
        var (value, version) = store.ReadState("last_token")!.Value;
        Assert.Equal(("abc", 1L), (value.AsText, version));
        Assert.True(store.SetState("last_token", ProgressValue.OfDecimal(2.5m), expectedVersion: version));   // the type may change with the declaration
        Assert.Equal(2.5m, store.GetState("last_token")!.AsDecimal);
        Assert.False(store.SetState("last_token", ProgressValue.OfText("late"), expectedVersion: version));
        Assert.True(store.SetState("last_token", ProgressValue.OfBool(true)));                             // no version asked: last writer wins
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_renamed_or_re_sent_file_is_recognised_by_its_content_and_marked_when_processed(string engine)
    {
        var store = new ProgressStore(Open(engine), Key);
        var hash = new string('a', 64);

        Assert.Null(store.FindFile("sftp", hash));
        Assert.True(store.NoteFile("sftp", "/in/orders-1.csv", 2048, Start, hash));
        Assert.False(store.NoteFile("sftp", "/in/orders-1-resent.csv", 2048, Start.AddHours(1), hash));   // the same content under another name: seen
        Assert.True(store.NoteFile("sftp", "/in/orders-2.csv", 10, Start, new string('b', 64)));
        Assert.Equal("/in/orders-1.csv", store.FindFile("sftp", hash)!.Path);
        Assert.Null(store.FindFile("other-connection", hash));                                                // per connection
        Assert.False(store.FindFile("sftp", hash)!.Processed);

        var run = Guid.NewGuid();
        Assert.True(store.MarkFileProcessed("sftp", hash, run, "done"));
        var processed = store.FindFile("sftp", hash)!;
        Assert.True(processed.Processed);
        Assert.Equal(("done", run.ToString("D")), (processed.Outcome, processed.RunId));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void An_idempotency_key_lets_one_caller_make_the_write_and_a_retry_reads_its_result_until_the_key_expires(string engine)
    {
        var store = new ProgressStore(Open(engine), Key);

        Assert.True(store.TryBeginIdempotent("ticket:order-17", TimeSpan.FromHours(1)));
        Assert.False(store.TryBeginIdempotent("ticket:order-17", TimeSpan.FromHours(1)));      // a retry while the first is still going
        Assert.Null(store.IdempotentResult("ticket:order-17"));
        store.CompleteIdempotent("ticket:order-17", "TICKET-901");
        Assert.False(store.TryBeginIdempotent("ticket:order-17", TimeSpan.FromHours(1)));
        Assert.Equal("TICKET-901", store.IdempotentResult("ticket:order-17"));

        _clock.Advance(TimeSpan.FromHours(2));
        Assert.Null(store.IdempotentResult("ticket:order-17"));                                  // expired
        Assert.True(store.TryBeginIdempotent("ticket:order-17", TimeSpan.FromHours(1)));         // and free again
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void A_dead_letter_is_kept_counted_and_replayed_or_discarded_and_its_body_goes_with_it(string engine)
    {
        var database = Open(engine);
        var store = new ProgressStore(database, Key);
        var payloads = new PayloadStore(database, chunkSize: 1024);
        var letter = database.NewId();
        Guid body;
        using (var writer = payloads.OpenWrite(new PayloadOptions(OwnerModule: ProgressModule.Name, OwnerRow: letter.ToString("D"))))
        {
            body = writer.Id;
            writer.Write("{\"order\":17}"u8);
            writer.Complete();
        }

        store.AddDeadLetter("crm", "400 Bad Request: invalid phone", letter, body);
        Assert.True(store.RecordDeadLetterAttempt(letter, "400 Bad Request: invalid phone (again)"));
        var open = Assert.Single(store.DeadLetters());
        Assert.Equal((2, "open", body), (open.Attempts, open.Status, open.BodyPayload!.Value));
        Assert.Empty(new ProgressStore(database, Key with { Channel = "preview" }).DeadLetters());

        Assert.True(store.MarkDeadLetterReplayed(letter));
        Assert.False(store.DiscardDeadLetter(letter));                 // settled: only open ones change
        Assert.Empty(store.DeadLetters());
        Assert.Single(store.DeadLetters(DeadLetter.Replayed));

        // settled and old: the row goes with retention, and its payload as one whose owner is gone
        _clock.Advance(TimeSpan.FromDays(40));
        var first = new RetentionJob(database).Run();
        Assert.Equal(1, first.Removed["runner_progress.dead_letter"]);
        _clock.Advance(TimeSpan.FromHours(2));
        new RetentionJob(database).Run();
        Assert.Null(payloads.Get(body));
    }

    [Theory]
    [MemberData(nameof(StateEngines.Engines), MemberType = typeof(StateEngines))]
    public void Webhooks_wait_in_the_inbox_and_each_goes_to_exactly_one_run(string engine)
    {
        var database = Open(engine);
        var store = new ProgressStore(database, Key);
        var first = store.Receive("github", signatureOk: true);
        _clock.Advance(TimeSpan.FromSeconds(1));
        store.Receive("github", signatureOk: false);
        _clock.Advance(TimeSpan.FromSeconds(1));
        store.Receive("stripe", signatureOk: true);
        Assert.Equal(3, store.InboxWaiting());

        var runs = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToArray();
        var taken = runs.AsParallel().WithDegreeOfParallelism(8).Select(run => new ProgressStore(database, Key).TakeNext(run)).Where(i => i is not null).Select(i => i!.Id).ToList();

        Assert.Equal(3, taken.Count);
        Assert.Equal(3, taken.Distinct().Count());        // no webhook went to two runs
        Assert.Equal(0, store.InboxWaiting());
        Assert.Null(store.TakeNext(Guid.NewGuid()));
        Assert.Contains(first, taken);
    }
}
