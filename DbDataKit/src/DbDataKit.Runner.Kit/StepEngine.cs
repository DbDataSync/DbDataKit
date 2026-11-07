using System.Diagnostics;
using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit;

/// <summary>What a step does when it is in doubt (it began in an earlier attempt and nothing says whether it finished): plan 28b section 4.</summary>
internal enum Recovery
{
    /// <summary>Run the body again.</summary>
    Repeatable,
    /// <summary>Call the check: a result is stored and used, none runs the body.</summary>
    Check,
    /// <summary>Stop the run, naming the step and its key; an operator says whether it happened.</summary>
    Ask,
}

/// <summary>
/// Runs steps against the backend (plan 28b section 4): Begin says reuse, run or in doubt; a body's result is stored before the step returns; recovery decides the in-doubt case. The same code runs over every
/// <see cref="IRunBackend"/>.
/// </summary>
internal sealed class StepEngine(RunContext run)
{
    private IRunBackend Backend => run.Backend;

    /// <summary>The key a step is stored under: inside a unit it includes the unit's key.</summary>
    private string StoredKey(string key) => run.Unit.Value is { } unit ? $"{unit.Name}/{unit.Key}::{key}" : key;

    private void Enter(string name)
    {
        if (run.Step.Value is { } outer)
            throw new InvalidOperationException($"The step '{name}' was called inside the body of the step '{outer.Name}' (key '{outer.Key}'). A step is the smallest piece and may not contain another step: compose steps in the job.");
    }

    // ---- value steps

    public async Task<TOut> RunValueAsync<TIn, TOut>(ValueStep<TIn, TOut> step, TIn input, Recovery recovery, Func<CancellationToken, Task<TOut?>>? check, Func<CancellationToken, Task<TOut>> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Enter(step.Name);
        run.Register("step", step.Name, step);
        var key = StoredKey(step.KeyOf(input));
        var begin = await Backend.BeginStepAsync(step.Name, key, ct).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        switch (begin)
        {
            case StepBegin.Reuse reuse:
                run.AddStep(new StepReport(step.Name, key, StepStatus.Reused, clock.Elapsed, reuse.Step.Counts));
                return JsonSerializer.Deserialize<TOut>(reuse.ResultJson ?? "null", RunContext.Json)!;
            case StepBegin.InDoubt doubt:
                switch (recovery)
                {
                    case Recovery.Ask:
                        throw new TaskFailureException(TaskFailureKind.Permanent,
                            $"The step '{step.Name}' with key '{key}' began in attempt {doubt.Step.Attempt} and nothing says whether it finished, and its recovery is Ask: an operator marks it as having happened (with its result) or not in the console, and the run is retried.", key);
                    case Recovery.Check:
                        var found = await check!(ct).ConfigureAwait(false);
                        if (found is not null)
                        {
                            var json = run.SerializeResult(step.Name, found);
                            await Backend.RetakeStepAsync(step.Name, key, ct).ConfigureAwait(false);
                            await Backend.CompleteValueStepAsync(step.Name, key, json, null, ct).ConfigureAwait(false);
                            run.AddStep(new StepReport(step.Name, key, StepStatus.Recovered, clock.Elapsed, new Dictionary<string, long>()));
                            return found;
                        }

                        break;
                }

                await Backend.RetakeStepAsync(step.Name, key, ct).ConfigureAwait(false);
                break;
        }

        return await ExecuteValueAsync(step, key, body, clock, ct).ConfigureAwait(false);
    }

    private async Task<TOut> ExecuteValueAsync<TIn, TOut>(ValueStep<TIn, TOut> step, string key, Func<CancellationToken, Task<TOut>> body, Stopwatch clock, CancellationToken ct)
    {
        var scope = new CountScope(run.CurrentScope);
        TOut result;
        try
        {
            run.Step.Value = new StepFrame(step.Name, key);
            run.Scope.Value = scope;
            await run.BeforeStepBodyAsync(step.Name, key, ct).ConfigureAwait(false);
            result = await body(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await StepThrewAsync(ex, step.Name, key, scope, clock, ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            run.Step.Value = null;
            run.Scope.Value = null;
        }

        await run.AfterStepBodyAsync(step.Name, key, ct).ConfigureAwait(false);
        try
        {
            var json = run.SerializeResult(step.Name, result);
            await Backend.CompleteValueStepAsync(step.Name, key, json, scope.Snapshot(), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is StepResultTooLargeException or InvalidOperationException or ArgumentException)
        {
            await Backend.FailStepAsync(step.Name, key, ex.Message, scope.Snapshot(), ct).ConfigureAwait(false);
            run.AddStep(new StepReport(step.Name, key, StepStatus.Failed, clock.Elapsed, scope.Snapshot()));
            throw;
        }

        run.AddStep(new StepReport(step.Name, key, StepStatus.Ran, clock.Elapsed, scope.Snapshot()));
        return result;
    }

    // ---- stream steps

    public async Task<PayloadRef> RunStreamAsync<TIn>(StreamStep<TIn> step, TIn input, Recovery recovery, Func<CancellationToken, Task<PayloadRef?>>? check, Func<Stream, CancellationToken, Task> body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        Enter(step.Name);
        run.Register("step", step.Name, step);
        var key = StoredKey(step.KeyOf(input));
        var begin = await Backend.BeginStepAsync(step.Name, key, ct).ConfigureAwait(false);
        var clock = Stopwatch.StartNew();
        StepBegin.Run? toRun = null;
        switch (begin)
        {
            case StepBegin.Reuse reuse:
                run.AddStep(new StepReport(step.Name, key, StepStatus.Reused, clock.Elapsed, reuse.Step.Counts));
                return Backend.GetPayload(reuse.ResultPayload ?? throw new InvalidOperationException($"The step '{step.Name}' (key '{key}') completed with no payload; it is not a streaming step's result."));
            case StepBegin.Run fresh:
                toRun = fresh;
                break;
            case StepBegin.InDoubt doubt:
                // a complete payload owned by the step proves the body finished writing (a payload is complete only when the body returned normally): adopt it, whatever the recovery says (plan 28b K14)
                if (await Backend.AdoptStepPayloadAsync(step.Name, key, ct).ConfigureAwait(false) is { } adopted)
                {
                    run.AddStep(new StepReport(step.Name, key, StepStatus.Recovered, clock.Elapsed, new Dictionary<string, long>()));
                    return Backend.GetPayload(adopted);
                }

                switch (recovery)
                {
                    case Recovery.Ask:
                        throw new TaskFailureException(TaskFailureKind.Permanent,
                            $"The step '{step.Name}' with key '{key}' began in attempt {doubt.Step.Attempt} and nothing says whether it finished, and its recovery is Ask: an operator marks it as having happened (with its payload) or not in the console, and the run is retried.", key);
                    case Recovery.Check:
                        var found = await check!(ct).ConfigureAwait(false);
                        if (found is not null)
                        {
                            await Backend.RetakeStepAsync(step.Name, key, ct).ConfigureAwait(false);
                            await Backend.CompleteStreamStepAsync(step.Name, key, found.Id, null, ct).ConfigureAwait(false);
                            run.AddStep(new StepReport(step.Name, key, StepStatus.Recovered, clock.Elapsed, new Dictionary<string, long>()));
                            return found;
                        }

                        break;
                }

                toRun = await Backend.RetakeStepAsync(step.Name, key, ct).ConfigureAwait(false);
                break;
        }

        var scope = new CountScope(run.CurrentScope);
        StepStream stream = await Backend.OpenStepStreamAsync(toRun!, step.Name, key, step.MediaType, ct).ConfigureAwait(false);
        try
        {
            run.Step.Value = new StepFrame(step.Name, key);
            run.Scope.Value = scope;
            await run.BeforeStepBodyAsync(step.Name, key, ct).ConfigureAwait(false);
            await body(stream.Stream, ct).ConfigureAwait(false);
            await run.BeforePayloadCommitAsync(step.Name, key, ct).ConfigureAwait(false);
            stream.Complete();                                                  // the payload commits: from here on a crash leaves a complete payload the retry adopts
            await run.AfterStepBodyAsync(step.Name, key, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await stream.Stream.DisposeAsync().ConfigureAwait(false);
            await StepThrewAsync(ex, step.Name, key, scope, clock, ct).ConfigureAwait(false);
            throw;
        }
        finally
        {
            run.Step.Value = null;
            run.Scope.Value = null;
        }

        await stream.Stream.DisposeAsync().ConfigureAwait(false);
        try
        {
            await Backend.CompleteStreamStepAsync(step.Name, key, stream.PayloadId(), scope.Snapshot(), ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            await Backend.FailStepAsync(step.Name, key, ex.Message, scope.Snapshot(), ct).ConfigureAwait(false);
            run.AddStep(new StepReport(step.Name, key, StepStatus.Failed, clock.Elapsed, scope.Snapshot()));
            throw;
        }

        run.AddStep(new StepReport(step.Name, key, StepStatus.Ran, clock.Elapsed, scope.Snapshot()));
        return Backend.GetPayload(stream.PayloadId());
    }

    /// <summary>
    /// A body that threw. A crash stand-in leaves the step started (in doubt next time, as a dead process would). A cancellation does too: the body stopped part way and nothing says what it had done. Any
    /// other exception is the body saying it failed, and the step is recorded failed (a retry runs it again).
    /// </summary>
    private async Task StepThrewAsync(Exception ex, string name, string key, CountScope scope, Stopwatch clock, CancellationToken ct)
    {
        if (ex is SimulatedCrashException || ex is OperationCanceledException) return;
        await Backend.FailStepAsync(name, key, $"{ex.GetType().Name}: {ex.Message}", scope.Snapshot(), CancellationToken.None).ConfigureAwait(false);
        run.AddStep(new StepReport(name, key, StepStatus.Failed, clock.Elapsed, scope.Snapshot()));
    }
}

/// <summary>The call of a value step before its recovery is chosen: only <see cref="Repeatable"/>, <see cref="Ask"/> and <see cref="Check"/> exist (plan 28b K11).</summary>
public sealed class StepCall<TIn, TOut>
{
    private readonly StepEngine _engine;
    private readonly ValueStep<TIn, TOut> _step;
    private readonly TIn _input;

    internal StepCall(StepEngine engine, ValueStep<TIn, TOut> step, TIn input)
    {
        _engine = engine;
        _step = step;
        _input = input;
    }

    /// <summary>A crash in the middle means: run the body again (it is harmless to repeat).</summary>
    public ValueRunner<TIn, TOut> Repeatable() => new(_engine, _step, _input, Recovery.Repeatable, null);

    /// <summary>A crash in the middle means: stop and ask an operator.</summary>
    public ValueRunner<TIn, TOut> Ask() => new(_engine, _step, _input, Recovery.Ask, null);

    /// <summary>A crash in the middle means: call <paramref name="check"/>; a non-null result is the step's result and the body does not run, null runs the body.</summary>
    public ValueRunner<TIn, TOut> Check(Func<CancellationToken, Task<TOut?>> check) => new(_engine, _step, _input, Recovery.Check, check ?? throw new ArgumentNullException(nameof(check)));
}

/// <summary>A value step with its recovery chosen: it can run.</summary>
public sealed class ValueRunner<TIn, TOut>
{
    private readonly StepEngine _engine;
    private readonly ValueStep<TIn, TOut> _step;
    private readonly TIn _input;
    private readonly Recovery _recovery;
    private readonly Func<CancellationToken, Task<TOut?>>? _check;

    internal ValueRunner(StepEngine engine, ValueStep<TIn, TOut> step, TIn input, Recovery recovery, Func<CancellationToken, Task<TOut?>>? check)
    {
        _engine = engine;
        _step = step;
        _input = input;
        _recovery = recovery;
        _check = check;
    }

    /// <summary>Runs the body at most once per key in the attempt chain and returns its result, stored before this returns; a retry answers from memory.</summary>
    public Task<TOut> RunAsync(Func<CancellationToken, Task<TOut>> body, CancellationToken ct) => _engine.RunValueAsync(_step, _input, _recovery, _check, body, ct);
}

/// <summary>The call of a streaming step before its recovery is chosen.</summary>
public sealed class StreamStepCall<TIn>
{
    private readonly StepEngine _engine;
    private readonly StreamStep<TIn> _step;
    private readonly TIn _input;

    internal StreamStepCall(StepEngine engine, StreamStep<TIn> step, TIn input)
    {
        _engine = engine;
        _step = step;
        _input = input;
    }

    public StreamRunner<TIn> Repeatable() => new(_engine, _step, _input, Recovery.Repeatable, null);

    public StreamRunner<TIn> Ask() => new(_engine, _step, _input, Recovery.Ask, null);

    public StreamRunner<TIn> Check(Func<CancellationToken, Task<PayloadRef?>> check) => new(_engine, _step, _input, Recovery.Check, check ?? throw new ArgumentNullException(nameof(check)));
}

/// <summary>A streaming step with its recovery chosen: it can stream.</summary>
public sealed class StreamRunner<TIn>
{
    private readonly StepEngine _engine;
    private readonly StreamStep<TIn> _step;
    private readonly TIn _input;
    private readonly Recovery _recovery;
    private readonly Func<CancellationToken, Task<PayloadRef?>>? _check;

    internal StreamRunner(StepEngine engine, StreamStep<TIn> step, TIn input, Recovery recovery, Func<CancellationToken, Task<PayloadRef?>>? check)
    {
        _engine = engine;
        _step = step;
        _input = input;
        _recovery = recovery;
        _check = check;
    }

    /// <summary>
    /// Hands the body a write-only stream that is a payload writer (chunks of 1 MiB, SHA-256 as it goes, one chunk in memory). The payload is complete only when the body returns normally; the returned
    /// <see cref="PayloadRef"/> reads it back.
    /// </summary>
    public Task<PayloadRef> StreamAsync(Func<Stream, CancellationToken, Task> body, CancellationToken ct) => _engine.RunStreamAsync(_step, _input, _recovery, _check, body, ct);
}
