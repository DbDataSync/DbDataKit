using DbDataKit.Connections;
using DbDataKit.Monitoring;
using DbDataKit.Runner;
using DbDataKit.Scheduling;

namespace DbDataKit.Runner.Abstractions.Tests;

/// <summary>Plan 28a R02: the task and run model and its pure mappings.</summary>
public sealed class RunModelTests
{
    private static IEnumerable<T> All<T>() where T : struct, Enum => Enum.GetValues<T>();

    // ---- every end state has exactly one outcome, and the stores can represent it

    /// <summary>Outcomes that are not the end of a task: still going, or the process of the runner itself went away (<c>RunStore.InterruptRunning</c>).</summary>
    private static readonly RunOutcome[] NotATaskEnd = [RunOutcome.Running, RunOutcome.Interrupted];

    /// <summary>Queue statuses that are not final: the item has not finished.</summary>
    private static readonly WorkItemStatus[] NotFinal = [WorkItemStatus.Pending, WorkItemStatus.Claimed, WorkItemStatus.Running];

    [Fact]
    public void Every_end_state_has_one_run_outcome_that_is_a_final_one()
    {
        var outcomes = All<TaskEndState>().Select(TaskEndMapping.OutcomeOf).ToList();
        Assert.Equal(outcomes.Count, outcomes.Distinct().Count());              // two states never share an outcome
        Assert.DoesNotContain(RunOutcome.Running, outcomes);                    // RunStore.Finish refuses Running
        Assert.DoesNotContain(RunOutcome.Interrupted, outcomes);                // only the runner's restart makes that one
    }

    [Fact]
    public void Every_run_outcome_is_either_the_outcome_of_a_task_end_or_named_as_not_one()
    {
        var reached = All<TaskEndState>().Select(TaskEndMapping.OutcomeOf).ToHashSet();
        foreach (var outcome in All<RunOutcome>())
            Assert.True(reached.Contains(outcome) ^ NotATaskEnd.Contains(outcome),
                $"RunOutcome.{outcome} is neither reached from a TaskEndState nor listed as not being a task's end: map it, or list it in NotATaskEnd.");
    }

    [Fact]
    public void Every_end_state_has_one_final_work_item_status_and_every_final_status_is_reached_or_named()
    {
        var statuses = All<TaskEndState>().Select(TaskEndMapping.WorkItemStatusOf).ToHashSet();
        Assert.DoesNotContain(statuses, s => NotFinal.Contains(s));
        foreach (var status in All<WorkItemStatus>())
            Assert.True(statuses.Contains(status) ^ NotFinal.Contains(status),
                $"WorkItemStatus.{status} is neither the result of a TaskEndState nor listed as not final.");
    }

    [Fact]
    public void An_undefined_end_state_is_refused_and_never_stored()
    {
        var bogus = (TaskEndState)99;
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskEndMapping.OutcomeOf(bogus));
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskEndMapping.WorkItemStatusOf(bogus));
        Assert.NotEmpty(new TaskEnd(bogus).Problems());
    }

    [Fact]
    public void The_mappings_are_the_ones_the_plan_names()
    {
        Assert.Equal(RunOutcome.Succeeded, TaskEndMapping.OutcomeOf(TaskEndState.Succeeded));
        Assert.Equal(RunOutcome.CompletedWithFailures, TaskEndMapping.OutcomeOf(TaskEndState.CompletedWithFailures));   // QR4
        Assert.Equal(RunOutcome.Failed, TaskEndMapping.OutcomeOf(TaskEndState.Failed));
        Assert.Equal(RunOutcome.Stopped, TaskEndMapping.OutcomeOf(TaskEndState.Stopped));
        Assert.Equal(WorkItemStatus.Done, TaskEndMapping.WorkItemStatusOf(TaskEndState.Succeeded));
        Assert.Equal(WorkItemStatus.Done, TaskEndMapping.WorkItemStatusOf(TaskEndState.CompletedWithFailures));
        Assert.Equal(WorkItemStatus.Failed, TaskEndMapping.WorkItemStatusOf(TaskEndState.Failed));
        Assert.Equal(WorkItemStatus.Cancelled, TaskEndMapping.WorkItemStatusOf(TaskEndState.Stopped));
    }

    [Fact]
    public void An_end_becomes_the_arguments_of_RunStore_Finish()
    {
        var failure = new TaskFailure(TaskFailureKind.Permanent, "bad row");
        var finish = new TaskEnd(TaskEndState.CompletedWithFailures, Processed: 1200, FailedItems: 3, FirstFailedItem: "dbo.Orders", Failure: failure).ToRunFinish();
        Assert.Equal(RunOutcome.CompletedWithFailures, finish.Outcome);
        Assert.Equal(1200L, finish.Rows);
        Assert.Equal("dbo.Orders", finish.FailedItem);
        Assert.Equal(new RunFailure("Permanent", "bad row"), finish.Failure);

        var plain = new TaskEnd(TaskEndState.Succeeded, 5).ToRunFinish();
        Assert.Equal(new RunFinish(RunOutcome.Succeeded, 5, null, null), plain);

        // a failure's own item stands in when no first failed item was given
        Assert.Equal("t1", new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, "x", "t1")).ToRunFinish().FailedItem);
        Assert.Equal(RunOutcome.Succeeded, new TaskEnd(TaskEndState.Succeeded).ToRunOutcome());
        Assert.Equal(WorkItemStatus.Cancelled, TaskEndMapping.Cancelled().ToWorkItemStatus());
    }

    [Fact]
    public void An_inconsistent_end_message_is_named()
    {
        Assert.Empty(new TaskEnd(TaskEndState.Succeeded, 3).Problems());
        Assert.NotEmpty(new TaskEnd(TaskEndState.Succeeded, 3, FailedItems: 1).Problems());
        Assert.NotEmpty(new TaskEnd(TaskEndState.Succeeded, Failure: new TaskFailure(TaskFailureKind.Permanent, "x")).Problems());
        Assert.NotEmpty(new TaskEnd(TaskEndState.CompletedWithFailures, 3).Problems());
        Assert.NotEmpty(new TaskEnd(TaskEndState.Failed).Problems());
        Assert.NotEmpty(new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Cancelled, "x")).Problems());
        Assert.NotEmpty(new TaskEnd(TaskEndState.Succeeded, -1).Problems());
        Assert.NotEmpty(new TaskEnd(TaskEndState.Succeeded, Outputs: new Dictionary<string, TaskOutput> { ["a"] = new(null, null) }).Problems());
        Assert.Empty(new TaskEnd(TaskEndState.Succeeded, Outputs: new Dictionary<string, TaskOutput> { ["a"] = TaskOutput.Payload("p1"), ["b"] = TaskOutput.FromText("ok") }).Problems());
    }

    // ---- process-level results

    [Fact]
    public void A_process_that_gave_no_end_message_becomes_a_classified_end()
    {
        Assert.Equal(TaskFailureKind.ProcessExited, TaskEndMapping.ProcessExited(137).Failure!.Kind);
        Assert.Contains("137", TaskEndMapping.ProcessExited(137).Failure!.Message);
        Assert.Equal(TaskFailureKind.ProtocolViolation, TaskEndMapping.ProcessExited(0).Failure!.Kind);   // exit 0 without an end is still a violation
        Assert.Equal(TaskFailureKind.TimedOut, TaskEndMapping.TimedOut(TimeSpan.FromMinutes(30)).Failure!.Kind);
        Assert.Equal(TaskFailureKind.LimitExceeded, TaskEndMapping.LimitExceeded("memory").Failure!.Kind);
        Assert.Equal(TaskFailureKind.ProtocolViolation, TaskEndMapping.ProtocolViolation("two ends").Failure!.Kind);
        foreach (var end in new[] { TaskEndMapping.ProcessExited(1), TaskEndMapping.ProcessExited(0), TaskEndMapping.TimedOut(TimeSpan.FromSeconds(1)), TaskEndMapping.LimitExceeded("memory"), TaskEndMapping.ProtocolViolation("x"), TaskEndMapping.Cancelled() })
            Assert.Empty(end.Problems());
        Assert.Equal(TaskEndState.Failed, TaskEndMapping.TimedOut(TimeSpan.FromSeconds(1)).State);
        Assert.Equal(TaskEndState.Failed, TaskEndMapping.LimitExceeded("memory").State);
    }

    [Fact]
    public void A_cancel_is_stopped_and_never_retried()
    {
        var end = TaskEndMapping.Cancelled("an operator stopped it", processed: 10);
        Assert.Equal(TaskEndState.Stopped, end.State);
        Assert.Equal(RunOutcome.Stopped, end.ToRunOutcome());
        Assert.Equal(TaskFailureKind.Cancelled, end.Failure!.Kind);
        Assert.Equal(10, end.Processed);
        Assert.False(TaskEndMapping.ShouldRetry(end, 1, 5));
    }

    // ---- retries (DR8)

    [Fact]
    public void Every_failure_kind_says_whether_it_is_retryable_and_only_the_expected_ones_are()
    {
        var retryable = All<TaskFailureKind>().Where(TaskFailure.IsRetryable).ToHashSet();
        Assert.Equal([TaskFailureKind.Temporary, TaskFailureKind.TimedOut, TaskFailureKind.ProcessExited], retryable.OrderBy(k => k));
        foreach (var kind in All<TaskFailureKind>())
            Assert.Equal(retryable.Contains(kind), new TaskFailure(kind, "m").Retryable);
    }

    [Fact]
    public void A_retry_applies_to_a_retryable_failure_while_attempts_remain()
    {
        var temporary = new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Temporary, "down"));
        Assert.True(TaskEndMapping.ShouldRetry(temporary, 1, 3));
        Assert.True(TaskEndMapping.ShouldRetry(temporary, 2, 3));
        Assert.False(TaskEndMapping.ShouldRetry(temporary, 3, 3));       // attempts counts the first run
        Assert.False(TaskEndMapping.ShouldRetry(temporary, 1, null));    // no attempts declared: one run
        Assert.False(TaskEndMapping.ShouldRetry(temporary, 1, 1));
        Assert.True(TaskEndMapping.ShouldRetry(TaskEndMapping.ProcessExited(1), 1, 2));
        Assert.True(TaskEndMapping.ShouldRetry(TaskEndMapping.TimedOut(TimeSpan.FromSeconds(1)), 1, 2));
        Assert.False(TaskEndMapping.ShouldRetry(new TaskEnd(TaskEndState.Failed, Failure: new TaskFailure(TaskFailureKind.Permanent, "x")), 1, 5));
        Assert.False(TaskEndMapping.ShouldRetry(TaskEndMapping.LimitExceeded("memory"), 1, 5));
        Assert.False(TaskEndMapping.ShouldRetry(TaskEndMapping.ProtocolViolation("x"), 1, 5));
        Assert.False(TaskEndMapping.ShouldRetry(new TaskEnd(TaskEndState.Succeeded), 1, 5));
        Assert.False(TaskEndMapping.ShouldRetry(new TaskEnd(TaskEndState.CompletedWithFailures, 5, 1), 1, 5));
    }

    // ---- ConnectionFailure

    [Fact]
    public void Every_connection_failure_kind_maps_to_temporary_or_permanent_as_stated()
    {
        foreach (var kind in All<ConnectionFailureKind>())
        {
            var mapped = TaskFailure.KindOf(kind);
            var expected = kind is ConnectionFailureKind.Network or ConnectionFailureKind.Timeout ? TaskFailureKind.Temporary : TaskFailureKind.Permanent;
            Assert.Equal(expected, mapped);
        }
    }

    [Fact]
    public void A_connection_failure_keeps_its_public_text_its_code_and_its_item_and_never_the_raw_text()
    {
        var connection = new ConnectionFailure(ConnectionFailureKind.Network, "socket-10061", null, raw: "Password=hunter2 refused");
        var failure = TaskFailure.From(connection, "dbo.Orders");
        Assert.Equal(TaskFailureKind.Temporary, failure.Kind);
        Assert.True(failure.Retryable);
        Assert.Equal(connection.PublicMessage, failure.Message);
        Assert.DoesNotContain("hunter2", failure.Message);
        Assert.Equal("dbo.Orders", failure.Item);
        Assert.Equal("Network", failure.Code);
        Assert.Equal("Temporary:Network", failure.ToRunFailure().Kind);

        var login = TaskFailure.From(new ConnectionFailure(ConnectionFailureKind.Login, "28000", null));
        Assert.Equal(TaskFailureKind.Permanent, login.Kind);
        Assert.False(login.Retryable);
        Assert.Equal("Permanent:Login", login.ToRunFailure().Kind);
        Assert.Equal("Cancelled", new TaskFailure(TaskFailureKind.Cancelled, "m").ToRunFailure().Kind);
    }

    // ---- TaskRef

    [Theory]
    [InlineData("orders-sync", "stable", "extract")]
    [InlineData("a", "b", "c")]
    [InlineData("0pkg_x", "beta-2", "load_orders")]
    public void A_task_reference_round_trips_through_its_string_form(string package, string channel, string task)
    {
        var reference = new TaskRef(package, channel, task);
        Assert.Equal($"{package}/{channel}/{task}", reference.ToString());
        Assert.Equal(reference, TaskRef.Parse(reference.ToString()));
        Assert.True(TaskRef.TryParse(reference.ToString(), out var parsed));
        Assert.Equal(reference, parsed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a/b")]
    [InlineData("a/b/c/d")]
    [InlineData("A/b/c")]
    [InlineData("a//c")]
    [InlineData("a/b/ c")]
    [InlineData("-a/b/c")]
    [InlineData("a/b/c.d")]
    public void A_bad_task_reference_is_refused_and_says_the_form(string text)
    {
        Assert.False(TaskRef.TryParse(text, out var result));
        Assert.Null(result);
        Assert.Contains("package/channel/task", Assert.Throws<FormatException>(() => TaskRef.Parse(text)).Message);
    }

    [Fact]
    public void A_task_reference_cannot_be_built_from_a_bad_name_and_uses_the_manifests_rule()
    {
        Assert.Throws<ArgumentException>(() => new TaskRef("Orders", "stable", "extract"));
        Assert.Throws<ArgumentException>(() => new TaskRef("orders", "st/able", "extract"));
        Assert.Throws<ArgumentException>(() => new TaskRef("orders", "stable", new string('a', 65)));
        Assert.Null(TaskRef.Problem(new string('a', 64), "x", "y"));
        Assert.Contains("channel", TaskRef.Problem("a", "B", "c"));
        Assert.Throws<ArgumentException>(() => new TaskRef("a", "b", null!));
        // the same names the manifest validator accepts
        foreach (var name in new[] { "a", "0", "a_b-c", "orders-sync" }) Assert.Null(TaskRef.Problem(name, name, name));
    }

    // ---- TaskStart

    private static TaskStart Start(Action<Dictionary<string, string>>? handles = null, int attempt = 1)
    {
        var h = new Dictionary<string, string> { ["source"] = "orders-db" };
        handles?.Invoke(h);
        return new TaskStart(new TaskRef("orders-sync", "stable", "extract"), Guid.NewGuid(), attempt, "1.4.0",
            new Dictionary<string, string> { ["since"] = "2024-01-01" }, h, "snapshot-1", TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void A_start_message_carries_what_the_task_needs_and_defaults_to_protocol_v1()
    {
        var start = Start();
        Assert.Empty(start.Problems());
        Assert.Equal("dbdatakit.runner.v1", start.ProtocolVersion);
        Assert.Equal("orders-db", start.ConnectionHandles["source"]);
    }

    [Fact]
    public void A_start_message_with_a_secret_shaped_handle_or_a_bad_attempt_is_refused()
    {
        Assert.NotEmpty(Start(h => h["x"] = "Server=db;Password=secret").Problems());
        Assert.NotEmpty(Start(h => h["x"] = "user@host").Problems());
        Assert.NotEmpty(Start(h => h["x"] = "").Problems());
        Assert.NotEmpty(Start(attempt: 0).Problems());
        Assert.NotEmpty((Start() with { RunId = Guid.Empty }).Problems());
        Assert.NotEmpty((Start() with { CancellationGrace = TimeSpan.FromSeconds(-1) }).Problems());
        Assert.NotEmpty((Start() with { PackageVersion = " " }).Problems());
    }
}
