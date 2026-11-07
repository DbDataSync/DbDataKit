using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.Runner.Execution;
using DbDataKit.Scheduling;

namespace DbDataKit.Runner.Scheduler;

/// <summary>
/// What a work item of the runner carries (the item's payload, plan 28a R09): which task, on which version, with which inputs, which attempt of its chain, and whether an attempt has started.
/// <para>
/// <see cref="Version"/> is the version the item runs on. It is set when the item is queued and <b>kept</b> by every retry once an attempt has started (<see cref="Started"/>), so a retry never runs on a version activated after the first
/// attempt began (DR8); while no attempt has started it follows the active version. <see cref="Chain"/> is the same for every attempt of the item, so steps and units of an earlier attempt are reused (R03a, R03s).
/// </para>
/// </summary>
public sealed record WorkPayload
{
    public const string ScheduleTrigger = "schedule";
    public const string ManualTrigger = "manual";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The task as <c>package/channel/task</c> (<see cref="TaskRef"/>).</summary>
    public required string Task { get; init; }

    /// <summary>The version to run (text, as the projection names it).</summary>
    public required string Version { get; init; }

    /// <summary>The task's inputs as text (the kit binds them to the job's input record).</summary>
    public Dictionary<string, string> Inputs { get; init; } = [];

    /// <summary>The attempt this item is at: 1 for the first run, 2 for the first retry.</summary>
    public int Attempt { get; init; } = 1;

    /// <summary>The attempt chain, the same for all attempts of this item.</summary>
    public required string Chain { get; init; }

    /// <summary>True once a loop began an attempt on <see cref="Version"/>: from then on the version is not replaced.</summary>
    public bool Started { get; init; }

    /// <summary>What queued the item: <see cref="ScheduleTrigger"/> or <see cref="ManualTrigger"/> (R13 adds more).</summary>
    public string Trigger { get; init; } = ScheduleTrigger;

    /// <summary>Who asked for a manual run.</summary>
    public string? RequestedBy { get; init; }

    /// <summary>For an item a trigger queued (R13): how many triggered runs came before it in its chain (an after-run trigger adds one to the finished run's; an event that starts a chain is 0). The runtime guard of <c>TriggerOptions.MaxChainDepth</c> reads it.</summary>
    public int TriggerDepth { get; init; }

    /// <summary>For an item a trigger queued (R13): what set it off, in words: <c>run 3f2a... of pkg/prod/extract (succeeded)</c>, <c>file in/a.csv</c>, <c>webhook shop</c>. Never a body or a secret.</summary>
    public string? TriggerSource { get; init; }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Reads a payload; null when the text is none or not a payload (the caller reports that and drops the item).</summary>
    public static WorkPayload? TryParse(string? json, out string? problem)
    {
        problem = null;
        if (string.IsNullOrWhiteSpace(json))
        {
            problem = "The work item has no payload.";
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<WorkPayload>(json, Json);
            if (payload is null || payload.Attempt < 1 || !TaskRef.TryParse(payload.Task, out _))
            {
                problem = "The work item's payload does not name a task and an attempt.";
                return null;
            }

            return payload;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            problem = $"The work item's payload is not readable: {ex.Message}";
            return null;
        }
    }
}

/// <summary>
/// Puts a work item back for another attempt (plan 28a R08). The item stays the same row (so the same owner, task and place in the queue's rules), gets a new run id, so the attempt has a run record of its own, and is not claimable
/// before the plan's time (<c>available_at</c>, the queue's not-before). The payload moves to the next attempt: the attempt number, <see cref="WorkPayload.Started"/> true and the version the attempt ran on, the chain unchanged.
/// </summary>
public sealed class WorkItemRetries(ModuleWorkQueueStore queue)
{
    /// <summary>The payload the next attempt carries.</summary>
    public static WorkPayload NextPayload(WorkPayload current, RetryPlan plan, string ranVersion) =>
        current with { Attempt = plan.NextAttempt, Started = true, Version = ranVersion };

    /// <summary>Re-enqueues the item for the plan's attempt. Returns the new run id, or null when <paramref name="workerId"/> no longer holds the item (its lease ran out): another loop has it.</summary>
    public Guid? Reschedule(WorkItem item, string workerId, RetryPlan plan, WorkPayload next)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(next);
        if (next.Attempt != plan.NextAttempt) throw new ArgumentException($"The payload is at attempt {next.Attempt} and the plan's next attempt is {plan.NextAttempt}.", nameof(next));
        return queue.Reschedule(item.Id, workerId, plan.At, next.ToJson());
    }
}
