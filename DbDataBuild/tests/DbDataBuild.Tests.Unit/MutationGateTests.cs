using System.Data;
using DbDataBuild.Core;
using DbDataBuild.Execution;

namespace DbDataBuild.Tests.Unit;

public class MutationGateTests
{
    /// <summary>Records what happened and in which order, shared with the log so ordering can be asserted.</summary>
    private sealed class Probe : IWriteExecutor, IStatementLog
    {
        public List<string> Events { get; } = [];
        public Exception? Throw { get; set; }
        public bool LogFails { get; set; }
        public Task<long> ExecuteAsync(string text, IReadOnlyList<GateParameter> parameters, CancellationToken ct)
        {
            Events.Add("exec:" + text);
            if (Throw != null) throw Throw;
            return Task.FromResult(7L);
        }
        public void Append(StatementLogEntry entry)
        {
            if (LogFails) throw new IOException("disk full");
            Events.Add($"log:{entry.Phase}:{entry.StepId}");
        }
        public bool LockGranted { get; set; } = true;
        public Task<bool> TryLockAsync(string resource, bool shared, CancellationToken ct) { Events.Add("lock:" + resource); return Task.FromResult(LockGranted); }
        public Task UnlockAsync(string resource, bool shared, CancellationToken ct) { Events.Add("unlock:" + resource); return Task.CompletedTask; }
        public Task RecoverAsync(CancellationToken ct) { Events.Add("recover"); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static MutationGate Gate(Probe p, StatementKind permitted = StatementKind.Data | StatementKind.Tracking, bool dry = false) =>
        MutationGate.ForTesting(p, "run", permitted, p, dry);

    [Fact]
    public async Task The_statement_is_logged_before_it_runs_and_the_outcome_after()
    {
        var p = new Probe();
        var rows = await Gate(p).ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "INSERT 1"));
        Assert.Equal(7, rows);
        Assert.Equal(["log:begin:s1", "exec:INSERT 1", "log:end:s1"], p.Events);
    }

    [Fact]
    public async Task A_statement_of_a_kind_the_command_does_not_permit_is_refused_and_not_logged_as_run()
    {
        var p = new Probe();
        var ex = await Assert.ThrowsAsync<GateRefusedException>(() => Gate(p).ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Ddl, "DROP TABLE x")));
        Assert.Equal("DDB-502", ex.Diagnostic.Code);
        Assert.Empty(p.Events);
    }

    [Fact]
    public async Task If_the_log_cannot_be_written_the_statement_does_not_run()
    {
        var p = new Probe { LogFails = true };
        var ex = await Assert.ThrowsAsync<GateRefusedException>(() => Gate(p).ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "INSERT 1")));
        Assert.Equal("DDB-503", ex.Diagnostic.Code);
        Assert.DoesNotContain(p.Events, e => e.StartsWith("exec:"));
    }

    [Fact]
    public async Task A_failing_statement_is_logged_with_the_error_type_and_never_its_message()
    {
        var log = new MemoryStatementLog();
        var p = new Probe { Throw = new InvalidOperationException("Violation of key for value 'secret-customer-42'") };
        var gate = MutationGate.ForTesting(p, "run", StatementKind.Data, log);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "INSERT 1")));
        var end = log.Entries.Single(e => e.Phase == "end");
        Assert.Equal("failed InvalidOperationException", end.Outcome);
        Assert.DoesNotContain("secret", string.Join(" ", log.Entries.Select(e => e.Outcome + e.Text)));
    }

    [Fact]
    public async Task A_failed_statement_is_followed_by_a_recovery_so_later_tracking_writes_can_run()
    {
        var p = new Probe { Throw = new InvalidOperationException("boom") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Gate(p).ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "INSERT 1")));
        Assert.Equal(["log:begin:s1", "exec:INSERT 1", "log:end:s1", "log:recover:s1:rollback", "recover"], p.Events);
    }

    [Fact]
    public async Task The_application_lock_is_logged_and_a_lock_held_elsewhere_is_reported_not_waited_for()
    {
        var p = new Probe();
        var gate = Gate(p);
        Assert.True(await gate.TryAcquireApplicationLockAsync("dbdatabuild:x"));
        await gate.ReleaseApplicationLockAsync("dbdatabuild:x");
        Assert.Equal(["log:begin:lock", "lock:dbdatabuild:x", "log:end:lock", "log:begin:unlock", "unlock:dbdatabuild:x", "log:end:unlock"], p.Events);

        var busy = new Probe { LockGranted = false };
        Assert.False(await Gate(busy).TryAcquireApplicationLockAsync("dbdatabuild:x"));
    }

    [Fact]
    public async Task Dry_run_takes_no_lock_and_runs_no_recovery()
    {
        var p = new Probe();
        var gate = Gate(p, dry: true);
        Assert.True(await gate.TryAcquireApplicationLockAsync("r"));
        await gate.ReleaseApplicationLockAsync("r");
        Assert.DoesNotContain(p.Events, e => e.StartsWith("lock:") || e.StartsWith("unlock:"));
    }

    [Fact]
    public async Task Dry_run_runs_every_check_and_log_entry_but_never_the_executor()
    {
        var p = new Probe();
        var gate = Gate(p, dry: true);
        Assert.Equal(0, await gate.ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "INSERT 1")));
        Assert.Equal(["log:dry-run:s1"], p.Events);
        await Assert.ThrowsAsync<GateRefusedException>(() => gate.ExecuteAsync(GateStatement.FromPlanStep("s2", StatementKind.Ddl, "DROP TABLE x")));
    }

    [Fact]
    public void A_plan_step_can_never_claim_to_be_a_tracking_statement()
    {
        Assert.Throws<ArgumentException>(() => GateStatement.FromPlanStep("s1", StatementKind.Tracking, "INSERT 1"));
        Assert.Throws<ArgumentException>(() => GateStatement.FromPlanStep("s1", (StatementKind)64, "INSERT 1"));
    }

    [Fact]
    public async Task The_log_records_hash_text_parameters_and_ordinal_in_order()
    {
        var log = new MemoryStatementLog();
        var gate = MutationGate.ForTesting(new Probe(), "run", StatementKind.Data, log);
        await gate.ExecuteAsync(GateStatement.FromPlanStep("a", StatementKind.Data, "UPDATE t SET x = @x", [new("x", DbType.Int32, 5)]));
        await gate.ExecuteAsync(GateStatement.FromPlanStep("b", StatementKind.Data, "UPDATE t SET y = 1"));
        Assert.Equal([1, 1, 2, 2], log.Entries.Select(e => e.Ordinal));
        var begin = log.Entries[0];
        Assert.Equal(DbDataKit.Schema.Hashing.ScriptHash("UPDATE t SET x = @x"), begin.StatementHash);
        Assert.Equal("UPDATE t SET x = @x", begin.Text);
        Assert.Equal([("x", "5")], begin.Parameters!.Select(p => (p.Name, p.Value)));
        Assert.Null(log.Entries[1].Text);
    }

    /// <summary>
    /// The owner's rule for B06: Build logs the values of a statement's parameters, as it always has, and not hashes of them. The shared gate's own default is hashes; this tool asks for the full-value level in one
    /// place (<c>MutationGate.Options</c>), and this test fails if that ever changes. (Whether hashes would be better is a separate decision for the owner.)
    /// </summary>
    [Fact]
    public async Task Parameter_values_are_logged_as_they_always_were_never_as_hashes()
    {
        var log = new MemoryStatementLog();
        var gate = MutationGate.ForTesting(new Probe(), "apply", StatementKind.Data, log);
        await gate.ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "UPDATE t SET a = @n, b = @name, c = @gone",
            [new("n", DbType.Int32, 5), new("name", DbType.String, "secret-customer-42"), new("gone", DbType.String, null)]));
        var begin = log.Entries.Single(e => e.Phase == "begin");
        Assert.Equal([("n", "5"), ("name", "secret-customer-42"), ("gone", "")], begin.Parameters!.Select(p => (p.Name, p.Value)));      // a null is the empty text, as Convert.ToString gives it and as the log always showed it
        Assert.Equal("UPDATE t SET a = @n, b = @name, c = @gone", begin.Text);
        Assert.All(log.Entries.Where(e => e.Phase != "begin"), e => Assert.Null(e.Parameters));
    }

    [Fact]
    public async Task The_values_are_in_the_file_in_the_clear_as_the_file_has_always_shown_them()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = Guid.NewGuid();
            using var log = new FileStatementLog(dir, "apply", run);
            var gate = MutationGate.ForTesting(new Probe(), "apply", StatementKind.Data, log);
            await gate.ExecuteAsync(GateStatement.FromPlanStep("s1", StatementKind.Data, "UPDATE t SET b = @name", [new("name", DbType.String, "secret-customer-42")]));
            var begin = ReadWhileOpen(log.Path).First();
            Assert.Contains("\"name\":\"name\"", begin);
            Assert.Contains("\"value\":\"secret-customer-42\"", begin);      // the value, in the clear, as the file has always shown it
            Assert.Contains("\"hash\":\"", begin);                          // and, new with the shared log, a salted hash and the type beside it
            Assert.Contains("\"type\":\"string\"", begin);
            Assert.Contains("\"text\":\"UPDATE t SET b = @name\"", begin);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void The_log_file_can_be_read_by_its_owner_only_on_a_system_that_has_owners()
    {
        if (OperatingSystem.IsWindows()) return;      // there the file inherits the folder's access list, which is the project's own
        var dir = Path.Combine(Path.GetTempPath(), "ddb-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var log = new FileStatementLog(dir, "apply", Guid.NewGuid());
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(log.Path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public async Task The_parameters_of_a_bulk_insert_are_counted_not_listed()
    {
        var log = new MemoryStatementLog();
        var gate = MutationGate.ForTesting(new Probe(), "seed", StatementKind.Data, log);
        await gate.ExecuteAsync(GateStatement.BulkInsert("rows", "INSERT INTO t VALUES (@a), (@b)", [new("a", DbType.String, "row-one"), new("b", DbType.String, "row-two")]));
        var logged = Assert.Single(log.Entries[0].Parameters!);
        Assert.Equal(("(bulk values)", "2 values, not logged"), (logged.Name, logged.Value));
    }

    [Fact]
    public async Task The_application_lock_names_its_resource_in_the_log()
    {
        var log = new MemoryStatementLog();
        var gate = MutationGate.ForTesting(new Probe(), "apply", StatementKind.Data, log);
        await gate.AcquireApplicationLockAsync("dbdatabuild:warehouse", shared: true, TimeSpan.Zero);
        await gate.ReleaseApplicationLockAsync("dbdatabuild:warehouse", shared: true);
        Assert.Equal(["acquire application lock dbdatabuild:warehouse (shared)", "release application lock dbdatabuild:warehouse (shared)"], log.Entries.Where(e => e.Phase == "begin").Select(e => e.Text));
    }

    [Fact]
    public async Task Sibling_gates_write_one_ordered_log_and_obey_the_permissions_they_are_given()
    {
        var log = new MemoryStatementLog();
        var gate = MutationGate.ForTesting(new Probe(), "refresh", StatementKind.Data | StatementKind.Tracking, log);
        var sibling = gate.SiblingForTesting(new Probe());
        await gate.ExecuteAsync(GateStatement.FromPlanStep("a", StatementKind.Data, "INSERT 1"));
        await sibling.ExecuteAsync(GateStatement.FromPlanStep("b", StatementKind.Data, "INSERT 2"));
        Assert.Equal([1, 1, 2, 2], log.Entries.Select(e => e.Ordinal));
        Assert.Equal(gate.RunId, sibling.RunId);
        await Assert.ThrowsAsync<GateRefusedException>(() => sibling.ExecuteAsync(GateStatement.FromPlanStep("c", StatementKind.Ddl, "DROP TABLE x")));
    }

    [Fact]
    public void File_log_is_durable_per_line()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var run = Guid.NewGuid();
            using var log = new FileStatementLog(dir, "apply", run);
            log.Append(new StatementLogEntry(run, 1, DateTime.UtcNow, "apply", "begin", "s1", "Data", "h", "SELECT 'é'", [new DbDataKit.Transfer.LoggedParameter("p", "String", "", "v")], null));
            var line = ReadWhileOpen(log.Path).Single(); // readable while the log is still open: it was flushed
            Assert.Contains("\"step\":\"s1\"", line);
            Assert.Contains("é", line);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Logins_are_read_from_the_named_variable_with_no_fallback_between_them()
    {
        var env = new Dictionary<string, string?> { ["DBDATA_BUILD_SQLSERVER_WRITE"] = "Server=x;User Id=writer;Password=hunter2" };
        var (write, _) = LoginSettings.FromEnvironment("sqlserver", Login.Write, env.GetValueOrDefault);
        Assert.NotNull(write);
        var (read, error) = LoginSettings.FromEnvironment("sqlserver", Login.Read, env.GetValueOrDefault);
        Assert.Null(read);
        Assert.Equal("DDB-501", error!.Code);
        Assert.Contains("DBDATA_BUILD_SQLSERVER_READ", error.Found);
        Assert.DoesNotContain("hunter2", error.Found);
    }

    [Fact]
    public void The_old_DBDATABUILD_names_are_not_read()
    {
        var env = new Dictionary<string, string?> { ["DBDATABUILD_SQLSERVER_READ"] = "Server=x;User Id=reader;Password=hunter2" };
        var (read, error) = LoginSettings.FromEnvironment("sqlserver", Login.Read, env.GetValueOrDefault);
        Assert.Null(read);
        Assert.Equal("DDB-501", error!.Code);
        Assert.Contains("DBDATA_BUILD_SQLSERVER_READ", error.Found);
    }

    [Theory]
    [InlineData("warehouse", "DBDATA_BUILD_WAREHOUSE_READ")]
    [InlineData("my-dw", "DBDATA_BUILD_MY_DW_READ")]
    [InlineData("dw.1", "DBDATA_BUILD_DW_1_READ")]
    public void The_variable_name_is_the_rule_every_product_shares(string connection, string expected)
    {
        Assert.Equal(expected, LoginSettings.VariableName(connection, Login.Read));
        var env = new Dictionary<string, string?> { [expected] = "Server=x;User Id=reader;Password=hunter2" };
        Assert.NotNull(LoginSettings.FromEnvironment(connection, Login.Read, env.GetValueOrDefault).Settings);
    }

    [Fact]
    public void The_login_description_names_the_variable_and_user_and_never_the_password()
    {
        var env = new Dictionary<string, string?> { ["DBDATA_BUILD_POSTGRES_READ"] = "Host=h;Username=reader;Password=hunter2" };
        var (settings, _) = LoginSettings.FromEnvironment("postgres", Login.Read, env.GetValueOrDefault);
        Assert.Equal("reader (DBDATA_BUILD_POSTGRES_READ)", settings!.Describe());
        var (integrated, _) = LoginSettings.FromEnvironment("sqlserver", Login.Read, new Dictionary<string, string?> { ["DBDATA_BUILD_SQLSERVER_READ"] = "Server=x;Integrated Security=true" }.GetValueOrDefault);
        Assert.Equal("integrated/default (DBDATA_BUILD_SQLSERVER_READ)", integrated!.Describe());
    }

    /// <summary>A reader must allow the writer's open handle (FileShare.ReadWrite); File.ReadAllLines does not, and on Windows that is a sharing violation.</summary>
    private static string[] ReadWhileOpen(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(fs);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}

