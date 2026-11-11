namespace DbDataKit.Runner.Execution;

/// <summary>What the scheduler needs of an executor: run one task and say how it ended. <see cref="TaskExecutor"/> is the real one; a test passes a fake.</summary>
public interface ITaskRunner
{
    /// <summary>Runs the task and returns when it has ended and its run record is finished. A cancel is a result (a stopped run), not an exception.</summary>
    Task<TaskRunResult> RunAsync(TaskRunRequest request, CancellationToken ct = default);
}
