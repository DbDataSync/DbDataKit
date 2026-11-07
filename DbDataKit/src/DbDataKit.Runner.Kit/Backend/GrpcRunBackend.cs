using DbDataKit.Runner.Protocol;
using DbDataKit.Runner.Protocol.V1;
using DbDataKit.State;
using Google.Protobuf;
using Grpc.Core;

namespace DbDataKit.Runner.Kit;

/// <summary>A call to the runner was refused or broke the protocol (an unknown or expired run token, a call the run may not make). Not retryable: retrying the same call repeats the refusal.</summary>
public sealed class RunnerProtocolException(StatusCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    /// <summary>The gRPC status the runner answered with.</summary>
    public StatusCode Code { get; } = code;
}

/// <summary>
/// The run behind the protocol (plan 28a R03b): every <see cref="IRunBackend"/> call is a message to the runner over a local socket, so the task's process never opens the runner's state (DR5). The progress is the
/// snapshot the runner sent at start and changes are written with the version they were read at; steps, units, files, inbox and payloads are calls of the same names; counts, the status line, log lines and lineage
/// are batched and sent one way. The kit's logic above this class is the same as above <see cref="InProcessRun"/>.
/// </summary>
internal sealed class GrpcRunBackend : IRunBackend, IAsyncDisposable
{
    private const int MaxBatchedLines = 256;
    private static readonly TimeSpan FlushEvery = TimeSpan.FromMilliseconds(250);

    private readonly RunnerApi.RunnerApiClient _client;
    private readonly Metadata _headers;
    private readonly Dictionary<(string Name, string Key), Checkpoint> _checkpoints = [];
    private readonly Dictionary<(string Name, string Key), (ProgressValue Value, long Version)> _states = [];

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _counts = new(StringComparer.Ordinal);
    private readonly List<LogLine> _logs = [];
    private readonly List<LineageRecord> _lineage = [];
    private string? _status;
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _flusher;
    private int _flushQueued;

    public GrpcRunBackend(RunnerApi.RunnerApiClient client, Metadata headers, StartInfo info)
    {
        _client = client;
        _headers = headers;
        Identity = new RunIdentity(WireConvert.ParseId(info.RunId, "run id"), info.Attempt, info.ChainId, new TaskRef(info.Package, info.Channel, info.Task));
        foreach (var checkpoint in info.Progress?.Checkpoints ?? [])
            _checkpoints[(checkpoint.Name, checkpoint.ItemKey)] = new Checkpoint(checkpoint.Name, checkpoint.ItemKey, checkpoint.Version, WireConvert.FromWire(checkpoint.Parts), DateTimeOffset.MinValue, null);
        foreach (var state in info.Progress?.States ?? [])
            _states[(state.Name, state.ItemKey)] = (WireConvert.FromWire(state.Value), state.Version);
        _flusher = Task.Run(FlushLoopAsync);
    }

    public RunIdentity Identity { get; }

    // ---- calls

    private static Exception Map(RpcException ex) => ex.StatusCode switch
    {
        StatusCode.Cancelled => new OperationCanceledException(ex.Status.Detail, ex),
        StatusCode.Unavailable or StatusCode.DeadlineExceeded => new IOException($"The runner could not be reached or stopped answering: {ex.Status.Detail}", ex),
        StatusCode.ResourceExhausted => new StepResultTooLargeException(ex.Status.Detail),
        StatusCode.FailedPrecondition => new InvalidOperationException(ex.Status.Detail, ex),
        StatusCode.InvalidArgument => new ArgumentException(ex.Status.Detail, ex),
        _ => new RunnerProtocolException(ex.StatusCode, ex.Status.Detail, ex),
    };

    private async Task<T> CallAsync<T>(Func<Metadata, Task<T>> call)
    {
        try
        {
            return await call(_headers).ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            throw Map(ex);
        }
    }

    private T Call<T>(Func<Metadata, T> call)
    {
        try
        {
            return call(_headers);
        }
        catch (RpcException ex)
        {
            throw Map(ex);
        }
    }

    // ---- start (the runner recorded the start when it answered Start)

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    // ---- progress

    public Task<Checkpoint?> ReadCheckpointAsync(string name, string itemKey, CancellationToken ct) =>
        Task.FromResult(_checkpoints.TryGetValue((name, itemKey), out var checkpoint) ? checkpoint : null);

    public Task<(ProgressValue Value, long Version)?> ReadStateAsync(string name, string itemKey, CancellationToken ct) =>
        Task.FromResult(_states.TryGetValue((name, itemKey), out var state) ? ((ProgressValue, long)?)state : null);

    public async Task<bool> CommitProgressAsync(IReadOnlyList<CheckpointWrite> checkpoints, IReadOnlyList<StateWrite> states, TaskEnd? end, CancellationToken ct)
    {
        var request = new AdvanceRequest();
        foreach (var write in checkpoints)
        {
            var change = new Protocol.V1.CheckpointChange { Name = write.Name, ItemKey = write.ItemKey, ExpectedVersion = write.ExpectedVersion };
            foreach (var (part, value) in write.Parts) change.Parts[part] = WireConvert.ToWire(value);
            request.Checkpoints.Add(change);
        }

        foreach (var write in states)
            request.States.Add(new StateChange { Name = write.Name, ItemKey = write.ItemKey, Value = WireConvert.ToWire(write.Value), ExpectedVersion = write.ExpectedVersion });
        if (end is not null)
        {
            request.End = WireConvert.ToWire(end);
            await FlushAsync(ct).ConfigureAwait(false);      // the run's token stops working when it ends: what was reported goes first
        }

        var reply = await CallAsync(h => _client.AdvanceAsync(request, h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return reply.Applied;
    }

    public async Task FinishAsync(TaskEnd end, CancellationToken ct)
    {
        await FlushAsync(ct).ConfigureAwait(false);
        await CallAsync(h => _client.EndAsync(new EndRequest { End = WireConvert.ToWire(end) }, h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
    }

    // ---- steps

    private static StepRecord RecordOf(StepRecordMessage message, string name, string key, string state) => new(
        WireConvert.ParseId(message.StepId, "step id"), "", name, key, state, message.Attempt, message.ResultJson.Length == 0 ? null : message.ResultJson,
        message.ResultPayload.Length == 0 ? null : WireConvert.ParseId(message.ResultPayload, "payload id"), new Dictionary<string, long>(message.Counts, StringComparer.Ordinal),
        DateTimeOffset.MinValue, null, null, 0);

    public async Task<StepBegin> BeginStepAsync(string name, string key, CancellationToken ct)
    {
        var reply = await CallAsync(h => _client.BeginStepAsync(new BeginStepRequest { Name = name, Key = key }, h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return reply.OutcomeCase switch
        {
            BeginStepReply.OutcomeOneofCase.Reuse => new StepBegin.Reuse(RecordOf(reply.Reuse, name, key, StepState.Completed)),
            BeginStepReply.OutcomeOneofCase.Run => new StepBegin.Run(WireConvert.ParseId(reply.Run.StepId, "step id")),
            BeginStepReply.OutcomeOneofCase.InDoubt => new StepBegin.InDoubt(RecordOf(reply.InDoubt, name, key, StepState.InDoubt)),
            _ => throw new RunnerProtocolException(StatusCode.Internal, $"The runner answered BeginStep for '{name}' with something this client does not know."),
        };
    }

    public async Task<StepBegin.Run> RetakeStepAsync(string name, string key, CancellationToken ct)
    {
        var reply = await CallAsync(h => _client.RetakeStepAsync(new StepKey { Name = name, Key = key }, h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return new StepBegin.Run(WireConvert.ParseId(reply.Run.StepId, "step id"));
    }

    public async Task CompleteValueStepAsync(string name, string key, string resultJson, IReadOnlyDictionary<string, long>? counts, CancellationToken ct)
    {
        var request = new CompleteStepRequest { Name = name, Key = key, ResultJson = resultJson };
        WireConvert.AddTo(request.Counts, counts);
        await CallAsync(h => _client.CompleteStepAsync(request, h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
    }

    public async Task CompleteStreamStepAsync(string name, string key, Guid payload, IReadOnlyDictionary<string, long>? counts, CancellationToken ct)
    {
        var request = new CompleteStepRequest { Name = name, Key = key, PayloadId = payload.ToString("D") };
        WireConvert.AddTo(request.Counts, counts);
        await CallAsync(h => _client.CompleteStepAsync(request, h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
    }

    public async Task FailStepAsync(string name, string key, string error, IReadOnlyDictionary<string, long>? counts, CancellationToken ct)
    {
        var request = new FailStepRequest { Name = name, Key = key, Error = error ?? "" };
        WireConvert.AddTo(request.Counts, counts);
        await CallAsync(h => _client.FailStepAsync(request, h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
    }

    public async Task<StepStream> OpenStepStreamAsync(StepBegin.Run run, string stepName, string key, string? mediaType, CancellationToken ct)
    {
        // the runner knows the step by its name and key (it remembers the step id it gave out at BeginStep), so the header carries those and not the id
        var upload = new RemoteStepUpload(_client.StepChunk(_headers, cancellationToken: ct));
        await upload.OpenAsync(stepName, key, mediaType).ConfigureAwait(false);
        return new StepStream(upload, () => upload.PayloadId, upload.MarkComplete);
    }

    // ---- units

    public async Task<UnitBegin> BeginUnitAsync(string name, string key, CancellationToken ct)
    {
        var reply = await CallAsync(h => _client.BeginUnitAsync(new UnitKey { Name = name, Key = key }, h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        return reply.Decision switch
        {
            UnitDecision.Run => UnitBegin.Run,
            UnitDecision.Skip => UnitBegin.Skip,
            _ => throw new RunnerProtocolException(StatusCode.Internal, $"The runner answered BeginUnit for '{name}' with a decision this client does not know."),
        };
    }

    private Task EndUnitAsync(string name, string key, UnitOutcome outcome, string? error, IReadOnlyDictionary<string, long>? counts, CancellationToken ct)
    {
        var request = new EndUnitRequest { Name = name, Key = key, Outcome = outcome, Error = error ?? "" };
        WireConvert.AddTo(request.Counts, counts);
        return CallAsync(h => _client.EndUnitAsync(request, h, cancellationToken: ct).ResponseAsync);
    }

    public Task CompleteUnitAsync(string name, string key, IReadOnlyDictionary<string, long>? counts, CancellationToken ct) => EndUnitAsync(name, key, UnitOutcome.Completed, null, counts, ct);

    public Task FailUnitAsync(string name, string key, string error, IReadOnlyDictionary<string, long>? counts, CancellationToken ct) => EndUnitAsync(name, key, UnitOutcome.Failed, error, counts, ct);

    public Task SkipUnitAsync(string name, string key, CancellationToken ct) => EndUnitAsync(name, key, UnitOutcome.Skipped, null, null, ct);

    // ---- files, inbox, dead letters

    public async Task<bool> ClaimFileAsync(string connection, string path, long size, DateTimeOffset modified, string hash, CancellationToken ct)
    {
        var request = new ClaimFileRequest { Connection = connection, Path = path ?? "", Size = size, ModifiedUnixMs = modified.ToUnixTimeMilliseconds(), Hash = hash };
        return (await CallAsync(h => _client.ClaimFileAsync(request, h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false)).AlreadyProcessed;
    }

    public Task MarkFileProcessedAsync(string connection, string hash, string outcome, CancellationToken ct) =>
        CallAsync(h => _client.CompleteFileAsync(new CompleteFileRequest { Connection = connection, Hash = hash, Outcome = outcome ?? "" }, h, cancellationToken: ct).ResponseAsync);

    public async Task<InboxTaken?> TakeInboxAsync(CancellationToken ct)
    {
        var reply = await CallAsync(h => _client.TakeInboxAsync(new TakeInboxRequest(), h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        if (!reply.HasItem) return null;
        return new InboxTaken(WireConvert.ParseId(reply.ItemId, "inbox item id"), reply.Source, reply.SignatureOk, reply.BodyPayloadId.Length == 0 ? null : WireConvert.ParseId(reply.BodyPayloadId, "payload id"));
    }

    public Task CompleteInboxAsync(Guid id, CancellationToken ct) =>
        CallAsync(h => _client.CompleteInboxAsync(new InboxItemRef { ItemId = id.ToString("D") }, h, cancellationToken: ct).ResponseAsync);

    public Task ReleaseInboxAsync(Guid id, CancellationToken ct) =>
        CallAsync(h => _client.ReleaseInboxAsync(new InboxItemRef { ItemId = id.ToString("D") }, h, cancellationToken: ct).ResponseAsync);

    public Task AddDeadLetterAsync(string source, string error, byte[] body, Guid id, CancellationToken ct) =>
        CallAsync(h => _client.RejectAsync(new RejectRequest { Source = source, Error = error ?? "", Body = ByteString.CopyFrom(body), Id = id.ToString("D") }, h, cancellationToken: ct).ResponseAsync);

    // ---- payloads

    public PayloadRef GetPayload(Guid id)
    {
        var info = Call(h => _client.StatPayload(new PayloadRequest { PayloadId = id.ToString("D") }, h));
        return new PayloadRef(id, info.Size, info.Sha256.Length == 0 ? null : info.Sha256, info.MediaType.Length == 0 ? null : info.MediaType, OpenPayload);
    }

    private Stream OpenPayload(Guid id) => new RemotePayloadStream(_client.ReadPayload(new PayloadRequest { PayloadId = id.ToString("D") }, _headers));

    // ---- one-way: counts, status, log, lineage

    public void Log(string level, string message, string? item = null)
    {
        int pending;
        lock (_gate)
        {
            _logs.Add(new LogLine { Level = level, Message = message, Item = item ?? "", AtUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
            pending = _logs.Count;
        }

        if (pending >= MaxBatchedLines) QueueFlush();
    }

    public void Count(string name, long n)
    {
        lock (_gate) _counts[name] = _counts.GetValueOrDefault(name) + n;
    }

    public void Status(string text)
    {
        lock (_gate) _status = text;
    }

    public void Lineage(IReadOnlyList<string> reads, IReadOnlyList<string> writes)
    {
        var record = new LineageRecord();
        record.Reads.AddRange(reads);
        record.Writes.AddRange(writes);
        lock (_gate) _lineage.Add(record);
    }

    private void QueueFlush()
    {
        if (Interlocked.Exchange(ref _flushQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // a log line that cannot be sent does not fail the job; the next call that matters reports the broken connection
            }
            finally
            {
                Interlocked.Exchange(ref _flushQueued, 0);
            }
        });
    }

    private async Task FlushLoopAsync()
    {
        using var timer = new PeriodicTimer(FlushEvery);
        try
        {
            while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false))
            {
                try
                {
                    await FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // see QueueFlush
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>Sends what was counted, said and logged since the last flush, in order.</summary>
    public async Task FlushAsync(CancellationToken ct)
    {
        await _flushLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var batch = new ReportBatch();
            lock (_gate)
            {
                if (_counts.Count == 0 && _logs.Count == 0 && _lineage.Count == 0 && _status is null) return;
                foreach (var (name, n) in _counts) batch.Counts[name] = n;
                batch.Status = _status ?? "";
                batch.Logs.AddRange(_logs);
                batch.Lineage.AddRange(_lineage);
                _counts.Clear();
                _logs.Clear();
                _lineage.Clear();
                _status = null;
            }

            await CallAsync(h => _client.ReportAsync(batch, h, cancellationToken: ct).ResponseAsync).ConfigureAwait(false);
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _flusher.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // nothing to report from a flusher that stopped
        }

        _stop.Dispose();
    }

    // ---- the client's streams

    /// <summary>The write side of a streaming step: one chunk of at most 1 MiB in memory, sent as it fills. Complete the upload, then dispose it: a stream disposed without being completed abandons the upload and the runner keeps nothing.</summary>
    private sealed class RemoteStepUpload(AsyncClientStreamingCall<StepChunkRequest, StepChunkReply> call) : Stream
    {
        private readonly byte[] _buffer = new byte[RunnerProtocolNames.MaxChunkBytes];
        private int _used;
        private bool _complete;
        private bool _disposed;

        public Guid PayloadId { get; private set; }

        public async Task OpenAsync(string name, string key, string? mediaType) =>
            await SendAsync(new StepChunkRequest { Header = new StepChunkHeader { Name = name, Key = key, MediaType = mediaType ?? "" } }).ConfigureAwait(false);

        public void MarkComplete() => _complete = true;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => !_disposed;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        private async Task SendAsync(StepChunkRequest message)
        {
            try
            {
                await call.RequestStream.WriteAsync(message).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not RpcException)
            {
                // the runner ended the call early: its status says why
                try
                {
                    await call.ResponseAsync.ConfigureAwait(false);
                }
                catch (RpcException rpc)
                {
                    throw MapUpload(rpc);
                }

                throw;
            }
            catch (RpcException rpc)
            {
                throw MapUpload(rpc);
            }
        }

        private static Exception MapUpload(RpcException rpc) => rpc.StatusCode == StatusCode.ResourceExhausted
            ? new PayloadTooLargeException(rpc.Status.Detail, Guid.Empty, 0)
            : Map(rpc);

        private async Task FlushChunkAsync()
        {
            if (_used == 0) return;
            var data = ByteString.CopyFrom(_buffer, 0, _used);
            _used = 0;
            await SendAsync(new StepChunkRequest { Data = data }).ConfigureAwait(false);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            while (!buffer.IsEmpty)
            {
                var take = Math.Min(buffer.Length, _buffer.Length - _used);
                buffer.Span[..take].CopyTo(_buffer.AsSpan(_used));
                _used += take;
                buffer = buffer[take..];
                if (_used == _buffer.Length) await FlushChunkAsync().ConfigureAwait(false);
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Write(ReadOnlySpan<byte> buffer) => WriteAsync(buffer.ToArray()).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        public override async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_complete)
                {
                    await FlushChunkAsync().ConfigureAwait(false);
                    await SendAsync(new StepChunkRequest { Complete = true }).ConfigureAwait(false);
                }

                await call.RequestStream.CompleteAsync().ConfigureAwait(false);
                var reply = await call.ResponseAsync.ConfigureAwait(false);
                if (_complete && reply.Completed) PayloadId = WireConvert.ParseId(reply.PayloadId, "payload id");
            }
            catch (RpcException rpc) when (_complete)
            {
                throw MapUpload(rpc);
            }
            catch (RpcException)
            {
                // an abandoned upload: whatever the runner answered, nothing is kept
            }
            finally
            {
                call.Dispose();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeAsync().AsTask().GetAwaiter().GetResult();
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>A forward-only read of a payload the runner streams, holding one chunk at a time; the runner checks size and SHA-256 as it reads the payload from its store, and a payload that fails the check ends the call with an error.</summary>
    private sealed class RemotePayloadStream(AsyncServerStreamingCall<PayloadChunk> call) : Stream
    {
        private ByteString _chunk = ByteString.Empty;
        private int _offset;
        private bool _done;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            while (_offset >= _chunk.Length)
            {
                if (_done) return 0;
                try
                {
                    if (!await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false))
                    {
                        _done = true;
                        return 0;
                    }
                }
                catch (RpcException ex)
                {
                    throw Map(ex);
                }

                _chunk = call.ResponseStream.Current.Data;
                _offset = 0;
            }

            var n = Math.Min(buffer.Length, _chunk.Length - _offset);
            _chunk.Span.Slice(_offset, n).CopyTo(buffer.Span);
            _offset += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(Span<byte> buffer)
        {
            var temp = new byte[buffer.Length];
            var n = ReadAsync(temp.AsMemory()).AsTask().GetAwaiter().GetResult();
            temp.AsSpan(0, n).CopyTo(buffer);
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override void Flush()
        {
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) call.Dispose();
            base.Dispose(disposing);
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
