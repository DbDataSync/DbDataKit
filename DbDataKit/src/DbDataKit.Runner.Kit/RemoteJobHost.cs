using DbDataKit.Runner.Protocol;
using DbDataKit.Runner.Protocol.V1;
using Grpc.Core;

namespace DbDataKit.Runner.Kit;

/// <summary>What a remote run is given besides the endpoint and the token: the points where a test makes the process die, the cap on units at once (the task's concurrency limit, which the runner passes on), the way a slot is opened, and how often the run says it is alive.</summary>
public sealed class RemoteRunOptions
{
    /// <summary>Points where a test makes the process "die" (see <see cref="RunnerHooks"/>).</summary>
    public RunnerHooks? Hooks { get; init; }

    /// <summary>The most a unit loop may run at once, whatever the job asks; null: the job's own value.</summary>
    public int? MaxParallelism { get; init; }

    /// <summary>Opens the connection a slot is bound to (a connection handle's name goes to the runner, which resolves it; this is the task side of that).</summary>
    public SlotOpener? SlotOpener { get; init; }

    /// <summary>How often the task sends a heartbeat (and learns of a cancel it was not pushed). Default 5 seconds.</summary>
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Who is speaking, for the runner's log.</summary>
    public string Client { get; init; } = "DbDataKit.Runner.Kit";
}

/// <summary>
/// Runs a job in a task process against its runner (plan 28a R03b): connects to the runner's local socket with the run token, reads <c>Start</c> (the run, its inputs as strings, its progress), binds the inputs to the
/// job's typed input record, runs the job through the same logic as <see cref="JobRunner"/>, and sends how it ended. The job's code is the same as in-process; only where its state lives differs.
/// </summary>
public static class RemoteJobHost
{
    /// <summary>Runs <typeparamref name="TJob"/> for the run the token names.</summary>
    public static Task<RunResult> RunAsync<TJob>(RunnerEndpoint endpoint, string token, CancellationToken ct, RemoteRunOptions? options = null)
        where TJob : RunnerJobBase, new() =>
        RunAsync(typeof(TJob), endpoint, token, ct, options);

    /// <summary>Runs <typeparamref name="TJob"/> against the endpoint and token the runner put in the environment of this process.</summary>
    public static Task<RunResult> RunAsync<TJob>(CancellationToken ct, RemoteRunOptions? options = null)
        where TJob : RunnerJobBase, new()
    {
        var (endpoint, token) = RunnerEndpoint.FromEnvironment();
        return RunAsync(typeof(TJob), endpoint, token, ct, options);
    }

    public static async Task<RunResult> RunAsync(Type jobType, RunnerEndpoint endpoint, string token, CancellationToken ct, RemoteRunOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(jobType);
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentException.ThrowIfNullOrEmpty(token);
        JobRunner.CheckJobType(jobType);
        options ??= new RemoteRunOptions();

        using var channel = endpoint.CreateChannel();
        var client = new RunnerApi.RunnerApiClient(channel);
        var headers = new Metadata { { RunnerProtocolNames.TokenHeader, token } };

        StartInfo info;
        try
        {
            info = await client.StartAsync(new StartRequest { ProtocolVersion = RunnerProtocol.V1, Client = options.Client }, headers, cancellationToken: ct).ResponseAsync.ConfigureAwait(false);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.FailedPrecondition or StatusCode.InvalidArgument)
        {
            throw new RunnerProtocolException(ex.StatusCode, ex.Status.Detail, ex);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            throw new IOException($"The runner at {endpoint} could not be reached: {ex.Status.Detail}", ex);
        }
        catch (RpcException ex)
        {
            throw new RunnerProtocolException(ex.StatusCode, ex.Status.Detail, ex);
        }

        if (info.ProtocolVersion != RunnerProtocol.V1)
            throw new RunnerProtocolException(StatusCode.FailedPrecondition, $"The runner speaks protocol '{info.ProtocolVersion}' and this kit speaks '{RunnerProtocol.V1}'; a task and a runner meet on one version.");

        var taskName = JobRunner.TaskNameOf(jobType);
        if (!string.Equals(info.Task, taskName, StringComparison.Ordinal))
            throw new ArgumentException($"The run is for the task '{info.Task}' and {jobType.Name} declares the task '{taskName}'; the runner started the wrong job.", nameof(jobType));

        var probe = (RunnerJobBase)(Activator.CreateInstance(jobType, nonPublic: true) ?? throw new InvalidOperationException($"{jobType.Name} cannot be created."));
        var input = InputBinder.FromStrings(probe.InputType, info.Inputs);

        await using var backend = new GrpcRunBackend(client, headers, info);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var grace = TimeSpan.FromMilliseconds(Math.Max(0, info.CancellationGraceMs));
        var cancelledByRunner = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watching = new CancellationTokenSource();
        var watchers = new[]
        {
            Task.Run(() => WatchCancelAsync(client, headers, cancelledByRunner, watching.Token), CancellationToken.None),
            Task.Run(() => HeartbeatAsync(client, headers, options.HeartbeatInterval, cancelledByRunner, watching.Token), CancellationToken.None),
        };
        _ = cancelledByRunner.Task.ContinueWith(_ =>
        {
            try
            {
                cancel.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // the run was over
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        var settings = new RunSettings(options.Hooks, options.MaxParallelism, Guid.CreateVersion7);
        var run = JobRunner.ExecuteAsync(jobType, backend, settings, options.SlotOpener, input, cancel.Token);
        try
        {
            var graceOver = cancelledByRunner.Task.ContinueWith(async _ => await Task.Delay(grace, CancellationToken.None).ConfigureAwait(false), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Unwrap();
            if (await Task.WhenAny(run, graceOver).ConfigureAwait(false) == run) return await run.ConfigureAwait(false);

            // the runner asked the job to stop and it did not within the grace period: the task ends the run itself (a real process would be killed next)
            _ = run.ContinueWith(r => _ = r.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            var reason = await cancelledByRunner.Task.ConfigureAwait(false);
            var end = TaskEndMapping.Cancelled($"{reason} The job did not stop within the grace period of {grace}.");
            await backend.FinishAsync(end, CancellationToken.None).ConfigureAwait(false);
            return new RunResult(backend.Identity.RunId, backend.Identity.Attempt, end, [], [], new Dictionary<string, long>(), null);
        }
        finally
        {
            await watching.CancelAsync().ConfigureAwait(false);
            try
            {
                await Task.WhenAll(watchers).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // the watchers end with their token
            }
        }
    }

    private static async Task WatchCancelAsync(RunnerApi.RunnerApiClient client, Metadata headers, TaskCompletionSource<string> cancelled, CancellationToken stop)
    {
        try
        {
            using var call = client.WatchCancel(new WatchCancelRequest(), headers, cancellationToken: stop);
            if (await call.ResponseStream.MoveNext(stop).ConfigureAwait(false)) cancelled.TrySetResult(call.ResponseStream.Current.Reason);
        }
        catch (Exception)
        {
            // the stream ends with the run, or the runner went away: the heartbeat and the job's own calls say so
        }
    }

    private static async Task HeartbeatAsync(RunnerApi.RunnerApiClient client, Metadata headers, TimeSpan every, TaskCompletionSource<string> cancelled, CancellationToken stop)
    {
        if (every <= TimeSpan.Zero) return;
        using var timer = new PeriodicTimer(every);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    var reply = await client.HeartbeatAsync(new HeartbeatRequest(), headers, cancellationToken: stop).ResponseAsync.ConfigureAwait(false);
                    if (reply.CancelRequested) cancelled.TrySetResult(reply.CancelReason);
                }
                catch (RpcException)
                {
                    // see WatchCancelAsync
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
