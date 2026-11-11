using System.Diagnostics;
using System.Threading.Channels;
using DbDataKit.State;

namespace DbDataKit.Runner.Kit;

/// <summary>A unit failed and the options said to stop at the first (plan 28b section 6): the run fails with that unit's error, its key as the failed item.</summary>
public sealed class UnitFailedException(TaskFailureKind kind, string unit, string key, Exception inner)
    : TaskFailureException(kind, $"The unit '{unit}' with key '{key}' failed and the run stops at the first failure: {inner.Message}", key, inner);

/// <summary>Runs units (plan 28b section 6): each item is begun in the store (skipped when it completed in this attempt chain), run with its own counts, and recorded done or failed.</summary>
internal sealed class UnitEngine(RunContext run, StepEngine steps)
{
    private IRunBackend Backend => run.Backend;

    public static async IAsyncEnumerable<T> Async<T>(IEnumerable<T> items)
    {
        foreach (var item in items) yield return item;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task RunAsync<T>(Unit<T> unit, IAsyncEnumerable<T> items, int parallelism, Func<T, CancellationToken, Task> body, CancellationToken ct, UnitOptions? options)
    {
        ArgumentNullException.ThrowIfNull(unit);
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(body);
        _ = steps;
        run.Register("unit", unit.Name, unit);
        var stop = options?.OnFailure == OnFailure.Stop;
        var workers = Math.Clamp(parallelism, 1, run.Settings.MaxParallelism ?? int.MaxValue);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queue = Channel.CreateBounded<T>(workers * 2);
        var state = new LoopState();

        async Task Produce()
        {
            try
            {
                await foreach (var item in items.WithCancellation(ct).ConfigureAwait(false))
                {
                    if (state.Fatal is not null) break;
                    if (state.Stopping)
                    {
                        await Backend.SkipUnitAsync(unit.Name, unit.KeyOf(item), CancellationToken.None).ConfigureAwait(false);     // the rest are recorded skipped
                        continue;
                    }

                    await queue.Writer.WriteAsync(item, ct).ConfigureAwait(false);
                }

                queue.Writer.Complete();
            }
            catch (Exception ex)
            {
                state.Die(ex);
                cts.Cancel();
                queue.Writer.Complete(ex);
            }
        }

        async Task Work()
        {
            try
            {
                await foreach (var item in queue.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (state.Fatal is not null) continue;          // the process "died": nothing more is recorded
                    var key = unit.KeyOf(item);
                    if (state.Stopping) { await Backend.SkipUnitAsync(unit.Name, key, CancellationToken.None).ConfigureAwait(false); continue; }
                    await ProcessAsync(unit, item, key, body, stop, state, cts, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                state.Die(ex);        // a store error or the like: nothing else can go on
                cts.Cancel();
                queue.Writer.TryComplete(ex);
            }
        }

        var tasks = new List<Task> { Task.Run(Produce, CancellationToken.None) };
        for (var i = 0; i < workers; i++) tasks.Add(Task.Run(Work, CancellationToken.None));
        Exception? error = null;
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            error = ex;
        }

        if (state.Fatal is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(state.Fatal).Throw();
        ct.ThrowIfCancellationRequested();
        if (state.First is { } first) throw new UnitFailedException(ExceptionClassifier.KindOf(first.Error), unit.Name, first.Key, first.Error);
        if (error is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
    }

    private sealed class LoopState
    {
        private int _stopping;

        public bool Stopping => Volatile.Read(ref _stopping) == 1;

        public (string Key, Exception Error)? First { get; private set; }

        public Exception? Fatal { get; private set; }

        public void Stop(string key, Exception error)
        {
            lock (this)
            {
                First ??= (key, error);
                Volatile.Write(ref _stopping, 1);
            }
        }

        public void Die(Exception error)
        {
            lock (this)
            {
                Fatal ??= error;
                Volatile.Write(ref _stopping, 1);
            }
        }
    }

    private async Task ProcessAsync<T>(Unit<T> unit, T item, string key, Func<T, CancellationToken, Task> body, bool stop, LoopState state, CancellationTokenSource cts, CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var begin = await Backend.BeginUnitAsync(unit.Name, key, ct).ConfigureAwait(false);
        if (begin == UnitBegin.Skip)
        {
            run.AddUnit(new UnitReport(unit.Name, key, "reused", clock.Elapsed, new Dictionary<string, long>(), null));
            return;
        }

        var scope = new CountScope(run.CurrentScope);
        try
        {
            run.Unit.Value = new UnitFrame(unit.Name, key);
            run.Scope.Value = scope;
            await run.BeforeUnitBodyAsync(unit.Name, key, ct).ConfigureAwait(false);
            await body(item, cts.Token).ConfigureAwait(false);
            await run.AfterUnitBodyAsync(unit.Name, key, ct).ConfigureAwait(false);
            await Backend.CompleteUnitAsync(unit.Name, key, scope.Snapshot(), ct).ConfigureAwait(false);
            run.AddUnit(new UnitReport(unit.Name, key, UnitState.Completed, clock.Elapsed, scope.Snapshot(), null));
        }
        catch (SimulatedCrashException ex)
        {
            state.Die(ex);
            cts.Cancel();
        }
        catch (OperationCanceledException ex) when (ct.IsCancellationRequested)
        {
            // the run was cancelled: the unit stays started and runs again on the retry
            state.Die(ex);
            cts.Cancel();
        }
        catch (OperationCanceledException) when (state.Stopping)
        {
            await Backend.FailUnitAsync(unit.Name, key, "Stopped before it finished: another unit failed and the run stops at the first failure.", scope.Snapshot(), CancellationToken.None).ConfigureAwait(false);
            run.AddUnit(new UnitReport(unit.Name, key, UnitState.Failed, clock.Elapsed, scope.Snapshot(), "stopped"));
        }
        catch (Exception ex)
        {
            await Backend.FailUnitAsync(unit.Name, key, $"{ex.GetType().Name}: {ex.Message}", scope.Snapshot(), CancellationToken.None).ConfigureAwait(false);
            run.AddUnit(new UnitReport(unit.Name, key, UnitState.Failed, clock.Elapsed, scope.Snapshot(), ex.Message));
            run.NoteFailedUnit(key);
            run.Backend.Log("Warning", $"The unit '{unit.Name}' with key '{key}' failed: {ex.GetType().Name}: {ex.Message}", key);
            if (stop)
            {
                state.Stop(key, ex);
                cts.Cancel();
            }
        }
        finally
        {
            run.Unit.Value = null;
            run.Scope.Value = null;
        }
    }
}
