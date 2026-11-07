namespace DbDataKit.Runner;

/// <summary>A task of a channel's projection with the triggers it carries: what <see cref="TriggerGraph.FindCycle(IEnumerable{ProjectedTriggers})"/> reads.</summary>
public sealed record ProjectedTriggers(string Package, string Channel, string Task, IReadOnlyList<TaskTrigger> Triggers);

/// <summary>A loop of after-run triggers across the packages of one channel: the tasks in order as <c>package/task</c>, the first repeated at the end.</summary>
public sealed record TriggerCycle(string Channel, IReadOnlyList<string> Tasks)
{
    /// <summary>The loop in words: <c>a/extract runs, then b/load runs, then a/extract (channel 'prod')</c>.</summary>
    public override string ToString() => $"{string.Join(" runs, then ", Tasks)} (channel '{Channel}')";
}

/// <summary>A set of projected rows would make tasks start each other for ever: the publish that replaces the projection is refused, and the message names the loop.</summary>
public sealed class TriggerCycleException(TriggerCycle cycle)
    : InvalidOperationException($"The triggers form a loop, so the tasks would start each other for ever: {cycle}. Remove one of the 'after' triggers.")
{
    public TriggerCycle Cycle { get; } = cycle;
}
