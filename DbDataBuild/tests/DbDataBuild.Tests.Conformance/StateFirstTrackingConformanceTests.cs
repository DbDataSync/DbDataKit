using System.Text.Json.Nodes;
using DbDataBuild.Execution;
using DbDataBuild.State;
using DbDataKit.State;
using DbDataKit.Tracking.Build;
using Xunit;
using AuditLog = DbDataBuild.Execution.AuditLog;

namespace DbDataBuild.Tests.Conformance;

/// <summary>
/// Plan 28c E16, <c>tracking: { mode: state }</c> on real engines: a project whose tracking is state-first writes every record of the commands it runs (init, deploy, refresh, ack) to the state before the target, and what the state holds equals what a
/// follower copies from the target afterwards, row by row for every stream (the same keys, the same order, the same documents). A run with no state to write to is refused before a statement runs, and a journal that fails stops the write before the
/// target is touched.
/// </summary>
public partial class ApplyConformanceTests
{
    private static string StateFirstConfig(string name) => "tracking: { connection: " + name + " }";

    private static async Task<Run> StateFirstSetUp(string name)
    {
        var run = await SetUp(name);
        var config = File.ReadAllText(Path.Combine(run.Dir, "dbdatabuild.yml"));
        Assert.Contains(StateFirstConfig(name), config);
        run.Write("dbdatabuild.yml", config.Replace(StateFirstConfig(name), "tracking: { connection: " + name + ", mode: state }"));
        return run;
    }

    private static StateDatabase SqliteState(string dir, string file) => StateDatabase.OpenModules(Path.Combine(dir, file), BuildTrackingState.Modules, BuildTrackingState.Scope);

    /// <summary>Asserts every stream of the tracking tables is the same in the state the commands wrote first and in a state a follower filled from the target.</summary>
    private static async Task AssertStateEqualsTheFollowersCopyAsync(Run run, string name, StateDatabase first, string identity, string dir)
    {
        var copy = SqliteState(dir, "follower.db");
        var scope = new TrackingScope(name, "dbdatabuild", name);
        await using var read = await ReadSession.OpenAsync(LoginSettings.FromEnvironment(name, DbDataBuild.Execution.Login.Read, run.Env).Settings!);
        var copier = new BuildTrackingCopier(identity, new BuildTrackingSource(read, scope, identity, TimeSpan.Zero), new BuildTrackingStore(copy), (kind, json) => DbDataBuild.Cli.MetadataInfoRows.HasRows(kind) ? DbDataBuild.Cli.MetadataInfoRows.From(kind, json) : null);
        var result = await copier.RunAsync(TimeSpan.FromSeconds(60));
        Assert.True(result.Ok, string.Join("; ", result.Problems));

        var a = new BuildTrackingStore(first);
        var b = new BuildTrackingStore(copy);
        foreach (var stream in BuildStreams.All.Where(s => s.Name != "tracking_version"))
        {
            var written = a.Rows(identity, stream.Name);
            var copied = b.Rows(identity, stream.Name);
            Assert.True(copied.Count > 0 || stream.Name is "block_log" or "plan_store" or "operation_interval", $"the commands left no row of {stream.Name} to compare");
            Assert.Equal(copied.Select(r => r.Key), written.Select(r => r.Key));
            for (var i = 0; i < copied.Count; i++)
            {
                Assert.Equal(copied[i].Order, written[i].Order);
                if (stream.Name == "metadata_document")
                {
                    // PostgreSQL's jsonb gives the document text back normalised: the same JSON, not the same characters
                    var x = JsonNode.Parse(copied[i].Document)!.AsObject();
                    var y = JsonNode.Parse(written[i].Document)!.AsObject();
                    var inner = (string)x["document"]!;
                    Assert.True(JsonNode.DeepEquals(JsonNode.Parse(inner), JsonNode.Parse((string)y["document"]!)), $"the metadata document {copied[i].Key} differs");
                    x.Remove("document"); y.Remove("document");
                    Assert.Equal(x.ToJsonString(), y.ToJsonString());
                }
                else Assert.Equal(copied[i].Document, written[i].Document);
            }
        }

        // the snapshots are rebuilt from the documents on both sides
        foreach (var table in BuildStreams.Snapshots)
            Assert.Equal(b.Rows(identity, table).Select(r => r.Key).Order(StringComparer.Ordinal), a.Rows(identity, table).Select(r => r.Key).Order(StringComparer.Ordinal));
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_state_first_project_writes_its_records_to_the_state_first_and_the_state_equals_what_a_follower_copies_from_the_target(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ddb-statefirst-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var state = SqliteState(dir, "first.db");
        var sink = new StateBuildTrackingSink(new BuildTrackingStore(state));
        Run? made = null;
        try
        {
            // the server's primary reaches the commands as the variable the Build server sets, and the state as the host gives it to the flow; the setup's own init, compile and deploy are journaled too
            Run.PrimaryOverride.Value = "state";
            using (TrackingJournalSource.Use(sink))
            {
                made = await FailureSetUp(name, "    p: { on_failure: continue }\n");
                var run = made;
                await run.Engine.ExecAsync("INSERT INTO staging.orders VALUES (4, 40.00)");
                Ok(run.Cli("connection", "refresh", name, "p"), "refresh");       // a refresh writes a plan event, its members and a load for each model
                Ok(run.Cli("connection", "publish"), "publish");                  // the documents of the models: the state rebuilds object_info and metadata_dependency from them, as a publish does on the target
            }

            var store = new BuildTrackingStore(state);
            var identity = Assert.Single(store.Identities());
            Assert.EndsWith("@" + name, identity, StringComparison.Ordinal);
            foreach (var stream in new[] { "migration_log", "run_log", "ddl_log", "refresh_plan_member", "schema_version", "metadata_document" })
                Assert.True(store.Count(identity, stream) > 0, $"the state holds no row of {stream}");
            Assert.Empty(store.Unsettled(identity, "ddl_log", 100));
            Assert.Empty(store.Unsettled(identity, "run_log", 100));

            await AssertStateEqualsTheFollowersCopyAsync(made, name, state, identity, dir);
        }
        finally
        {
            Run.PrimaryOverride.Value = null;
            if (made is not null) { if (Directory.Exists(made.Dir)) Directory.Delete(made.Dir, true); await made.Engine.DisposeAsync(); }
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    [SkippableTheory, MemberData(nameof(Engines))]
    public async Task A_state_first_run_with_no_state_is_refused_before_anything_is_written_and_a_journal_that_fails_stops_the_write_before_the_target(string name)
    {
        var run = await StateFirstSetUp(name);
        await using var engine = run.Engine;
        try
        {
            // init writes no tracking row through a writer: the layout record is the script's, so the project can be initialised without a state
            var noState = run.Cli("connection", "init", "--apply");
            Assert.True(noState.Exit == 0 || noState.Err.Contains("state-first", StringComparison.Ordinal), noState.Out + noState.Err);
            Ok(run.Cli("project", "compile"), "compile");
            var plan = run.Cli("connection", "deploy", "--write-plan");
            if (plan.Exit == 0)
            {
                var refused = run.Cli("connection", "deploy", "--apply-plan", run.PlanFile(plan.Out));
                Assert.NotEqual(0, refused.Exit);
                Assert.Contains("state-first", refused.Err + refused.Out, StringComparison.Ordinal);
                Assert.Contains("Nothing was executed", refused.Out, StringComparison.Ordinal);
                Assert.Equal(0, await CountAsync(run, $"{run.Q("dbdatabuild")}.{run.Q("migration_log")}"));
            }

            // a journal that fails: the writer stops before its statement, so the target never holds a row the state lacks
            var scope = new TrackingScope(name, "dbdatabuild", name) { Journal = new ThrowingJournal() };
            await using var gate = await MutationGate.OpenAsync(LoginSettings.FromEnvironment(name, DbDataBuild.Execution.Login.Write, run.Env).Settings!, "test", StatementKind.Tracking, new MemoryStatementLog(), Guid.NewGuid());
            await Assert.ThrowsAsync<InvalidOperationException>(() => AuditLog.AcknowledgeAsync(gate, scope, "a1", "marts.v_orders", "DDB-300", "detail", "me", "reason"));
            Assert.Equal(0, await CountAsync(run, $"{run.Q("dbdatabuild")}.{run.Q("block_log")}"));
        }
        finally { if (Directory.Exists(run.Dir)) Directory.Delete(run.Dir, true); }
    }

    private sealed class ThrowingJournal : ITrackingJournal
    {
        public string Identity => "buildtracking:x@y";
        public Task InsertAsync(string table, IReadOnlyDictionary<string, object?> columns, CancellationToken cancellationToken) => throw new InvalidOperationException("the state is down");
        public Task UpdateAsync(string table, IReadOnlyDictionary<string, object?> key, IReadOnlyDictionary<string, object?> changes, CancellationToken cancellationToken) => throw new InvalidOperationException("the state is down");
    }
}
