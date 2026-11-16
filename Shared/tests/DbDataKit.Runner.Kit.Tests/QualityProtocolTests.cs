using DbDataKit.Runner.Kit.Tests.Samples;
using DbDataKit.Runner.Protocol.V1;
using DbDataKit.State;
using Grpc.Core;

namespace DbDataKit.Runner.Kit.Tests.Samples
{
    /// <summary>Plan 35 I3: a job that records the results of checks it evaluated in the runner's quality module through the protocol, as a refresh does after its loads.</summary>
    [RunnerTask("quality-recorder")]
    public sealed class QualityRecorder : RunnerJob<NoInput, NoProgress, NoSlots>
    {
        public const string Dataset = "postgres://pg1/warehouse.sales.orders";

        public static (Guid RunId, decimal? FirstBaseline, decimal? SecondBaseline, CheckStatusRow Failed, CheckStatusRow? Status, IReadOnlyList<CheckStatusRow> Blocks)? Last { get; set; }

        protected override async Task RunAsync(CancellationToken ct)
        {
            var at = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
            var first = await Quality.BaselineAsync(Dataset, "orders_rows", ct);
            await Quality.RecordAsync(new CheckResultRecord("orders_rows", Dataset, CheckOutcome.Pass, at, 0, 50, Observed: 1234), ct);
            var second = await Quality.BaselineAsync(Dataset, "orders_rows", ct);
            var keys = Enumerable.Range(1, 12).Select(i => $"order_id={i}").ToList();
            var failed = await Quality.RecordAsync(new CheckResultRecord("orders_customer_present", Dataset, CheckOutcome.Fail, at, 12, 0, keys, RunId: "not-this-run", Severity: "block"), ct);
            var status = await Quality.StatusAsync(Dataset, "orders_customer_present", ct);
            var blocks = await Quality.BlocksAsync(ct);
            Last = (Run.Id, first, second, failed, status, blocks);
        }
    }
}

namespace DbDataKit.Runner.Kit.Tests
{
    /// <summary>
    /// Plan 35 I3: a task evaluates checks in its own process and the runner records each result in its <c>quality</c> module through the protocol: with the store's block rules, under the run's own id whatever the task says, and only when what a
    /// result says is one the module knows.
    /// </summary>
    public sealed class QualityProtocolTests
    {
        private const string Dataset = QualityRecorder.Dataset;

        [Theory]
        [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
        public async Task A_task_records_its_results_in_the_runners_quality_module_under_its_own_run_and_the_block_rules_are_the_stores(string engine)
        {
            using var h = new Harness(engine, remote: true);
            QualityRecorder.Last = null;

            var result = await h.Run<QualityRecorder>();

            Assert.Equal(TaskEndState.Succeeded, result.End.State);
            var (runId, first, second, failed, status, blocks) = QualityRecorder.Last!.Value;
            Assert.Null(first);                     // no evaluation before: the first only records
            Assert.Equal(1234m, second);            // the reading of the evaluation just recorded
            Assert.True(failed.Blocking);           // a block check that failed blocks
            Assert.Equal(CheckOutcome.Fail, status!.Outcome);
            Assert.Equal(failed.LastResultId, status.LastResultId);
            Assert.Equal("orders_customer_present", Assert.Single(blocks).CheckId);

            var store = new QualityStore(h.Database);
            var stored = Assert.Single(store.History(Dataset, "orders_customer_present"));
            Assert.Equal(runId.ToString("D"), stored.RunId);        // the run that sent it, not what the task said
            Assert.Equal(QualityStore.MaxSampleKeys, stored.SampleKeys.Count);
            Assert.Equal(runId.ToString("D"), Assert.Single(store.History(Dataset, "orders_rows")).RunId);
            Assert.True(Assert.Single(store.Blocks()).Blocking);
        }

        [Theory]
        [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
        public void A_result_the_module_does_not_know_is_refused_and_nothing_is_written(string engine)
        {
            using var h = new Harness(engine, remote: true);
            using var run = new RawClient(h.Host.Endpoint, h.Grant("quality-recorder"));
            run.Start();
            QualityRecordRequest Request(Action<QualityRecordRequest> change)
            {
                var request = new QualityRecordRequest { CheckId = "c1", Dataset = Dataset, Status = "fail", EvaluatedAt = "2026-11-16T10:00:00.0000000+00:00", Severity = "warn", OnError = "alert", Measured = "3" };
                change(request);
                return request;
            }

            Assert.Equal("fail", run.Client.QualityRecord(Request(_ => { }), run.Headers).Status.Status);
            foreach (var bad in new Action<QualityRecordRequest>[]
            {
                r => r.Severity = "fatal", r => r.OnError = "ignore", r => r.Status = "maybe", r => r.Dataset = "", r => r.CheckId = " ", r => r.EvaluatedAt = "",
                r => r.Measured = "a lot", r => r.Message = new string('x', 5000), r => r.SampleKeys.Add(new string('k', 600)),
            })
                Assert.Equal(StatusCode.InvalidArgument, Assert.Throws<RpcException>(() => run.Client.QualityRecord(Request(r => { r.CheckId = "c2"; bad(r); }), run.Headers)).StatusCode);

            var store = new QualityStore(h.Database);
            Assert.Single(store.All());
            Assert.Empty(store.History(Dataset, "c2"));
            Assert.Null(run.Client.QualityStatus(new QualityCheckRef { Dataset = Dataset, CheckId = "c2" }, run.Headers).Status);
            Assert.Equal("", run.Client.QualityBaseline(new QualityCheckRef { Dataset = Dataset, CheckId = "c1" }, run.Headers).Value);   // a failed check without a reading is no baseline
            Assert.Empty(run.Client.QualityBlocks(new QualityBlocksRequest(), run.Headers).Blocks);                                      // a warn check never blocks
        }

        [Fact]
        public async Task A_run_in_this_process_records_in_the_quality_module_of_its_own_state_under_its_run()
        {
            using var h = new Harness(DbDataKit.State.StateEngineIds.Sqlite);
            QualityRecorder.Last = null;
            var result = await h.Run<QualityRecorder>();
            Assert.Equal(TaskEndState.Succeeded, result.End.State);
            Assert.Equal(QualityRecorder.Last!.Value.RunId.ToString("D"), Assert.Single(new QualityStore(h.Database).History(Dataset, "orders_customer_present")).RunId);
        }
    }
}
