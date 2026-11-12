using DbDataKit.Runner.Kit;
using DbDataKit.Runner.Protocol.V1;
using DbDataKit.State;
using DbDataKit.State.Tracking;
using Google.Protobuf;
using Grpc.Core;

namespace DbDataKit.Runner.Protocol.Server;

/// <summary>
/// The runner's side of <c>dbdatakit.runner.v1</c> (plan 28a R03b): each message is answered by the same backend logic as an in-process run (<c>InProcessRun</c> over the progress, step, unit, payload and run
/// stores), for the one run the call's token names (<see cref="RunTokenInterceptor"/>). A call can only reach that run's progress, steps, units, payloads, inbox items and log; an id it names that the run was not
/// handed (a payload, an inbox item, a step it did not begin) is refused. Exceptions of the stores become gRPC statuses the client turns back into the exceptions the kit's logic expects.
/// </summary>
internal sealed class RunnerProtocolService(RunSessions sessions) : RunnerApi.RunnerApiBase
{
    private const int MaxDeadLetterBytes = 16 * 1024 * 1024;

    // ---- plumbing

    private static RunSession Session(ServerCallContext context) => RunTokenInterceptor.SessionOf(context);

    private static RunSession Started(ServerCallContext context)
    {
        var session = Session(context);
        if (!session.Started) throw new RpcException(new Status(StatusCode.FailedPrecondition, "Start has not been called: the first call of a run is Start."));
        return session;
    }

    private static RpcException Refuse(StatusCode code, string message) => new(new Status(code, message));

    private static async Task<T> Guard<T>(Func<Task<T>> body)
    {
        try
        {
            return await body().ConfigureAwait(false);
        }
        catch (RpcException)
        {
            throw;
        }
        catch (WorkNotOwnedException ex)
        {
            throw Refuse(StatusCode.PermissionDenied, ex.Message);
        }
        catch (TrackingNotGrantedException ex)
        {
            throw Refuse(StatusCode.PermissionDenied, ex.Message);
        }
        catch (StepResultTooLargeException ex)
        {
            throw Refuse(StatusCode.ResourceExhausted, ex.Message);
        }
        catch (PayloadTooLargeException ex)
        {
            throw Refuse(StatusCode.ResourceExhausted, ex.Message);
        }
        catch (OperationCanceledException ex)
        {
            throw Refuse(StatusCode.Cancelled, ex.Message);
        }
        catch (ArgumentException ex)
        {
            throw Refuse(StatusCode.InvalidArgument, ex.Message);
        }
        catch (FormatException ex)
        {
            throw Refuse(StatusCode.InvalidArgument, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw Refuse(StatusCode.FailedPrecondition, ex.Message);
        }
        catch (Exception ex)
        {
            throw Refuse(StatusCode.Internal, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private sealed class Empty
    {
        public static readonly Empty Instance = new();
    }

    private static void Named(string name, string key, string what = "step")
    {
        if (string.IsNullOrWhiteSpace(name)) throw Refuse(StatusCode.InvalidArgument, $"The {what} has no name.");
        if (key is null) throw Refuse(StatusCode.InvalidArgument, $"The {what} '{name}' has no key.");
    }

    /// <summary>A payload id the run may use: one it uploaded or was handed. Anyone else's is refused, so a run cannot read another run's data by guessing an id.</summary>
    private static Guid Payload(RunSession session, string text)
    {
        var id = WireConvert.ParseId(text, "payload id");
        if (!session.Payloads.ContainsKey(id)) throw Refuse(StatusCode.PermissionDenied, $"The payload {id} is not one this run uploaded or was given.");
        return id;
    }

    private static void Grant(RunSession session, Guid? payload)
    {
        if (payload is { } id) session.Payloads.TryAdd(id, 0);
    }

    private static void GrantValues(RunSession session, IEnumerable<ProgressValue> values)
    {
        foreach (var value in values)
            if (value.Type == ProgressType.Bytes) session.Payloads.TryAdd(value.AsPayload, 0);
    }

    // ---- start and end

    public override Task<StartInfo> Start(StartRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Session(context);
        if (request.ProtocolVersion != RunnerProtocol.V1)
            throw Refuse(StatusCode.FailedPrecondition,
                $"This runner serves the protocol '{RunnerProtocol.V1}' and the task speaks '{request.ProtocolVersion}', which it cannot serve. Build the package against a kit that speaks '{RunnerProtocol.V1}'.");
        return await session.StartAsync(() => BuildStart(session), context.CancellationToken).ConfigureAwait(false);
    });

    private static StartInfo BuildStart(RunSession session)
    {
        var start = session.Grant.Start;
        var info = new StartInfo
        {
            ProtocolVersion = RunnerProtocol.V1,
            RunId = start.RunId.ToString("D"),
            Attempt = start.Attempt,
            ChainId = session.Identity.Chain,
            Package = start.Task.Package,
            Channel = start.Task.Channel,
            Task = start.Task.Task,
            PackageVersion = start.PackageVersion,
            CancellationGraceMs = (long)start.CancellationGrace.TotalMilliseconds,
            Progress = Snapshot(session),
        };
        foreach (var (name, value) in start.Inputs) info.Inputs[name] = value;
        foreach (var (slot, handle) in start.ConnectionHandles) info.ConnectionHandles[slot] = handle;
        return info;
    }

    /// <summary>The task's progress as it is now: every checkpoint and state value with its version. Read once; later writes name these versions (DR5).</summary>
    private static ProgressSnapshot Snapshot(RunSession session)
    {
        var snapshot = new ProgressSnapshot();
        var store = session.Progress;
        foreach (var name in store.CheckpointNames())
            foreach (var key in store.ItemKeys(name))
            {
                if (store.GetCheckpoint(name, key) is not { } checkpoint) continue;
                GrantValues(session, checkpoint.Parts.Values);
                var message = new CheckpointValue { Name = name, ItemKey = key, Version = checkpoint.Version };
                foreach (var (part, value) in checkpoint.Parts) message.Parts[part] = WireConvert.ToWire(value);
                snapshot.Checkpoints.Add(message);
            }

        foreach (var name in store.StateNames())
            foreach (var key in store.StateKeys(name))
            {
                if (store.ReadState(name, key) is not { } state) continue;
                GrantValues(session, [state.Value]);
                snapshot.States.Add(new StateValueMessage { Name = name, ItemKey = key, Version = state.Version, Value = WireConvert.ToWire(state.Value) });
            }

        return snapshot;
    }

    private static TaskEnd EndOf(RunSession session, TaskEndMessage? message)
    {
        if (message is null) throw Refuse(StatusCode.InvalidArgument, "The end message is missing.");
        TaskEnd end;
        try
        {
            end = WireConvert.FromWire(message);
        }
        catch (ArgumentException ex)
        {
            throw Refuse(StatusCode.InvalidArgument, ex.Message);
        }

        var problems = end.Problems();
        if (problems.Count > 0) throw Refuse(StatusCode.InvalidArgument, "The end message is not consistent: " + string.Join(" ", problems));
        foreach (var output in end.OutputsOrEmpty.Values)
            if (output.PayloadId is { } id) Payload(session, id);
        return end;
    }

    public override Task<EndReply> End(EndRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        var end = EndOf(session, request.End);
        // the run is finished by the host after the process has exited and the log is complete, when the host says so (a finished run has all its lines); otherwise here
        if (!session.Grant.HostFinishesRun) await session.Backend.FinishAsync(end, CancellationToken.None).ConfigureAwait(false);
        session.RecordEnd(end);
        ReleaseWork(session);
        Ended(session);
        return new EndReply();
    });

    /// <summary>What the run still holds of its owner's work goes back to the queue (plan 28c E11): a run that ended did not finish it.</summary>
    private static void ReleaseWork(RunSession session)
    {
        if (session.WorkUsed) session.Work.ReleaseAll();
    }

    /// <summary>The run is over: its token stops working now (after this reply, a call with it is refused).</summary>
    private void Ended(RunSession session) => sessions.Revoke(session.Token, "The run has ended, and its token no longer works.");

    // ---- progress

    public override Task<AdvanceReply> Advance(AdvanceRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        var checkpoints = new List<CheckpointWrite>();
        foreach (var change in request.Checkpoints)
        {
            Named(change.Name, change.ItemKey, "checkpoint");
            var parts = WireConvert.FromWire(change.Parts);
            ProgressBytesAllowed(session, parts.Values);
            checkpoints.Add(new CheckpointWrite(change.Name, change.ItemKey, parts, change.ExpectedVersion));
        }

        var states = new List<StateWrite>();
        foreach (var change in request.States)
        {
            Named(change.Name, change.ItemKey, "state value");
            if (change.Value is null) throw Refuse(StatusCode.InvalidArgument, $"The state value '{change.Name}' has no value.");
            var value = WireConvert.FromWire(change.Value);
            ProgressBytesAllowed(session, [value]);
            states.Add(new StateWrite(change.Name, change.ItemKey, value, change.ExpectedVersion));
        }

        var end = request.End is null ? null : EndOf(session, request.End);
        var applied = await session.Backend.CommitProgressAsync(checkpoints, states, end, context.CancellationToken).ConfigureAwait(false);
        if (applied && end is not null)
        {
            session.RecordEnd(end);
            ReleaseWork(session);
            Ended(session);
        }
        return new AdvanceReply { Applied = applied };
    });

    private static void ProgressBytesAllowed(RunSession session, IEnumerable<ProgressValue> values)
    {
        foreach (var value in values)
            if (value.Type == ProgressType.Bytes && !session.Payloads.ContainsKey(value.AsPayload))
                throw Refuse(StatusCode.PermissionDenied, $"The progress value names the payload {value.AsPayload}, which this run neither uploaded nor was given.");
    }

    // ---- steps

    private static StepRecordMessage ToWire(RunSession session, StepRecord record)
    {
        Grant(session, record.ResultPayload);
        var message = new StepRecordMessage
        {
            StepId = record.Id.ToString("D"),
            Attempt = record.Attempt,
            State = record.State,
            ResultJson = record.ResultJson ?? "",
            ResultPayload = record.ResultPayload?.ToString("D") ?? "",
        };
        WireConvert.AddTo(message.Counts, record.Counts);
        return message;
    }

    public override Task<BeginStepReply> BeginStep(BeginStepRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        Named(request.Name, request.Key);
        var begin = await session.Backend.BeginStepAsync(request.Name, request.Key, context.CancellationToken).ConfigureAwait(false);
        var reply = new BeginStepReply();
        switch (begin)
        {
            case StepBegin.Reuse reuse:
                reply.Reuse = ToWire(session, reuse.Step);
                break;
            case StepBegin.Run run:
                session.StepIds[(request.Name, request.Key)] = run.StepId;
                reply.Run = new StepRun { StepId = run.StepId.ToString("D") };
                break;
            case StepBegin.InDoubt doubt:
                reply.InDoubt = ToWire(session, doubt.Step);
                break;
        }

        return reply;
    });

    public override Task<RetakeStepReply> RetakeStep(StepKey request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        Named(request.Name, request.Key);
        var run = await session.Backend.RetakeStepAsync(request.Name, request.Key, context.CancellationToken).ConfigureAwait(false);
        session.StepIds[(request.Name, request.Key)] = run.StepId;
        return new RetakeStepReply { Run = new StepRun { StepId = run.StepId.ToString("D") } };
    });

    public override Task<AdoptStepPayloadReply> AdoptStepPayload(StepKey request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        Named(request.Name, request.Key);
        var payload = await session.Backend.AdoptStepPayloadAsync(request.Name, request.Key, context.CancellationToken).ConfigureAwait(false);
        return new AdoptStepPayloadReply { PayloadId = payload?.ToString("D") ?? "" };
    });

    public override Task<CompleteStepReply> CompleteStep(CompleteStepRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        Named(request.Name, request.Key);
        var counts = WireConvert.CountsFromWire(request.Counts);
        switch (request.ResultCase)
        {
            case CompleteStepRequest.ResultOneofCase.ResultJson:
                await session.Backend.CompleteValueStepAsync(request.Name, request.Key, request.ResultJson, counts, context.CancellationToken).ConfigureAwait(false);
                break;
            case CompleteStepRequest.ResultOneofCase.PayloadId:
                await session.Backend.CompleteStreamStepAsync(request.Name, request.Key, Payload(session, request.PayloadId), counts, context.CancellationToken).ConfigureAwait(false);
                break;
            default:
                throw Refuse(StatusCode.InvalidArgument, $"CompleteStep for '{request.Name}' carries neither a result nor a payload.");
        }

        return new CompleteStepReply();
    });

    public override Task<FailStepReply> FailStep(FailStepRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        Named(request.Name, request.Key);
        await session.Backend.FailStepAsync(request.Name, request.Key, request.Error, WireConvert.CountsFromWire(request.Counts), CancellationToken.None).ConfigureAwait(false);
        return new FailStepReply();
    });

    public override Task<StepChunkReply> StepChunk(IAsyncStreamReader<StepChunkRequest> requestStream, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        var ct = context.CancellationToken;
        if (!await requestStream.MoveNext(ct).ConfigureAwait(false) || requestStream.Current.PartCase != StepChunkRequest.PartOneofCase.Header)
            throw Refuse(StatusCode.InvalidArgument, "A step upload starts with its header (the step's name and key).");
        var header = requestStream.Current.Header;
        Named(header.Name, header.Key);
        if (!session.StepIds.TryGetValue((header.Name, header.Key), out var stepId))
            throw Refuse(StatusCode.FailedPrecondition, $"The step '{header.Name}' with key '{header.Key}' was not begun by this run (BeginStep comes first).");

        var stream = await session.Backend.OpenStepStreamAsync(new StepBegin.Run(stepId), header.Name, header.Key, header.MediaType.Length == 0 ? null : header.MediaType, ct).ConfigureAwait(false);
        var completed = false;
        try
        {
            while (await requestStream.MoveNext(ct).ConfigureAwait(false))
            {
                var part = requestStream.Current;
                if (completed) throw Refuse(StatusCode.InvalidArgument, "The upload sent more after its complete message.");
                switch (part.PartCase)
                {
                    case StepChunkRequest.PartOneofCase.Data:
                        if (part.Data.Length > RunnerProtocolNames.MaxChunkBytes)
                            throw Refuse(StatusCode.InvalidArgument, $"A chunk of {part.Data.Length} bytes is over the limit of {RunnerProtocolNames.MaxChunkBytes} (1 MiB).");
                        session.NoteChunk(upload: true, part.Data.Length);
                        await stream.Stream.WriteAsync(part.Data.Memory, ct).ConfigureAwait(false);
                        break;
                    case StepChunkRequest.PartOneofCase.Complete:
                        completed = part.Complete;
                        break;
                    default:
                        throw Refuse(StatusCode.InvalidArgument, "A step upload has its header first, then data, then complete.");
                }
            }

            if (completed) stream.Complete();
        }
        catch
        {
            await stream.Stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        await stream.Stream.DisposeAsync().ConfigureAwait(false);
        if (!completed) return new StepChunkReply { Completed = false };
        var id = stream.PayloadId();
        session.Payloads.TryAdd(id, 0);
        var info = session.Backend.GetPayload(id);
        return new StepChunkReply { Completed = true, PayloadId = id.ToString("D"), Size = info.Size, Sha256 = info.Sha256 ?? "" };
    });

    // ---- payloads

    public override Task<PayloadDescription> StatPayload(PayloadRequest request, ServerCallContext context) => Guard(() =>
    {
        var session = Started(context);
        var id = Payload(session, request.PayloadId);
        var info = session.Backend.GetPayload(id);
        return Task.FromResult(new PayloadDescription { PayloadId = id.ToString("D"), Size = info.Size, Sha256 = info.Sha256 ?? "", MediaType = info.MediaType ?? "" });
    });

    public override async Task ReadPayload(PayloadRequest request, IServerStreamWriter<PayloadChunk> responseStream, ServerCallContext context)
    {
        await Guard(async () =>
        {
            var session = Started(context);
            var id = Payload(session, request.PayloadId);
            await using var stream = session.Backend.GetPayload(id).OpenRead();
            var buffer = new byte[RunnerProtocolNames.MaxChunkBytes];
            int read;
            while ((read = await stream.ReadAsync(buffer, context.CancellationToken).ConfigureAwait(false)) > 0)
            {
                session.NoteChunk(upload: false, read);
                await responseStream.WriteAsync(new PayloadChunk { Data = ByteString.CopyFrom(buffer, 0, read) }, context.CancellationToken).ConfigureAwait(false);
            }

            return Empty.Instance;
        }).ConfigureAwait(false);
    }

    // ---- units

    public override Task<BeginUnitReply> BeginUnit(UnitKey request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        Named(request.Name, request.Key, "unit");
        var begin = await session.Backend.BeginUnitAsync(request.Name, request.Key, context.CancellationToken).ConfigureAwait(false);
        return new BeginUnitReply { Decision = begin == UnitBegin.Skip ? UnitDecision.Skip : UnitDecision.Run };
    });

    public override Task<UnitProgressReply> UnitProgress(UnitProgressRequest request, ServerCallContext context) => Guard(() =>
    {
        var session = Started(context);
        Named(request.Name, request.Key, "unit");
        session.SetUnitCounts(request.Name, request.Key, new Dictionary<string, long>(request.Counts, StringComparer.Ordinal));
        return Task.FromResult(new UnitProgressReply());
    });

    public override Task<EndUnitReply> EndUnit(EndUnitRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        Named(request.Name, request.Key, "unit");
        var counts = WireConvert.CountsFromWire(request.Counts);
        switch (request.Outcome)
        {
            case UnitOutcome.Completed:
                await session.Backend.CompleteUnitAsync(request.Name, request.Key, counts, context.CancellationToken).ConfigureAwait(false);
                break;
            case UnitOutcome.Failed:
                await session.Backend.FailUnitAsync(request.Name, request.Key, request.Error, counts, CancellationToken.None).ConfigureAwait(false);
                break;
            case UnitOutcome.Skipped:
                await session.Backend.SkipUnitAsync(request.Name, request.Key, CancellationToken.None).ConfigureAwait(false);
                break;
            default:
                throw Refuse(StatusCode.InvalidArgument, $"EndUnit for '{request.Name}' names no outcome.");
        }

        return new EndUnitReply();
    });

    // ---- files, inbox, dead letters

    public override Task<ClaimFileReply> ClaimFile(ClaimFileRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        if (string.IsNullOrWhiteSpace(request.Connection) || string.IsNullOrWhiteSpace(request.Hash)) throw Refuse(StatusCode.InvalidArgument, "A file claim names its connection and its content hash.");
        var seen = await session.Backend.ClaimFileAsync(request.Connection, request.Path, request.Size, DateTimeOffset.FromUnixTimeMilliseconds(request.ModifiedUnixMs), request.Hash, context.CancellationToken).ConfigureAwait(false);
        return new ClaimFileReply { AlreadyProcessed = seen };
    });

    public override Task<CompleteFileReply> CompleteFile(CompleteFileRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        await session.Backend.MarkFileProcessedAsync(request.Connection, request.Hash, request.Outcome, context.CancellationToken).ConfigureAwait(false);
        return new CompleteFileReply();
    });

    public override Task<TakeInboxReply> TakeInbox(TakeInboxRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        var item = await session.Backend.TakeInboxAsync(context.CancellationToken).ConfigureAwait(false);
        if (item is null) return new TakeInboxReply { HasItem = false };
        session.TakenInbox.TryAdd(item.Id, 0);
        Grant(session, item.Body);
        return new TakeInboxReply { HasItem = true, ItemId = item.Id.ToString("D"), Source = item.Source, SignatureOk = item.SignatureOk, BodyPayloadId = item.Body?.ToString("D") ?? "" };
    });

    private static Guid Taken(RunSession session, string text)
    {
        var id = WireConvert.ParseId(text, "inbox item id");
        if (!session.TakenInbox.ContainsKey(id)) throw Refuse(StatusCode.PermissionDenied, $"The inbox item {id} is not one this run took.");
        return id;
    }

    public override Task<InboxReply> CompleteInbox(InboxItemRef request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        var id = Taken(session, request.ItemId);
        await session.Backend.CompleteInboxAsync(id, context.CancellationToken).ConfigureAwait(false);
        session.TakenInbox.TryRemove(id, out _);
        return new InboxReply();
    });

    public override Task<InboxReply> ReleaseInbox(InboxItemRef request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        var id = Taken(session, request.ItemId);
        await session.Backend.ReleaseInboxAsync(id, CancellationToken.None).ConfigureAwait(false);
        session.TakenInbox.TryRemove(id, out _);
        return new InboxReply();
    });

    public override Task<RejectReply> Reject(RejectRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        if (request.Body.Length > MaxDeadLetterBytes) throw Refuse(StatusCode.InvalidArgument, $"A dead letter's body is at most {MaxDeadLetterBytes} bytes; this one is {request.Body.Length}.");
        var id = WireConvert.ParseId(request.Id, "dead letter id");
        await session.Backend.AddDeadLetterAsync(request.Source, request.Error, request.Body.ToByteArray(), id, context.CancellationToken).ConfigureAwait(false);
        session.TakenInbox.TryRemove(id, out _);      // a rejected item is finished: it is not released when the run ends
        return new RejectReply();
    });

    // ---- one-way

    public override Task<ReportReply> Report(ReportBatch request, ServerCallContext context) => Guard(() =>
    {
        var session = Started(context);
        foreach (var line in request.Logs) session.Backend.Log(line.Level.Length == 0 ? "Information" : line.Level, line.Message, line.Item.Length == 0 ? null : line.Item);
        session.Add(request);
        return Task.FromResult(new ReportReply());
    });

    // ---- liveness and cancellation

    public override Task<HeartbeatReply> Heartbeat(HeartbeatRequest request, ServerCallContext context) => Guard(() =>
    {
        var session = Started(context);
        session.LastHeartbeat = session.Time.GetUtcNow();
        var cancelled = session.CancelSignal.Task.IsCompleted;
        if (session.WorkUsed) session.Work.Renew();      // the claims of a run that is alive last another lease (plan 28c E11)
        return Task.FromResult(new HeartbeatReply { CancelRequested = cancelled, CancelReason = session.CancelReason ?? "" });
    });

    public override Task<ResolveConnectionReply> ResolveConnection(ResolveConnectionRequest request, ServerCallContext context) => Guard(async () =>
    {
        var session = Started(context);
        if (string.IsNullOrWhiteSpace(request.Slot)) throw Refuse(StatusCode.InvalidArgument, "The slot has no name.");
        var role = string.IsNullOrEmpty(request.Role) ? "default" : request.Role;
        if (role is not ("default" or "read" or "write")) throw Refuse(StatusCode.InvalidArgument, $"The login role '{role}' is not one of default, read or write.");
        // only a slot the run was started with: the handles are the channel's bindings as they were when the run began
        if (!session.Grant.Start.ConnectionHandles.TryGetValue(request.Slot, out var connection))
            throw Refuse(StatusCode.PermissionDenied, $"The slot '{request.Slot}' is not one this run was given: it is not declared by the task, or it has no binding in channel '{session.Grant.Start.Task.Channel}'.");
        var resolver = session.Grant.Slots ?? throw Refuse(StatusCode.FailedPrecondition, "This runner has no connection bindings to resolve a slot with.");
        ConnectionGrant grant;
        try
        {
            grant = await resolver.ResolveAsync(request.Slot, connection, role, context.CancellationToken).ConfigureAwait(false);
        }
        catch (SlotResolutionException ex)
        {
            throw Refuse(StatusCode.FailedPrecondition, ex.Message);
        }

        var reply = new ResolveConnectionReply { Connection = grant.Connection, Engine = grant.Engine, Role = grant.Role, ReadOnly = grant.ReadOnly, AuthMode = grant.AuthMode };
        if (grant.Host is not null) reply.Host = grant.Host;
        if (grant.Port is { } port) reply.Port = port;
        if (grant.Database is not null) reply.Database = grant.Database;
        if (grant.ConnectionString is not null) reply.ConnectionString = grant.ConnectionString;
        if (grant.User is not null) reply.User = grant.User;
        if (grant.Password is not null) reply.Password = grant.Password;
        if (grant.ConnectionStringSecret is not null) reply.ConnectionStringSecret = grant.ConnectionStringSecret;
        if (grant.Token is not null) reply.Token = grant.Token;
        if (grant.HttpDefinition is not null) reply.HttpDefinition = grant.HttpDefinition;
        foreach (var (reference, value) in grant.HttpSecrets) reply.HttpSecrets[reference] = value;
        return reply;
    });

    // ---- engine work (plan 28c E11): the owner's queue, for the owner this run's token names

    public override Task<EnqueueWorkReply> EnqueueWork(EnqueueWorkRequest request, ServerCallContext context) => Guard(() =>
    {
        var session = Started(context);
        var runId = session.Work.Enqueue(request.Kind, request.Subject, request.Part, request.Resource, request.Lane, request.Payload);
        return Task.FromResult(new EnqueueWorkReply { RunId = runId.ToString("D") });
    });

    public override Task<ClaimWorkReply> ClaimWork(ClaimWorkRequest request, ServerCallContext context) => Guard(() =>
    {
        var session = Started(context);
        if (session.CancelSignal.Task.IsCompleted) return Task.FromResult(new ClaimWorkReply());      // a run that was told to stop takes nothing more
        if (session.Work.Claim(request.Lane) is not { } item) return Task.FromResult(new ClaimWorkReply());
        return Task.FromResult(new ClaimWorkReply
        {
            HasItem = true, Id = item.Id, Kind = item.Kind, Subject = item.Subject, Part = item.Part, Resource = item.Resource, Lane = item.Lane, RunId = item.RunId.ToString("D"), Payload = item.Payload ?? "",
        });
    });

    public override Task<WorkReply> StartWork(WorkItemRef request, ServerCallContext context) => Guard(() =>
        Task.FromResult(new WorkReply { Applied = Started(context).Work.Start(request.Id) }));

    public override Task<WorkReply> FinishWork(FinishWorkRequest request, ServerCallContext context) => Guard(() =>
    {
        var outcome = request.Outcome switch
        {
            WorkOutcome.Done => WorkFinish.Done,
            WorkOutcome.Failed => WorkFinish.Failed,
            WorkOutcome.Cancelled => WorkFinish.Cancelled,
            _ => throw Refuse(StatusCode.InvalidArgument, "An item is finished as done, failed or cancelled."),
        };
        return Task.FromResult(new WorkReply { Applied = Started(context).Work.Finish(request.Id, outcome) });
    });

    public override Task<WorkReply> ReleaseWork(WorkItemRef request, ServerCallContext context) => Guard(() =>
        Task.FromResult(new WorkReply { Applied = Started(context).Work.Release(request.Id) }));

    public override Task<CancelRequestedReply> IsCancelRequested(WorkRunRef request, ServerCallContext context) => Guard(() =>
    {
        var session = Started(context);
        return Task.FromResult(new CancelRequestedReply { Requested = session.Work.IsCancelRequested(WireConvert.ParseId(request.RunId, "work run id")) });
    });

    public override Task<WorkStateReply> WorkState(WorkStateRequest request, ServerCallContext context) => Guard(() =>
    {
        var snapshot = Started(context).Work.State([.. request.Kinds]);
        var reply = new WorkStateReply { Outstanding = snapshot.Outstanding, Paused = snapshot.Paused };
        foreach (var (kind, subjects) in snapshot.Kinds)
        {
            var state = new WorkKindState { Kind = kind };
            foreach (var (subject, known) in subjects)
                state.Subjects.Add(new WorkSubjectState { Subject = subject, LastEnqueuedUnixMs = known.LastEnqueued.ToUnixTimeMilliseconds(), InFlight = known.InFlight });
            reply.Kinds.Add(state);
        }

        return Task.FromResult(reply);
    });

    public override async Task WatchCancel(WatchCancelRequest request, IServerStreamWriter<CancelNotice> responseStream, ServerCallContext context)
    {
        var session = Started(context);
        var gone = new TaskCompletionSource();
        await using var registration = context.CancellationToken.Register(() => gone.TrySetResult());
        var first = await Task.WhenAny(session.CancelSignal.Task, session.EndSignal.Task, gone.Task).ConfigureAwait(false);
        if (first == session.CancelSignal.Task)
            await responseStream.WriteAsync(new CancelNotice { Reason = session.CancelSignal.Task.Result, GraceMs = (long)session.Grant.Start.CancellationGrace.TotalMilliseconds }).ConfigureAwait(false);
        await Task.WhenAny(session.EndSignal.Task, gone.Task).ConfigureAwait(false);
    }

    // ---- Build's tracking copy (plan 28c E16): the runner's build_tracking module, for the identities of the connections this run holds

    private static TrackingPositionMessage? ToWire(TrackingPosition? p) => p is null ? null : new TrackingPositionMessage { Order = p.Order, Key = p.Key };

    private static TrackingPosition? FromWire(TrackingPositionMessage? p) => p is null ? null : new TrackingPosition(p.Order, p.Key);

    private static TrackingDivergenceMessage ToWire(TrackingDivergence d)
    {
        var message = new TrackingDivergenceMessage { Id = d.Id, Identity = d.Identity, Stream = d.Stream, Status = d.Status, Reason = d.Reason, DivergedAtUnixMs = d.DivergedAt.ToUnixTimeMilliseconds() };
        if (d.Common is not null) message.Common = ToWire(d.Common);
        if (d.PrimaryHigh is not null) message.PrimaryHigh = ToWire(d.PrimaryHigh);
        if (d.CopyHigh is not null) message.CopyHigh = ToWire(d.CopyHigh);
        return message;
    }

    private static IReadOnlyList<TrackingRow> Rows(IEnumerable<TrackingRowMessage> rows) => [.. rows.Select(r => new TrackingRow(r.Key, r.Order, r.Document))];

    public override Task<TrackingStateReply> TrackingState(TrackingStreamRef request, ServerCallContext context) => Guard(() =>
    {
        var state = Started(context).Tracking.State(request.Identity, request.Stream);
        var reply = new TrackingStateReply { Version = state.Version };
        if (state.Position is not null) reply.Position = ToWire(state.Position);
        if (state.Open is not null) reply.Open = ToWire(state.Open);
        return Task.FromResult(reply);
    });

    public override Task<TrackingWriteReply> TrackingWrite(TrackingWriteRequest request, ServerCallContext context) => Guard(() =>
        Task.FromResult(new TrackingWriteReply { Applied = Started(context).Tracking.Write(request.Identity, request.Stream, Rows(request.Rows), request.Advance, request.ExpectedVersion) }));

    public override Task<TrackingTailReply> TrackingTail(TrackingTailRequest request, ServerCallContext context) => Guard(() =>
    {
        var reply = new TrackingTailReply();
        reply.Positions.AddRange(Started(context).Tracking.Tail(request.Identity, request.Stream, request.Count).Select(p => ToWire(p)!));
        return Task.FromResult(reply);
    });

    public override Task<TrackingReadReply> TrackingRead(TrackingReadRequest request, ServerCallContext context) => Guard(() =>
    {
        var reply = new TrackingReadReply();
        reply.Rows.AddRange(Started(context).Tracking.Read(request.Identity, request.Stream, FromWire(request.After), request.Limit).Select(r => new TrackingRowMessage { Key = r.Key, Order = r.Order, Document = r.Document }));
        return Task.FromResult(reply);
    });

    public override Task<TrackingUnsettledReply> TrackingUnsettled(TrackingUnsettledRequest request, ServerCallContext context) => Guard(() =>
    {
        var reply = new TrackingUnsettledReply();
        reply.Rows.AddRange(Started(context).Tracking.Unsettled(request.Identity, request.Stream, request.Limit).Select(r => new TrackingUnsettledRow { Key = r.Key, Hash = r.Hash }));
        return Task.FromResult(reply);
    });

    public override Task<TrackingDivergenceReply> TrackingDivergence(TrackingDivergenceRequest request, ServerCallContext context) => Guard(() =>
        Task.FromResult(new TrackingDivergenceReply
        {
            Divergence = ToWire(Started(context).Tracking.Mark(request.Identity, request.Stream, request.Reason, FromWire(request.Common), FromWire(request.PrimaryHigh), FromWire(request.CopyHigh))),
        }));

    public override Task<TrackingSnapshotReply> TrackingSnapshot(TrackingSnapshotRequest request, ServerCallContext context) => Guard(() =>
        Task.FromResult(new TrackingSnapshotReply { Rows = Started(context).Tracking.ReplaceSnapshot(request.Identity, request.Table, Rows(request.Rows)) }));
}
