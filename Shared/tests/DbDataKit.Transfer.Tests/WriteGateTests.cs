using DbDataKit.Dialect;
using DbDataKit.Secrets;
using Xunit;

namespace DbDataKit.Transfer.Tests;

internal sealed class FakeExecutor : IWriteExecutor
{
    public List<string> Calls { get; } = [];
    public Func<SqlStatement, long> Rows { get; set; } = _ => 1;
    public Func<SqlStatement, object?> Scalar { get; set; } = _ => 0L;
    public Func<SqlStatement, Exception?> Fail { get; set; } = _ => null;
    public bool Locked { get; set; } = true;

    public Task BeginAsync(CancellationToken ct) { Calls.Add("begin"); return Task.CompletedTask; }
    public Task CommitAsync(CancellationToken ct) { Calls.Add("commit"); return Task.CompletedTask; }
    public Task RollbackAsync(CancellationToken ct) { Calls.Add("rollback"); return Task.CompletedTask; }
    public Task<long> ExecuteAsync(SqlStatement s, CancellationToken ct)
    {
        Calls.Add("exec:" + s.Id);
        if (Fail(s) is { } ex) throw ex;
        return Task.FromResult(Rows(s));
    }
    public Task<object?> ScalarAsync(SqlStatement s, CancellationToken ct) { Calls.Add("scalar:" + s.Id); return Task.FromResult(Scalar(s)); }
    public Task<bool> TryLockAsync(string r, bool shared, CancellationToken ct) { Calls.Add("lock:" + r); return Task.FromResult(Locked); }
    public Task UnlockAsync(string r, bool shared, CancellationToken ct) { Calls.Add("unlock:" + r); return Task.CompletedTask; }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class ThrowingLog : IStatementLog
{
    public void Append(StatementLogEntry entry) => throw new IOException("disk full");
}

public class WriteGateTests
{
    private static SqlStatement Stmt(WriteKind kind, string id = "s1", object? value = null) => new(id, "INSERT INTO t VALUES (@p)",
        [new SqlParameter("@p", "p", CanonicalType.String(), value ?? "hello")], new WriteIntent(id, kind, "t"));

    private static (WriteGate Gate, FakeExecutor Exec, MemoryStatementLog Log) Make(Func<WriteGateOptions, WriteGateOptions>? o = null, FakeExecutor? exec = null)
    {
        exec ??= new FakeExecutor();
        var log = new MemoryStatementLog();
        var options = new WriteGateOptions("apply", log);
        if (o is not null) options = o(options);
        return (new WriteGate(exec, options), exec, log);
    }

    [Fact]
    public async Task A_permitted_statement_runs_and_is_logged_before_and_after()
    {
        var (gate, exec, log) = Make();
        var rows = await gate.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken);
        Assert.Equal(1, rows);
        Assert.Equal(["exec:s1"], exec.Calls);
        Assert.Equal(["begin", "end"], log.Entries.Select(e => e.Phase));
        Assert.Equal([1, 1], log.Entries.Select(e => e.Ordinal));
        Assert.Equal("ok rows=1", log.Entries[1].Outcome);
    }

    [Fact]
    public async Task Ordinals_increase_per_statement()
    {
        var (gate, _, log) = Make();
        await gate.ExecuteAsync(Stmt(WriteKind.Insert, "a"), TestContext.Current.CancellationToken);
        await gate.ExecuteAsync(Stmt(WriteKind.Insert, "b"), TestContext.Current.CancellationToken);
        Assert.Equal([1, 1, 2, 2], log.Entries.Select(e => e.Ordinal));
    }

    [Fact]
    public async Task Readonly_access_refuses_every_write_and_executes_nothing()
    {
        var (gate, exec, log) = Make(o => o with { Access = AccessMode.ReadOnly });
        var ex = await Assert.ThrowsAsync<GateRefusedException>(() => gate.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken));
        Assert.Contains("read-only", ex.Message);
        Assert.Empty(exec.Calls);
        Assert.Empty(log.Entries);
    }

    [Fact]
    public async Task Readonly_access_still_allows_reads_but_not_writes_disguised_as_scalars()
    {
        var (gate, exec, _) = Make(o => o with { Access = AccessMode.ReadOnly });
        var read = new SqlStatement("r", "SELECT 1", [], new WriteIntent("r", WriteKind.None, "t"), StatementRole.Scalar);
        Assert.Equal(0L, await gate.ScalarAsync(read, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<GateRefusedException>(() => gate.ScalarAsync(Stmt(WriteKind.Delete), TestContext.Current.CancellationToken));
        Assert.Equal(["scalar:r"], exec.Calls);
    }

    [Fact]
    public async Task Statements_outside_the_permitted_kinds_are_refused()
    {
        var (gate, exec, _) = Make(o => o with { Permitted = WriteKind.Insert | WriteKind.Bulk });
        await gate.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<GateRefusedException>(() => gate.ExecuteAsync(Stmt(WriteKind.Delete, "d"), TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<GateRefusedException>(() => gate.ExecuteAsync(Stmt(WriteKind.Insert | WriteKind.Truncate, "t"), TestContext.Current.CancellationToken));
        Assert.Equal(["exec:s1"], exec.Calls);
    }

    [Fact]
    public async Task Default_log_has_hashes_and_types_but_no_text_and_no_values()
    {
        var (gate, _, log) = Make();
        await gate.ExecuteAsync(Stmt(WriteKind.Insert, value: "TOP-SECRET-VALUE"), TestContext.Current.CancellationToken);
        var begin = log.Entries[0];
        Assert.Null(begin.Text);
        Assert.Equal(64, begin.StatementHash.Length);
        var p = Assert.Single(begin.Parameters!);
        Assert.Equal("p", p.Name);
        Assert.Equal("string(unicode)", p.Type);
        Assert.Equal(12, p.ValueHash.Length);
        Assert.Null(p.Value);
        Assert.DoesNotContain("TOP-SECRET-VALUE", System.Text.Json.JsonSerializer.Serialize(log.Entries));
    }

    [Fact]
    public async Task Value_hashes_are_stable_within_a_run_and_salted_across_runs()
    {
        var (g1, _, l1) = Make();
        var (g2, _, l2) = Make();
        await g1.ExecuteAsync(Stmt(WriteKind.Insert, "a", "same"), TestContext.Current.CancellationToken);
        await g1.ExecuteAsync(Stmt(WriteKind.Insert, "b", "same"), TestContext.Current.CancellationToken);
        await g2.ExecuteAsync(Stmt(WriteKind.Insert, "a", "same"), TestContext.Current.CancellationToken);
        Assert.Equal(l1.Entries[0].Parameters![0].ValueHash, l1.Entries[2].Parameters![0].ValueHash);
        Assert.NotEqual(l1.Entries[0].Parameters![0].ValueHash, l2.Entries[0].Parameters![0].ValueHash);
    }

    [Fact]
    public async Task WithText_adds_the_text_and_WithValues_adds_redacted_values()
    {
        var redactor = new Redactor();
        redactor.Register("hunter2-secret");
        var (gate, _, log) = Make(o => o with { Detail = StatementLogDetail.WithValues, Redactor = redactor });
        await gate.ExecuteAsync(Stmt(WriteKind.Insert, value: "pw=hunter2-secret ok"), TestContext.Current.CancellationToken);
        var begin = log.Entries[0];
        Assert.Equal("INSERT INTO t VALUES (@p)", begin.Text);
        Assert.Equal("pw=*** ok", begin.Parameters![0].Value);
        Assert.DoesNotContain("hunter2-secret", System.Text.Json.JsonSerializer.Serialize(log.Entries));

        var (g2, _, l2) = Make(o => o with { Detail = StatementLogDetail.WithText });
        await g2.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken);
        Assert.NotNull(l2.Entries[0].Text);
        Assert.Null(l2.Entries[0].Parameters![0].Value);
    }

    [Fact]
    public async Task Text_is_redacted_when_logged()
    {
        var redactor = new Redactor();
        var (gate, _, log) = Make(o => o with { Detail = StatementLogDetail.WithText, Redactor = redactor });
        var s = new SqlStatement("x", "CREATE LOGIN u WITH Password=abc123;", [], new WriteIntent("x", WriteKind.Ddl, "t"));
        await gate.ExecuteAsync(s, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("abc123", log.Entries[0].Text);
    }

    [Fact]
    public async Task An_unwritable_log_means_the_statement_is_not_executed()
    {
        var exec = new FakeExecutor();
        var gate = new WriteGate(exec, new WriteGateOptions("apply", new ThrowingLog()));
        var ex = await Assert.ThrowsAsync<GateRefusedException>(() => gate.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken));
        Assert.Contains("log could not be written", ex.Message);
        Assert.Empty(exec.Calls);
    }

    [Fact]
    public async Task Dry_run_checks_and_logs_but_never_executes()
    {
        var log = new MemoryStatementLog();
        var gate = new WriteGate(null, new WriteGateOptions("plan", log) { DryRun = true });
        Assert.Equal(0, await gate.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken));
        Assert.Equal("dry-run", log.Entries.Single().Phase);
        var refused = new WriteGate(null, new WriteGateOptions("plan", log) { DryRun = true, Access = AccessMode.ReadOnly });
        await Assert.ThrowsAsync<GateRefusedException>(() => refused.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken));
    }

    [Fact]
    public void A_live_gate_needs_an_executor() =>
        Assert.Throws<ArgumentException>(() => new WriteGate(null, new WriteGateOptions("x", new MemoryStatementLog())));

    [Fact]
    public async Task A_failed_statement_logs_type_and_code_only_and_rolls_back()
    {
        var exec = new FakeExecutor { Fail = _ => new InvalidOperationException("duplicate key value (secret=1234)") };
        var (gate, _, log) = Make(exec: exec);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken));
        Assert.Contains("rollback", exec.Calls);
        var outcome = log.Entries.Single(e => e.Phase == "end").Outcome!;
        Assert.Equal("failed InvalidOperationException", outcome);
        Assert.DoesNotContain("secret", outcome);
    }

    // ---------------------------------------------------------------- siblings, refusal reasons, bulk values, lock text (plan 13, B06)

    [Fact]
    public async Task Sibling_gates_share_the_ordinal_and_the_log_but_each_runs_on_its_own_executor()
    {
        var (gate, exec, log) = Make();
        var other = new FakeExecutor();
        var sibling = gate.Sibling(other);
        await gate.ExecuteAsync(Stmt(WriteKind.Insert, "a"), TestContext.Current.CancellationToken);
        await sibling.ExecuteAsync(Stmt(WriteKind.Insert, "b"), TestContext.Current.CancellationToken);
        await gate.ExecuteAsync(Stmt(WriteKind.Insert, "c"), TestContext.Current.CancellationToken);
        Assert.Equal([1, 1, 2, 2, 3, 3], log.Entries.Select(e => e.Ordinal));
        Assert.Equal(["exec:a", "exec:c"], exec.Calls);
        Assert.Equal(["exec:b"], other.Calls);
        Assert.Equal(gate.RunId, sibling.RunId);
    }

    [Fact]
    public async Task Siblings_running_side_by_side_keep_the_log_one_ordered_sequence()
    {
        var (gate, _, log) = Make();
        var siblings = Enumerable.Range(0, 8).Select(_ => gate.Sibling(new FakeExecutor())).ToList();
        await Task.WhenAll(siblings.Select((g, i) => Task.Run(async () =>
        {
            for (var k = 0; k < 25; k++) await g.ExecuteAsync(Stmt(WriteKind.Insert, $"s{i}-{k}"), TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken)));
        var begins = log.Entries.Where(e => e.Phase == "begin").Select(e => e.Ordinal).ToList();
        Assert.Equal(begins.OrderBy(n => n), begins);          // an opening line takes its ordinal and is written as one step, so those lines are in ordinal order whichever session wrote them
        Assert.Equal(Enumerable.Range(1, 8 * 25), begins);     // and no ordinal is used twice or skipped
    }

    [Fact]
    public async Task A_sibling_may_be_given_other_permissions_and_a_dry_run_gate_has_none()
    {
        var (gate, _, _) = Make(o => o with { Permitted = WriteKind.Tracking });
        var data = gate.Sibling(new FakeExecutor(), WriteKind.Data);
        await data.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<GateRefusedException>(() => gate.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken));
        var dry = new WriteGate(null, new WriteGateOptions("plan", new MemoryStatementLog()) { DryRun = true });
        Assert.Throws<InvalidOperationException>(() => dry.Sibling(new FakeExecutor()));
    }

    [Theory]
    [InlineData(AccessMode.ReadOnly, WriteKind.Any, WriteKind.Insert, GateRefusal.ReadOnly)]
    [InlineData(AccessMode.ReadWrite, WriteKind.Tracking, WriteKind.Insert, GateRefusal.NotPermitted)]
    public async Task A_refusal_says_why(AccessMode access, WriteKind permitted, WriteKind kind, GateRefusal reason)
    {
        var (gate, exec, _) = Make(o => o with { Access = access, Permitted = permitted });
        var ex = await Assert.ThrowsAsync<GateRefusedException>(() => gate.ExecuteAsync(Stmt(kind), TestContext.Current.CancellationToken));
        Assert.Equal(reason, ex.Reason);
        Assert.Empty(exec.Calls);
    }

    [Fact]
    public async Task An_unwritable_log_is_a_refusal_with_its_own_reason()
    {
        var gate = new WriteGate(new FakeExecutor(), new WriteGateOptions("apply", new ThrowingLog()));
        var ex = await Assert.ThrowsAsync<GateRefusedException>(() => gate.ExecuteAsync(Stmt(WriteKind.Insert), TestContext.Current.CancellationToken));
        Assert.Equal(GateRefusal.LogUnavailable, ex.Reason);
    }

    [Fact]
    public async Task The_parameters_of_a_bulk_load_are_counted_in_the_log_never_listed()
    {
        var parameters = Enumerable.Range(0, 3).Select(i => new SqlParameter($"@p{i}", $"p{i}", CanonicalType.String(), "TOP-SECRET")).ToList();
        var s = new SqlStatement("b", "INSERT INTO t VALUES (@p0), (@p1), (@p2)", parameters, new WriteIntent("b", WriteKind.Insert, "t"), ParametersAreRows: true);

        var (gate, _, log) = Make(o => o with { Detail = StatementLogDetail.WithValues });
        await gate.ExecuteAsync(s, TestContext.Current.CancellationToken);
        var logged = Assert.Single(log.Entries[0].Parameters!);
        Assert.Equal("(bulk values)", logged.Name);
        Assert.Equal("3 values, not logged", logged.Value);
        Assert.DoesNotContain("TOP-SECRET", string.Join(" ", log.Entries.Select(e => e.Text + e.Parameters?.FirstOrDefault()?.Value)));

        var (quiet, _, quietLog) = Make();
        await quiet.ExecuteAsync(s, TestContext.Current.CancellationToken);
        Assert.Null(Assert.Single(quietLog.Entries[0].Parameters!).Value);      // below the full-value level not even the count is a value
    }

    [Fact]
    public async Task The_lock_resource_is_in_the_logged_text_only_when_the_gate_says_so()
    {
        var (quiet, _, quietLog) = Make(o => o with { Detail = StatementLogDetail.WithText });
        await quiet.AcquireLockAsync("dbdatabuild:warehouse", shared: true, ct: TestContext.Current.CancellationToken);
        Assert.Equal("acquire application lock (shared)", quietLog.Entries[0].Text);

        var (open, _, openLog) = Make(o => o with { Detail = StatementLogDetail.WithText, LogLockResource = true });
        await open.AcquireLockAsync("dbdatabuild:warehouse", shared: true, ct: TestContext.Current.CancellationToken);
        await open.ReleaseLockAsync("dbdatabuild:warehouse", shared: true, TestContext.Current.CancellationToken);
        Assert.Equal("acquire application lock dbdatabuild:warehouse (shared)", openLog.Entries[0].Text);
        Assert.Equal("release application lock dbdatabuild:warehouse (shared)", openLog.Entries.Single(e => e.StepId == "unlock" && e.Phase == "begin").Text);
    }

    [Fact]
    public async Task A_statement_hashes_the_same_whatever_the_line_endings()
    {
        var (gate, _, log) = Make();
        await gate.ExecuteAsync(new SqlStatement("a", "SELECT 1\r\nFROM t", [], new WriteIntent("a", WriteKind.Insert, "t")), TestContext.Current.CancellationToken);
        await gate.ExecuteAsync(new SqlStatement("b", "SELECT 1\nFROM t", [], new WriteIntent("b", WriteKind.Insert, "t")), TestContext.Current.CancellationToken);
        Assert.Equal(log.Entries[0].StatementHash, log.Entries[2].StatementHash);
    }

    [Fact]
    public async Task The_payload_reaches_the_executor_and_is_never_logged()
    {
        var seen = new List<object?>();
        var exec = new FakeExecutor { Rows = s => { seen.Add(s.Payload); return 1; } };
        var (gate, _, log) = Make(o => o with { Detail = StatementLogDetail.WithValues }, exec);
        await gate.ExecuteAsync(Stmt(WriteKind.Insert) with { Payload = "the caller's own statement" }, TestContext.Current.CancellationToken);
        Assert.Equal(["the caller's own statement"], seen);
        Assert.DoesNotContain("the caller's own statement", System.Text.Json.JsonSerializer.Serialize(log.Entries));
    }

    // ---------------------------------------------------------------- apply runner

    private static StatementSet Set(bool transactional, DeleteGuard? guard = null) => new("x",
    [
        new SqlStatement("x:count", "SELECT COUNT(*)", [], new WriteIntent("x:count", WriteKind.None, "t"), StatementRole.CountScope),
        new SqlStatement("x:delete", "DELETE", [], new WriteIntent("x:delete", WriteKind.Delete, "t", WriteRisk.Destructive)),
        new SqlStatement("x:insert", "INSERT", [], new WriteIntent("x:insert", WriteKind.Insert, "t")),
    ], transactional, guard);

    [Fact]
    public async Task A_transactional_set_runs_between_begin_and_commit()
    {
        var (gate, exec, _) = Make();
        var result = await gate.RunAsync(Set(true), TestContext.Current.CancellationToken);
        Assert.Equal(["begin", "scalar:x:count", "exec:x:delete", "exec:x:insert", "commit"], exec.Calls);
        Assert.Equal(1, result.RowsWritten);
        Assert.Equal(1, result.RowsDeleted);
    }

    [Fact]
    public async Task A_non_transactional_set_has_no_transaction_calls()
    {
        var (gate, exec, _) = Make();
        await gate.RunAsync(Set(false), TestContext.Current.CancellationToken);
        Assert.DoesNotContain("begin", exec.Calls);
        Assert.DoesNotContain("commit", exec.Calls);
    }

    [Fact]
    public async Task A_failure_in_a_transactional_set_rolls_back_and_never_commits()
    {
        var exec = new FakeExecutor { Fail = s => s.Id == "x:insert" ? new InvalidOperationException("boom") : null };
        var (gate, _, _) = Make(exec: exec);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.RunAsync(Set(true), TestContext.Current.CancellationToken));
        Assert.Contains("rollback", exec.Calls);
        Assert.DoesNotContain("commit", exec.Calls);
    }

    [Fact]
    public async Task The_delete_guard_rolls_back_and_throws_with_counts()
    {
        var exec = new FakeExecutor { Scalar = _ => 10L, Rows = s => s.Id == "x:delete" ? 8 : 5 };
        var (gate, _, _) = Make(exec: exec);
        var ex = await Assert.ThrowsAsync<DeleteGuardExceededException>(() => gate.RunAsync(Set(true, new DeleteGuard(0.5)), TestContext.Current.CancellationToken));
        Assert.Equal(10, ex.ScopeCount);
        Assert.Equal(8, ex.Deleted);
        Assert.Contains("rollback", exec.Calls);
        Assert.DoesNotContain("exec:x:insert", exec.Calls);
        Assert.DoesNotContain("commit", exec.Calls);
    }

    [Fact]
    public async Task The_delete_guard_passes_within_the_ratio_and_when_disabled()
    {
        var exec = new FakeExecutor { Scalar = _ => 10L, Rows = s => s.Id == "x:delete" ? 5 : 5 };
        var (gate, _, _) = Make(exec: exec);
        await gate.RunAsync(Set(true, new DeleteGuard(0.5)), TestContext.Current.CancellationToken);
        var exec2 = new FakeExecutor { Scalar = _ => 10L, Rows = s => 10 };
        var (g2, _, _) = Make(exec: exec2);
        await g2.RunAsync(Set(true, DeleteGuard.None), TestContext.Current.CancellationToken);
        Assert.Contains("commit", exec2.Calls);
    }

    [Fact]
    public async Task Readonly_access_refuses_a_whole_set_before_the_first_write()
    {
        var (gate, exec, _) = Make(o => o with { Access = AccessMode.ReadOnly });
        await Assert.ThrowsAsync<GateRefusedException>(() => gate.RunAsync(Set(true), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("exec:x:delete", exec.Calls);
        Assert.DoesNotContain("exec:x:insert", exec.Calls);
    }

    // ---------------------------------------------------------------- locks

    [Fact]
    public async Task Application_lock_is_acquired_released_and_logged_once()
    {
        var (gate, exec, log) = Make();
        Assert.True(await gate.AcquireLockAsync("target-1", ct: TestContext.Current.CancellationToken));
        await gate.ReleaseLockAsync("target-1", ct: TestContext.Current.CancellationToken);
        Assert.Equal(["lock:target-1", "unlock:target-1"], exec.Calls);
        Assert.Contains(log.Entries, e => e.Outcome == "granted");
        Assert.DoesNotContain(log.Entries, e => e.Text?.Contains("target-1") == true);
    }

    [Fact]
    public async Task A_held_lock_is_reported_not_thrown()
    {
        var (gate, exec, log) = Make(exec: new FakeExecutor { Locked = false });
        Assert.False(await gate.AcquireLockAsync("t", ct: TestContext.Current.CancellationToken));
        Assert.Single(exec.Calls);
        Assert.Contains(log.Entries, e => e.Outcome == "held by another process");
    }

    [Fact]
    public async Task Locks_are_available_in_readonly_mode_but_writes_are_not()
    {
        var (gate, _, _) = Make(o => o with { Access = AccessMode.ReadOnly });
        Assert.True(await gate.AcquireLockAsync("t", ct: TestContext.Current.CancellationToken));
    }

    // ---------------------------------------------------------------- bulk

    private sealed class CountingWriter : IBulkWriter
    {
        public int Calls;
        public async Task<long> WriteAsync(WriteTarget target, IAsyncEnumerable<DataBatch> rows, BulkOptions options, CancellationToken ct)
        {
            Calls++;
            long n = 0;
            await foreach (var b in rows) n += b.Rows;
            return n;
        }
    }

    private static async IAsyncEnumerable<DataBatch> Batches(TransferSchema s)
    {
        yield return new RowDataBatch(s, [[1], [2]]);
        yield return new RowDataBatch(s, [[3]]);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Bulk_writes_pass_the_same_checks_and_log_entries()
    {
        var schema = new TransferSchema([new("a", CanonicalType.Of(CanonicalKind.Int32))]);
        var (gate, _, log) = Make(o => o with { Permitted = WriteKind.Insert | WriteKind.Bulk });
        var w = new CountingWriter();
        var n = await gate.BulkWriteAsync(new WriteIntent("load", WriteKind.Insert, "t"), w, new WriteTarget(new TableName(null, "t"), schema), Batches(schema), new BulkOptions(), TestContext.Current.CancellationToken);
        Assert.Equal(3, n);
        Assert.Equal(["begin", "end"], log.Entries.Select(e => e.Phase));
        Assert.Equal(WriteKind.Insert | WriteKind.Bulk, Enum.Parse<WriteKind>(log.Entries[0].Kind));

        var (ro, _, _) = Make(o => o with { Access = AccessMode.ReadOnly });
        var w2 = new CountingWriter();
        await Assert.ThrowsAsync<GateRefusedException>(() => ro.BulkWriteAsync(new WriteIntent("load", WriteKind.Insert, "t"), w2, new WriteTarget(new TableName(null, "t"), schema), Batches(schema), new BulkOptions(), TestContext.Current.CancellationToken));
        Assert.Equal(0, w2.Calls);
    }

    [Fact]
    public async Task Dry_run_bulk_does_not_read_the_rows()
    {
        var schema = new TransferSchema([new("a", CanonicalType.Of(CanonicalKind.Int32))]);
        var gate = new WriteGate(null, new WriteGateOptions("p", new MemoryStatementLog()) { DryRun = true });
        var w = new CountingWriter();
        Assert.Equal(0, await gate.BulkWriteAsync(new WriteIntent("l", WriteKind.Insert, "t"), w, new WriteTarget(new TableName(null, "t"), schema), Batches(schema), new BulkOptions(), TestContext.Current.CancellationToken));
        Assert.Equal(0, w.Calls);
    }
}
