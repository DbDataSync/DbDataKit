using System.Net.Sockets;
using System.Reflection;
using DbDataKit.Connections;

namespace DbDataKit.Runner.Kit;

/// <summary>Turns what a job threw into the failure the runner records (plan 28b section 8; the mapping to outcomes and retries is plan 28a R02's).</summary>
internal static class ExceptionClassifier
{
    public static Exception Unwrap(Exception ex)
    {
        while (ex is AggregateException { InnerExceptions.Count: 1 } aggregate) ex = aggregate.InnerExceptions[0];
        return ex;
    }

    public static TaskFailureKind KindOf(Exception ex) => Failure(ex, null).Kind;

    /// <summary>
    /// A <see cref="TaskFailureException"/> says its own kind; a <see cref="ConnectionFailureException"/> is classified by the connection (network and timeout are temporary); I/O, timeouts and network errors are
    /// temporary; everything else is permanent, because retrying an unknown problem only repeats it.
    /// </summary>
    public static TaskFailure Failure(Exception ex, string? item)
    {
        ex = Unwrap(ex);
        return ex switch
        {
            TaskFailureException t => new TaskFailure(t.Kind, t.Message, t.Item ?? item),
            ConnectionFailureException c => TaskFailure.From(c.Failure, item),
            FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException => new TaskFailure(TaskFailureKind.Permanent, ex.Message, item),
            TimeoutException or IOException or HttpRequestException or SocketException => new TaskFailure(TaskFailureKind.Temporary, ex.Message, item),
            OperationCanceledException => new TaskFailure(TaskFailureKind.Temporary, "An operation was cancelled by its own timeout: " + ex.Message, item),
            _ => new TaskFailure(TaskFailureKind.Permanent, $"{ex.GetType().Name}: {ex.Message}", item),
        };
    }
}

/// <summary>
/// Runs a job in this process, over the real stores (plan 28b section 12): creates it, binds its input, progress and slots, runs it, and records the outcome through R02's mapping. The checkpoint moves in the
/// transaction that finishes the run record, so a run that fails leaves it where it was.
/// </summary>
public static class JobRunner
{
    /// <summary>The task name a job class declares with <see cref="RunnerTaskAttribute"/>.</summary>
    public static string TaskNameOf(Type job) =>
        job.GetCustomAttribute<RunnerTaskAttribute>(inherit: false)?.Name ?? throw new ArgumentException($"{job.Name} has no [RunnerTask(\"name\")]: a job class names its task.", nameof(job));

    /// <summary>
    /// Runs <typeparamref name="TJob"/> once. <paramref name="chain"/> is the attempt chain (the work item and its retries): a retry passes the same chain with the next <paramref name="attempt"/>, and the steps and
    /// units of the chain answer from memory. <paramref name="input"/> is the job's input record (null: the default of its type).
    /// </summary>
    public static Task<RunResult> RunAsync<TJob>(RunnerEnvironment environment, string chain, int attempt, object? input, CancellationToken ct, Guid? runId = null)
        where TJob : RunnerJobBase, new() =>
        RunAsync(typeof(TJob), environment, chain, attempt, input, ct, runId);

    public static async Task<RunResult> RunAsync(Type jobType, RunnerEnvironment environment, string chain, int attempt, object? input, CancellationToken ct, Guid? runId = null)
    {
        ArgumentNullException.ThrowIfNull(jobType);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentException.ThrowIfNullOrEmpty(chain);
        if (attempt < 1) throw new ArgumentOutOfRangeException(nameof(attempt), "The first attempt is 1.");
        if (!typeof(RunnerJobBase).IsAssignableFrom(jobType) || jobType.IsAbstract) throw new ArgumentException($"{jobType.Name} is not a job class (a class deriving from RunnerJob<,,>).", nameof(jobType));

        var task = new TaskRef(environment.Package, environment.Channel, TaskNameOf(jobType));
        var job = (RunnerJobBase)(Activator.CreateInstance(jobType, nonPublic: true) ?? throw new InvalidOperationException($"{jobType.Name} cannot be created."));
        var progress = (Progress)Activator.CreateInstance(job.ProgressType)!;
        var slots = (Slots)Activator.CreateInstance(job.SlotsType)!;
        var layout = ProgressLayout.For(job.ProgressType);
        var inputValue = input ?? DefaultInput(job.InputType);
        if (!job.InputType.IsInstanceOfType(inputValue)) throw new ArgumentException($"{jobType.Name} takes an input of type {job.InputType.Name}, not {inputValue.GetType().Name}.", nameof(input));

        var identity = new RunIdentity(runId ?? environment.Database.NewId(), attempt, chain, task);
        var backend = new InProcessRun(environment, identity);
        await backend.StartAsync(ct).ConfigureAwait(false);
        var context = new RunContext(backend, environment);
        var binder = new ProgressBinder(backend, layout);
        context.Progress = binder;
        BindSlots(slots, environment.SlotOpener);
        job.Bind(inputValue, progress, slots, context);

        TaskEnd end;
        var crashed = false;
        try
        {
            await binder.BindAsync(progress, ct).ConfigureAwait(false);
            await job.ExecuteAsync(ct).ConfigureAwait(false);
            end = Success(context);
            if (!await binder.SaveAsync(end, ct).ConfigureAwait(false))
            {
                end = TaskEndMapping.Cancelled("A checkpoint of this task moved since the run read it (another run got there first, or it was rewound); the run's progress was not saved.", Processed(context));
                await backend.FinishAsync(end, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (SimulatedCrashException)
        {
            crashed = true;
            throw;
        }
        catch (Exception ex)
        {
            end = await FailedAsync(ex, context, ct, backend).ConfigureAwait(false);
        }
        finally
        {
            if (!crashed) await ReleaseInboxAsync(context, backend).ConfigureAwait(false);
        }

        Summarize(context, end);
        return new RunResult(identity.RunId, attempt, end, context.StepReports, context.UnitReports, context.Root.Snapshot(), context.LastStatus);
    }

    private static object DefaultInput(Type type)
    {
        try
        {
            return Activator.CreateInstance(type) ?? throw new InvalidOperationException();
        }
        catch (Exception ex) when (ex is MissingMethodException or InvalidOperationException)
        {
            throw new ArgumentException($"The job takes an input of type {type.Name}, which has no parameterless constructor: pass the input.", "input", ex);
        }
    }

    private static void BindSlots(Slots slots, SlotOpener? opener)
    {
        slots.Opener = opener ?? ((slot, _, _) => throw new InvalidOperationException($"The slot '{slot}' cannot be opened: this run has no connection binding (RunnerEnvironment.SlotOpener)."));
        foreach (var property in slots.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            if (typeof(SlotBase).IsAssignableFrom(property.PropertyType) && property.GetValue(slots) is SlotBase slot) slot.Owner = slots;
        foreach (var field in slots.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (typeof(SlotBase).IsAssignableFrom(field.FieldType) && field.GetValue(slots) is SlotBase slot) slot.Owner = slots;
    }

    /// <summary>The count a run reports as processed: the counter named <c>rows</c>, else all counters added.</summary>
    private static long Processed(RunContext context)
    {
        var counts = context.Root.Snapshot();
        return counts.TryGetValue("rows", out var rows) ? rows : counts.Values.Sum();
    }

    private static TaskEnd Success(RunContext context)
    {
        var failed = context.FailedUnits + context.Rejected;
        return failed > 0
            ? new TaskEnd(TaskEndState.CompletedWithFailures, Processed(context), failed, context.FirstFailedItem)
            : new TaskEnd(TaskEndState.Succeeded, Processed(context));
    }

    private static async Task<TaskEnd> FailedAsync(Exception thrown, RunContext context, CancellationToken ct, IRunBackend backend)
    {
        var ex = ExceptionClassifier.Unwrap(thrown);
        TaskEnd end;
        if (ex is ProgressConflictException conflict) end = TaskEndMapping.Cancelled(conflict.Message, Processed(context));
        else if (ex is OperationCanceledException && ct.IsCancellationRequested) end = TaskEndMapping.Cancelled(null, Processed(context));
        else
        {
            var failure = ExceptionClassifier.Failure(ex, context.FirstFailedItem);
            end = new TaskEnd(TaskEndState.Failed, Processed(context), context.FailedUnits + context.Rejected, context.FirstFailedItem, failure);
        }

        await backend.FinishAsync(end, CancellationToken.None).ConfigureAwait(false);
        return end;
    }

    private static async Task ReleaseInboxAsync(RunContext context, IRunBackend backend)
    {
        foreach (var id in context.OpenInbox.Keys) await backend.ReleaseInboxAsync(id, CancellationToken.None).ConfigureAwait(false);
        context.OpenInbox.Clear();
    }

    private static void Summarize(RunContext context, TaskEnd end)
    {
        var counts = context.Root.Snapshot();
        var text = counts.Count == 0 ? "no counts" : string.Join(", ", counts.OrderBy(c => c.Key, StringComparer.Ordinal).Select(c => $"{c.Key} {c.Value}"));
        context.Backend.Log(end.State == TaskEndState.Failed ? "Error" : "Information", $"Run ended {end.State}: {text}" + (context.LastStatus is null ? "" : $"; last status: {context.LastStatus}"));
    }
}
