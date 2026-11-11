using DbDataKit.State;

namespace DbDataKit.Runner.Execution;

/// <summary>
/// At the start of a new attempt of a chain, puts the steps an earlier attempt left <c>started</c> in doubt (plan 28b section 4): a step whose attempt died between the body and its result may or may not have
/// happened. <c>StepStore.Begin</c> reports such a step in doubt whether or not this ran; running it before the job starts makes the state visible to the console and to the operator, and means a run that never
/// reaches the step still leaves it marked.
/// </summary>
public sealed class InDoubtReconciler(StateDatabase database)
{
    /// <summary>
    /// Marks every step of <paramref name="chain"/> that is still started in an attempt before <paramref name="attempt"/>; returns them as they are now (in doubt). Nothing happens for the first attempt, or when no
    /// earlier attempt left a started step.
    /// </summary>
    public IReadOnlyList<StepRecord> Reconcile(TaskRef task, string chain, int attempt)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrEmpty(chain);
        if (attempt <= 1) return [];

        var steps = new StepStore(database, new ProgressKey(task.Package, task.Channel, task.Task));
        var dead = steps.List(chain).Where(s => s.State == StepState.Started && s.Attempt < attempt).Select(s => s.Attempt).Distinct().Order().ToList();
        if (dead.Count == 0) return [];

        var marked = new List<StepRecord>();
        foreach (var earlier in dead) marked.AddRange(steps.MarkInDoubt(chain, earlier));
        return marked.Where(s => s.Attempt < attempt).ToList();
    }
}
