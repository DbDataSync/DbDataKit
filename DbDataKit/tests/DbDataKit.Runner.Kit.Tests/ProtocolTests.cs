using System.Text;
using DbDataKit.Monitoring;
using DbDataKit.Runner.Kit.Tests.Samples;
using DbDataKit.Runner.Protocol;
using DbDataKit.Runner.Protocol.Server;
using DbDataKit.Runner.Protocol.V1;
using DbDataKit.State;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Time.Testing;
using V1CheckpointChange = DbDataKit.Runner.Protocol.V1.CheckpointChange;

namespace DbDataKit.Runner.Kit.Tests;

/// <summary>A raw client of the protocol: what a task in another language would write. Used to make the calls the kit never makes (another run's ids, a missing token, a wrong version).</summary>
internal sealed class RawClient : IDisposable
{
    private readonly GrpcChannel _channel;

    public RawClient(RunnerEndpoint endpoint, string? token)
    {
        _channel = endpoint.CreateChannel();
        Client = new RunnerApi.RunnerApiClient(_channel);
        if (token is not null) Headers.Add(RunnerProtocolNames.TokenHeader, token);
    }

    public RunnerApi.RunnerApiClient Client { get; }

    public Metadata Headers { get; } = [];

    public GrpcChannel Channel => _channel;

    public StartInfo Start() => Client.Start(new StartRequest { ProtocolVersion = RunnerProtocol.V1, Client = "tests" }, Headers);

    /// <summary>BeginStep, then a complete upload of <paramref name="data"/> in chunks of <paramref name="chunk"/> bytes; returns the payload id.</summary>
    public async Task<string> UploadAsync(string step, string key, byte[] data, int chunk = 64 * 1024)
    {
        var begin = Client.BeginStep(new BeginStepRequest { Name = step, Key = key }, Headers);
        Assert.Equal(BeginStepReply.OutcomeOneofCase.Run, begin.OutcomeCase);
        using var call = Client.StepChunk(Headers);
        await call.RequestStream.WriteAsync(new StepChunkRequest { Header = new StepChunkHeader { Name = step, Key = key, MediaType = "application/octet-stream" } });
        for (var offset = 0; offset < data.Length; offset += chunk)
            await call.RequestStream.WriteAsync(new StepChunkRequest { Data = ByteString.CopyFrom(data, offset, Math.Min(chunk, data.Length - offset)) });
        await call.RequestStream.WriteAsync(new StepChunkRequest { Complete = true });
        await call.RequestStream.CompleteAsync();
        var reply = await call.ResponseAsync;
        Assert.True(reply.Completed);
        return reply.PayloadId;
    }

    public void Dispose() => _channel.Dispose();
}

/// <summary>Slice R03b: what the runner protocol adds to the kit, beyond running the sample jobs through it (<see cref="SampleJobsOverTheProtocolTests"/>).</summary>
public sealed class ProtocolTests
{
    private static async Task<RpcException> RefusedAsync(Func<Task> call) => await Assert.ThrowsAsync<RpcException>(call);

    private static string Chain() => Guid.NewGuid().ToString("D");

    // ---- every call needs a live token

    /// <summary>Calls the method with an empty request, whatever its kind, and says how the runner answered.</summary>
    private static async Task<StatusCode> CallAsync(GrpcChannel channel, MethodDescriptor descriptor, Metadata headers)
    {
        var input = Marshallers.Create<IMessage>(m => m.ToByteArray(), bytes => descriptor.InputType.Parser.ParseFrom(bytes));
        var output = Marshallers.Create<IMessage>(m => m.ToByteArray(), bytes => descriptor.OutputType.Parser.ParseFrom(bytes));
        var type = descriptor.IsClientStreaming ? (descriptor.IsServerStreaming ? MethodType.DuplexStreaming : MethodType.ClientStreaming) : descriptor.IsServerStreaming ? MethodType.ServerStreaming : MethodType.Unary;
        var method = new Method<IMessage, IMessage>(type, RunnerV1Reflection.Descriptor.Services[0].FullName, descriptor.Name, input, output);
        var invoker = channel.CreateCallInvoker();
        var options = new CallOptions(headers, deadline: DateTime.UtcNow.AddSeconds(20));
        var request = descriptor.InputType.Parser.ParseFrom([]);
        try
        {
            switch (type)
            {
                case MethodType.Unary:
                    await invoker.AsyncUnaryCall(method, null, options, request).ResponseAsync;
                    break;
                case MethodType.ClientStreaming:
                    using (var call = invoker.AsyncClientStreamingCall(method, null, options))
                    {
                        await call.RequestStream.CompleteAsync();
                        await call.ResponseAsync;
                    }

                    break;
                default:
                    using (var call = invoker.AsyncServerStreamingCall(method, null, options, request))
                    {
                        // a granted WatchCancel would wait for a cancel that never comes: give up on it after a moment
                        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                        try
                        {
                            await call.ResponseStream.MoveNext(cts.Token);
                        }
                        catch (OperationCanceledException)
                        {
                        }
                    }

                    break;
            }

            return StatusCode.OK;
        }
        catch (RpcException ex)
        {
            return ex.StatusCode;
        }
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_call_without_a_live_run_token_is_refused_on_every_method_of_the_service(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-11-07T08:00:00Z"));
        await using var host = await RunnerProtocolHost.StartAsync(new RunnerProtocolHostOptions { Time = clock, DefaultTokenLifetime = TimeSpan.FromHours(1) });

        var expired = h.Grant("orders", host: host);
        clock.Advance(TimeSpan.FromHours(2));                                           // its lifetime is over
        var withdrawn = h.Grant("orders", host: host);
        Assert.True(host.Unregister(withdrawn));
        var ended = h.Grant("orders", host: host);
        using (var raw = new RawClient(host.Endpoint, ended))
        {
            raw.Start();
            raw.Client.End(new EndRequest { End = new TaskEndMessage { State = EndState.Succeeded } }, raw.Headers);
        }

        var live = h.Grant("orders", host: host);
        var methods = RunnerV1Reflection.Descriptor.Services[0].Methods;
        Assert.True(methods.Count >= 22, "the service lists every call of plan 28b section 9");

        var bad = new Dictionary<string, string?>
        {
            ["no token"] = null,
            ["an unknown token"] = "this-token-was-never-issued",
            ["an expired token"] = expired,
            ["a withdrawn token"] = withdrawn,
            ["the token of a run that ended"] = ended,
        };
        using var channel = host.Endpoint.CreateChannel();
        foreach (var (what, token) in bad)
        {
            var headers = new Metadata();
            if (token is not null) headers.Add(RunnerProtocolNames.TokenHeader, token);
            foreach (var method in methods)
                Assert.True(StatusCode.Unauthenticated == await CallAsync(channel, method, headers), $"{method.Name} with {what} must be refused as Unauthenticated");
        }

        // and the live token is not refused (so the loop above proves something): without Start it is a FailedPrecondition, never Unauthenticated
        foreach (var method in methods)
            Assert.NotEqual(StatusCode.Unauthenticated, await CallAsync(channel, method, new Metadata { { RunnerProtocolNames.TokenHeader, live } }));

        var refusal = await RefusedAsync(async () => await new RunnerApi.RunnerApiClient(channel).HeartbeatAsync(new HeartbeatRequest(), new Metadata { { RunnerProtocolNames.TokenHeader, ended } }));
        Assert.Contains("run has ended", refusal.Status.Detail, StringComparison.Ordinal);
        Assert.Contains("expired", (await RefusedAsync(async () => await new RunnerApi.RunnerApiClient(channel).HeartbeatAsync(new HeartbeatRequest(), new Metadata { { RunnerProtocolNames.TokenHeader, expired } }))).Status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task One_runs_token_reaches_nothing_of_another_run(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var tokenA = h.Grant("orders", chain: Chain());
        var tokenB = h.Grant("customers", chain: Chain());
        using var a = new RawClient(h.Host.Endpoint, tokenA);
        using var b = new RawClient(h.Host.Endpoint, tokenB);
        a.Start();
        b.Start();

        // run A uploads a payload and takes an inbox item
        var data = Encoding.UTF8.GetBytes("a's secret");
        var payload = await a.UploadAsync("make", "k", data);
        var item = h.Receive("orders", "billing", new Invoice("inv-a", 1));
        var taken = a.Client.TakeInbox(new TakeInboxRequest(), a.Headers);
        Assert.True(taken.HasItem);

        // run B cannot read the payload, complete a step with it, write it into its progress, or release A's inbox item
        Assert.Equal(StatusCode.PermissionDenied, (await RefusedAsync(async () => await b.Client.StatPayloadAsync(new PayloadRequest { PayloadId = payload }, b.Headers))).StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, (await RefusedAsync(async () =>
        {
            using var call = b.Client.ReadPayload(new PayloadRequest { PayloadId = payload }, b.Headers);
            await call.ResponseStream.MoveNext(default);
        })).StatusCode);
        b.Client.BeginStep(new BeginStepRequest { Name = "mine", Key = "k" }, b.Headers);
        Assert.Equal(StatusCode.PermissionDenied, (await RefusedAsync(async () => await b.Client.CompleteStepAsync(new CompleteStepRequest { Name = "mine", Key = "k", PayloadId = payload }, b.Headers))).StatusCode);
        var advance = new AdvanceRequest();
        var change = new V1CheckpointChange { Name = "stolen", ItemKey = "", ExpectedVersion = 0 };
        change.Parts["value"] = new ProgressValueMessage { Type = ProgressValueType.Bytes, Text = payload };
        advance.Checkpoints.Add(change);
        Assert.Equal(StatusCode.PermissionDenied, (await RefusedAsync(async () => await b.Client.AdvanceAsync(advance, b.Headers))).StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, (await RefusedAsync(async () => await b.Client.ReleaseInboxAsync(new InboxItemRef { ItemId = taken.ItemId }, b.Headers))).StatusCode);
        Assert.Equal(StatusCode.PermissionDenied, (await RefusedAsync(async () => await b.Client.CompleteInboxAsync(new InboxItemRef { ItemId = taken.ItemId }, b.Headers))).StatusCode);
        Assert.False(b.Client.TakeInbox(new TakeInboxRequest(), b.Headers).HasItem);       // customers has no inbox of its own, and A's item is not B's

        // A still has all of it, and what B did landed under B's own task and chain
        using (var call = a.Client.ReadPayload(new PayloadRequest { PayloadId = payload }, a.Headers))
        {
            Assert.True(await call.ResponseStream.MoveNext(default));
            Assert.Equal(data, call.ResponseStream.Current.Data.ToByteArray());
        }

        a.Client.ReleaseInbox(new InboxItemRef { ItemId = taken.ItemId }, a.Headers);
        Assert.Equal(1, h.ProgressOf("orders").InboxWaiting());
        Assert.NotEqual(Guid.Empty, item);
        Assert.Null(h.StepsOf("orders").Get(h.ChainOfToken(tokenA), "mine", "k"));
        Assert.Equal(StepState.Started, h.StepsOf("customers").List(h.ChainOfToken(tokenB)).Single().State);
        Assert.Null(h.ProgressOf("customers").GetCheckpoint("stolen"));
        Assert.Empty(b.Start().Progress.Checkpoints);
    }

    // ---- the first call, and the version

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_client_of_an_unsupported_protocol_version_is_refused_naming_the_version(string engine)
    {
        using var h = new Harness(engine, remote: true);
        using var raw = new RawClient(h.Host.Endpoint, h.Grant("orders"));

        var refusal = await RefusedAsync(async () => await raw.Client.StartAsync(new StartRequest { ProtocolVersion = "dbdatakit.runner.v9" }, raw.Headers));

        Assert.Equal(StatusCode.FailedPrecondition, refusal.StatusCode);
        Assert.Contains("dbdatakit.runner.v9", refusal.Status.Detail, StringComparison.Ordinal);
        Assert.Contains(RunnerProtocol.V1, refusal.Status.Detail, StringComparison.Ordinal);
        Assert.Empty(h.Runs.Read(new RunQuery("runner")));                              // a task refused at Start never began: no run record

        var early = await RefusedAsync(async () => await raw.Client.HeartbeatAsync(new HeartbeatRequest(), raw.Headers));
        Assert.Equal(StatusCode.FailedPrecondition, early.StatusCode);                  // nothing but Start works before Start
        Assert.Contains("Start has not been called", early.Status.Detail, StringComparison.Ordinal);
    }

    // ---- progress through the protocol

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task Progress_is_read_once_at_start_and_written_with_the_version_it_was_read_at(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var store = h.ProgressOf("orders");
        Assert.True(store.Advance("seen", new Dictionary<string, ProgressValue> { ["value"] = ProgressValue.OfInt(5) }, 0));
        var runId = h.Database.NewId();
        var token = h.Grant("orders", runId: runId);
        using var raw = new RawClient(h.Host.Endpoint, token);

        var info = raw.Start();
        var checkpoint = Assert.Single(info.Progress.Checkpoints);
        Assert.Equal(("seen", 1L, "5"), (checkpoint.Name, checkpoint.Version, checkpoint.Parts["value"].Text));
        Assert.Equal(runId.ToString("D"), info.RunId);
        Assert.Equal(RunnerProtocol.V1, info.ProtocolVersion);
        Assert.Equal(RunOutcome.Running, h.StoredRun(runId).Run.Outcome);

        // somebody else moves the checkpoint after the task read it: Start answers the same again (read once), and the task's write is refused
        Assert.True(store.Advance("seen", new Dictionary<string, ProgressValue> { ["value"] = ProgressValue.OfInt(6) }, 1, actor: "admin"));
        Assert.Equal(1, raw.Start().Progress.Checkpoints.Single().Version);

        AdvanceRequest Write(long expected, TaskEndMessage? end = null)
        {
            var request = new AdvanceRequest { End = end };
            var change = new V1CheckpointChange { Name = "seen", ItemKey = "", ExpectedVersion = expected };
            change.Parts["value"] = new ProgressValueMessage { Type = ProgressValueType.Int, Text = "9" };
            request.Checkpoints.Add(change);
            return request;
        }

        var succeeded = new TaskEndMessage { State = EndState.Succeeded, Processed = 1 };
        Assert.False(raw.Client.Advance(Write(1, succeeded), raw.Headers).Applied);     // refused, and the run is not finished
        Assert.Equal(RunOutcome.Running, h.StoredRun(runId).Run.Outcome);
        Assert.Equal(6, store.GetCheckpoint("seen")!.Parts["value"].AsInt);
        raw.Client.Heartbeat(new HeartbeatRequest(), raw.Headers);                      // the token still works: the run is still going

        // at the version the checkpoint has now, with the outcome: written and finished in one step, and the token is spent
        Assert.True(raw.Client.Advance(Write(2, succeeded), raw.Headers).Applied);
        Assert.Equal(9, store.GetCheckpoint("seen")!.Parts["value"].AsInt);
        Assert.Equal((RunOutcome.Succeeded, 1L), (h.StoredRun(runId).Run.Outcome, h.StoredRun(runId).Run.Rows));
        Assert.Equal(StatusCode.Unauthenticated, (await RefusedAsync(async () => await raw.Client.HeartbeatAsync(new HeartbeatRequest(), raw.Headers))).StatusCode);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task An_end_message_that_contradicts_itself_is_refused_and_the_run_goes_on(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var runId = h.Database.NewId();
        using var raw = new RawClient(h.Host.Endpoint, h.Grant("orders", runId: runId));
        raw.Start();

        var refusal = await RefusedAsync(async () => await raw.Client.EndAsync(new EndRequest { End = new TaskEndMessage { State = EndState.Failed } }, raw.Headers));   // failed, and says nothing of why

        Assert.Equal(StatusCode.InvalidArgument, refusal.StatusCode);
        Assert.Equal(RunOutcome.Running, h.StoredRun(runId).Run.Outcome);
        raw.Client.End(new EndRequest { End = new TaskEndMessage { State = EndState.Stopped } }, raw.Headers);
        Assert.Equal(RunOutcome.Stopped, h.StoredRun(runId).Run.Outcome);
    }

    // ---- streams

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task Twenty_mebibytes_cross_the_socket_in_chunks_of_at_most_one_mebibyte_both_ways(string engine)
    {
        using var h = new Harness(engine, remote: true);
        const long size = 20L * 1024 * 1024;

        var result = await h.Run<Big>(new BigInput(size));

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        var telemetry = h.LastTelemetry!;
        foreach (var (direction, stats) in new[] { ("uploaded", telemetry.Uploaded), ("downloaded", telemetry.Downloaded) })
        {
            Assert.Equal(size, stats.Bytes);
            Assert.True(stats.LargestChunk <= RunnerProtocolNames.MaxChunkBytes, $"{direction}: a chunk of {stats.LargestChunk} bytes is over 1 MiB");
            Assert.True(stats.Chunks >= size / RunnerProtocolNames.MaxChunkBytes, $"{direction}: {stats.Chunks} chunks for {size} bytes is not a stream of 1 MiB chunks");
        }

        Assert.Equal(RunnerProtocolNames.MaxChunkBytes, telemetry.Uploaded.LargestChunk);   // the kit sends full 1 MiB chunks (it was written in 64 KiB pieces)
        Assert.Equal(size, h.World.ReadBack!.Total);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task The_runner_refuses_a_chunk_over_one_mebibyte_and_an_upload_nobody_begun(string engine)
    {
        using var h = new Harness(engine, remote: true);
        using var raw = new RawClient(h.Host.Endpoint, h.Grant("orders"));
        raw.Start();

        async Task<RpcException> Upload(string step, int chunkBytes)
        {
            using var call = raw.Client.StepChunk(raw.Headers);
            return await RefusedAsync(async () =>
            {
                try
                {
                    await call.RequestStream.WriteAsync(new StepChunkRequest { Header = new StepChunkHeader { Name = step, Key = "k" } });
                    await call.RequestStream.WriteAsync(new StepChunkRequest { Data = ByteString.CopyFrom(new byte[chunkBytes]) });
                    await call.RequestStream.CompleteAsync();
                }
                catch (Exception ex) when (ex is not RpcException)
                {
                    // the runner has already answered: its status is the response
                }

                await call.ResponseAsync;
            });
        }

        var unbegun = await Upload("never-begun", 10);
        Assert.Equal(StatusCode.FailedPrecondition, unbegun.StatusCode);
        Assert.Contains("never-begun", unbegun.Status.Detail, StringComparison.Ordinal);

        raw.Client.BeginStep(new BeginStepRequest { Name = "big", Key = "k" }, raw.Headers);
        var oversized = await Upload("big", RunnerProtocolNames.MaxChunkBytes + 1);
        Assert.Equal(StatusCode.InvalidArgument, oversized.StatusCode);
        Assert.Contains("1 MiB", oversized.Status.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task An_upload_that_is_not_completed_keeps_no_payload(string engine)
    {
        using var h = new Harness(engine, remote: true);
        using var raw = new RawClient(h.Host.Endpoint, h.Grant("orders"));
        raw.Start();
        raw.Client.BeginStep(new BeginStepRequest { Name = "make", Key = "k" }, raw.Headers);

        using var call = raw.Client.StepChunk(raw.Headers);
        await call.RequestStream.WriteAsync(new StepChunkRequest { Header = new StepChunkHeader { Name = "make", Key = "k" } });
        await call.RequestStream.WriteAsync(new StepChunkRequest { Data = ByteString.CopyFrom(new byte[1000]) });
        await call.RequestStream.CompleteAsync();                                       // the body threw: no complete message
        var reply = await call.ResponseAsync;

        Assert.False(reply.Completed);
        Assert.Equal("", reply.PayloadId);
        Assert.Equal(RunOutcome.Running, Assert.Single(h.Runs.Read(new RunQuery("runner"))).Run.Outcome);
    }

    // ---- one-way messages

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task Counts_the_status_line_lineage_and_log_lines_reach_the_runner(string engine)
    {
        using var h = new Harness(engine, remote: true);

        var result = await h.Run<Reporter>();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
        var telemetry = h.LastTelemetry!;
        Assert.Equal(7, telemetry.Counts["seen"]);                                      // 3 and 4, batched and added
        Assert.Equal("reported", telemetry.Status);                                     // the last status replaces the earlier ones
        var lineage = Assert.Single(telemetry.Lineage);
        Assert.Equal(["crm.orders"], lineage.Reads.ToArray());
        Assert.Equal(["warehouse.orders"], lineage.Writes.ToArray());
        Assert.True(telemetry.Started && telemetry.Ended);
        var lines = h.Runs.Logs(result.RunId).Select(l => l.Message).ToArray();
        Assert.Contains("hello from the reporter", lines);
        Assert.Contains(lines, l => l.StartsWith("Run ended Succeeded", StringComparison.Ordinal));
        Assert.True(Array.IndexOf(lines, "hello from the reporter") < lines.Length - 1);   // in order, the summary last
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public void A_unit_reports_its_progress_and_a_rejected_record_becomes_a_dead_letter_through_the_protocol(string engine)
    {
        using var h = new Harness(engine, remote: true);
        using var raw = new RawClient(h.Host.Endpoint, h.Grant("customers"));
        raw.Start();

        Assert.Equal(UnitDecision.Run, raw.Client.BeginUnit(new UnitKey { Name = "customer", Key = "c1" }, raw.Headers).Decision);
        var progress = new UnitProgressRequest { Name = "customer", Key = "c1" };
        progress.Counts["rows"] = 5;
        raw.Client.UnitProgress(progress, raw.Headers);
        var end = new EndUnitRequest { Name = "customer", Key = "c1", Outcome = UnitOutcome.Completed };
        end.Counts["rows"] = 9;
        raw.Client.EndUnit(end, raw.Headers);
        Assert.Equal(UnitDecision.Skip, raw.Client.BeginUnit(new UnitKey { Name = "customer", Key = "c1" }, raw.Headers).Decision);   // done in this chain: skipped
        Assert.Equal(UnitState.Completed, Assert.Single(h.UnitsOf("customers").List(h.ChainOfToken(h.LastToken!))).State);

        var id = h.Database.NewId();
        raw.Client.Reject(new RejectRequest { Source = "record:Row", Error = "no good", Body = ByteString.CopyFromUtf8("{}"), Id = id.ToString("D") }, raw.Headers);
        Assert.Equal("no good", Assert.Single(h.ProgressOf("customers").DeadLetters()).Error);
    }

    // ---- cancellation

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task The_runner_can_cancel_a_run_the_job_sees_its_token_fire_and_the_run_is_stopped(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var run = Task.Run(() => h.RunRemote<Waits>(null, 1, null, Chain(), null, CancellationToken.None));
        await h.World.JobStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var token = h.LastToken!;

        Assert.True(h.Host.Cancel(token, "an operator asked"));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(RunOutcome.Stopped, result.Outcome);
        Assert.Equal(TaskFailureKind.Cancelled, result.End.Failure!.Kind);
        Assert.False(result.End.Failure.Retryable);                                     // a cancel is never retried
        Assert.Equal(RunOutcome.Stopped, h.StoredRun(result.RunId).Run.Outcome);
        Assert.Equal("an operator asked", h.LastTelemetry!.CancelReason);
        Assert.False(h.Host.Cancel(token));                                             // the token is spent
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_job_that_ignores_the_cancel_is_ended_by_the_task_when_the_grace_period_is_over(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var run = Task.Run(() => h.RunRemote<Stubborn>(null, 1, null, Chain(), null, CancellationToken.None, grace: TimeSpan.FromMilliseconds(300)));
        await h.World.JobStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var token = h.LastToken!;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        Assert.True(h.Host.Cancel(token));
        var result = await run.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3.5), "the task did not wait for the job: " + clock.Elapsed);
        Assert.Equal(RunOutcome.Stopped, result.Outcome);
        Assert.Contains("grace period", result.End.Failure!.Message, StringComparison.Ordinal);
        Assert.Equal(RunOutcome.Stopped, h.StoredRun(result.RunId).Run.Outcome);
    }

    [Theory]
    [MemberData(nameof(KitEngines.Engines), MemberType = typeof(KitEngines))]
    public async Task A_cancel_reaches_a_task_that_polls_with_its_heartbeat_and_one_that_watches_with_a_stream(string engine)
    {
        using var h = new Harness(engine, remote: true);
        var token = h.Grant("orders", grace: TimeSpan.FromSeconds(7));
        using var raw = new RawClient(h.Host.Endpoint, token);
        var info = raw.Start();
        Assert.Equal(7000, info.CancellationGraceMs);                                   // the grace period travels in Start

        Assert.False(raw.Client.Heartbeat(new HeartbeatRequest(), raw.Headers).CancelRequested);
        Assert.NotNull(h.Host.TelemetryOf(token)!.LastHeartbeat);

        using var watch = raw.Client.WatchCancel(new WatchCancelRequest(), raw.Headers);
        var notice = watch.ResponseStream.MoveNext(default);
        await Task.Delay(100);
        Assert.False(notice.IsCompleted);                                               // nothing is pushed until the runner cancels
        Assert.True(h.Host.Cancel(token, "enough"));

        Assert.True(await notice.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(("enough", 7000L), (watch.ResponseStream.Current.Reason, watch.ResponseStream.Current.GraceMs));
        var beat = raw.Client.Heartbeat(new HeartbeatRequest(), raw.Headers);
        Assert.Equal((true, "enough"), (beat.CancelRequested, beat.CancelReason));
    }

    // ---- the host

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task The_socket_directory_and_the_socket_allow_their_owner_only_and_nothing_listens_on_tcp()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "File modes of a Unix socket are checked on Linux; Windows uses a named pipe that only the current user may open.");
        await using var host = await RunnerProtocolHost.StartAsync();

        var socket = host.Endpoint.Address;
        Assert.Equal(RunnerEndpointKind.UnixSocket, host.Endpoint.Kind);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.GetDirectoryName(socket)!));   // 0700
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socket));                                                       // 0600
        Assert.All(host.ListeningOn, address => Assert.StartsWith("http://unix:", address, StringComparison.Ordinal));
        Assert.NotEmpty(host.ListeningOn);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task A_socket_directory_that_others_can_enter_is_refused_naming_it_and_a_missing_one_is_made_private()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "File modes are checked on Linux.");
        var root = Directory.CreateTempSubdirectory("dbdatakit-sock-");
        try
        {
            var open = Path.Combine(root.FullName, "open");
            Directory.CreateDirectory(open);
            File.SetUnixFileMode(open, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => RunnerProtocolHost.StartAsync(new RunnerProtocolHostOptions { Directory = open }));
            Assert.Contains(open, ex.Message, StringComparison.Ordinal);
            Assert.Contains("0700", ex.Message, StringComparison.Ordinal);

            var fresh = Path.Combine(root.FullName, "fresh");
            await using (var host = await RunnerProtocolHost.StartAsync(new RunnerProtocolHostOptions { Directory = fresh }))
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(fresh));
                Assert.Equal(Path.Combine(fresh, "runner.sock"), host.Endpoint.Address);
            }
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task The_jobs_run_over_a_named_pipe_on_Windows()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Named pipes are Windows only; on Windows every test of SampleJobsOverTheProtocolTests runs over the pipe, and this one names it.");
        using var h = new Harness(StateEngineIds.Sqlite, remote: true);
        Assert.Equal(RunnerEndpointKind.NamedPipe, h.Host.Endpoint.Kind);
        h.World.OrderPages.Add(new Page([new Row("1", 1)], DateTimeOffset.Parse("2026-11-01T00:00:00Z")));

        var result = await h.Run<Orders>();

        Assert.Equal(RunOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public void An_endpoint_is_written_and_read_as_unix_or_npipe_and_never_as_a_tcp_address()
    {
        Assert.Equal(RunnerEndpoint.Unix("/run/dbdatakit/runner.sock"), RunnerEndpoint.Parse("unix:/run/dbdatakit/runner.sock"));
        Assert.Equal(RunnerEndpoint.Pipe("dbdatakit-runner"), RunnerEndpoint.Parse("npipe:dbdatakit-runner"));
        Assert.Equal("npipe:x", RunnerEndpoint.Pipe("x").ToString());
        Assert.Contains("unix:", Assert.Throws<FormatException>(() => RunnerEndpoint.Parse("http://127.0.0.1:5000")).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_is_granted_only_when_its_start_message_and_environment_agree()
    {
        using var h = new Harness(StateEngineIds.Sqlite, remote: true);
        var start = new TaskStart(new TaskRef("another-package", Harness.Channel, "orders"), Guid.NewGuid(), 1, "1.0", new Dictionary<string, string>(), new Dictionary<string, string>(), null, TimeSpan.FromSeconds(5));

        var ex = Assert.Throws<ArgumentException>(() => h.Host.Register(new RunGrant(start, Chain(), h.Environment())));

        Assert.Contains("another-package/prod/orders", ex.Message, StringComparison.Ordinal);
        Assert.Contains("kit-tests/prod", ex.Message, StringComparison.Ordinal);
        var other = start with { Task = new TaskRef(Harness.Package, Harness.Channel, "orders"), ProtocolVersion = "dbdatakit.runner.v7" };
        Assert.Contains("dbdatakit.runner.v7", Assert.Throws<ArgumentException>(() => h.Host.Register(new RunGrant(other, Chain(), h.Environment()))).Message, StringComparison.Ordinal);
    }

    // ---- the inputs, as strings

    [Fact]
    public void Inputs_cross_the_wire_as_strings_and_come_back_as_the_typed_record()
    {
        var input = new AllInputs("x", 5, 1L << 40, 12.50m, true, new DateOnly(2026, 11, 7), DateTimeOffset.Parse("2026-11-07T10:00:00Z"), Guid.Parse("0197f0e0-0000-7000-8000-00000000c0de"), Flavor.Spicy, [1, 2, 3], Wait: TimeSpan.FromMinutes(2));

        var strings = InputBinder.ToStrings(input);

        Assert.Equal("2026-11-07", strings["day"]);
        Assert.Equal("5", strings["count"]);
        Assert.Equal("true", strings["flag"]);
        Assert.Equal("Spicy", strings["flavor"]);
        Assert.Equal("[1,2,3]", strings["numbers"]);
        Assert.False(strings.ContainsKey("note"));                                      // no value, no input
        var back = (AllInputs)InputBinder.FromStrings(typeof(AllInputs), strings);
        Assert.Equal(input.Name, back.Name);
        Assert.Equal((input.Count, input.Big, input.Price, input.Flag, input.Day, input.At, input.Id, input.Flavor, input.Wait), (back.Count, back.Big, back.Price, back.Flag, back.Day, back.At, back.Id, back.Flavor, back.Wait));
        Assert.Equal(input.Numbers, back.Numbers);
        Assert.Equal((null, 3), (back.Note, back.Retries));                             // a default in the record applies
    }

    [Fact]
    public void An_input_that_is_missing_unknown_or_of_the_wrong_form_is_refused_naming_it()
    {
        var all = InputBinder.ToStrings(new AllInputs("x", 5, 1, 1m, true, new DateOnly(2026, 11, 7), DateTimeOffset.UnixEpoch, Guid.Empty, Flavor.Plain, []));
        var without = all.Where(p => p.Key != "day").ToDictionary(p => p.Key, p => p.Value);
        Assert.Contains("'day'", Assert.Throws<ArgumentException>(() => InputBinder.FromStrings(typeof(AllInputs), without)).Message, StringComparison.Ordinal);

        var extra = new Dictionary<string, string>(all) { ["colour"] = "red" };
        Assert.Contains("'colour'", Assert.Throws<ArgumentException>(() => InputBinder.FromStrings(typeof(AllInputs), extra)).Message, StringComparison.Ordinal);

        var wrong = new Dictionary<string, string>(all) { ["day"] = "next tuesday" };
        Assert.Contains("'day'", Assert.Throws<ArgumentException>(() => InputBinder.FromStrings(typeof(AllInputs), wrong)).Message, StringComparison.Ordinal);

        var flavor = new Dictionary<string, string>(all) { ["flavor"] = "Sour" };
        Assert.Contains("'flavor'", Assert.Throws<ArgumentException>(() => InputBinder.FromStrings(typeof(AllInputs), flavor)).Message, StringComparison.Ordinal);

        Assert.Contains("declares no inputs", Assert.Throws<ArgumentException>(() => InputBinder.FromStrings(typeof(NoInput), new Dictionary<string, string> { ["a"] = "b" })).Message, StringComparison.Ordinal);
    }
}
