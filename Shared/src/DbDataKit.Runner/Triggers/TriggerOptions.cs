namespace DbDataKit.Runner.Triggers;

/// <summary>How the triggers of a runner behave (plan 28a R13). Every value has a default that is right for a runner; a test or an installation changes one.</summary>
public sealed class TriggerOptions
{
    public const int DefaultMaxChainDepth = 16;

    /// <summary>
    /// The runtime guard against trigger loops: a run started by an after-run trigger is one level deeper than the run it follows (a run started by a schedule, by hand, by a webhook or by a file is level 0), and a trigger that
    /// would start a run deeper than this does nothing and says so (the log and the status). Default 16. A loop is refused when a package is published (<see cref="Scheduler.ProjectionDocuments.RequireNoCycle"/>); this is for the one
    /// that got in anyway.
    /// </summary>
    public int MaxChainDepth { get; init; } = DefaultMaxChainDepth;

    /// <summary>How often a file trigger looks at its folder when its manifest does not say (<c>poll</c>). Default 30 seconds.</summary>
    public TimeSpan FilePoll { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>A file modified more recently than this is left for the next look: it may still be being written. Default 2 seconds; zero looks at every file.</summary>
    public TimeSpan FileSettle { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>At most this many new files are queued by one look at a folder (the rest are found at the next). Default 100.</summary>
    public int FileBatch { get; init; } = 100;

    /// <summary>Where a connection slot's folder is. Null: this runner has none, and a file trigger says so in the status.</summary>
    public IFileSourceProvider? FileSources { get; init; }

    internal void Validate()
    {
        if (MaxChainDepth < 1) throw new ArgumentException("The chain depth is at least 1.", nameof(MaxChainDepth));
        if (FilePoll <= TimeSpan.Zero) throw new ArgumentException("The file poll is above zero.", nameof(FilePoll));
        if (FileSettle < TimeSpan.Zero) throw new ArgumentException("The file settle time is not negative.", nameof(FileSettle));
        if (FileBatch < 1) throw new ArgumentException("A look queues at least one file.", nameof(FileBatch));
    }
}

/// <summary>One trigger of a task as the engine sees it, for the status.</summary>
/// <param name="Task">The task the trigger starts.</param>
/// <param name="Kind"><c>after</c>, <c>webhook</c>, <c>file</c>, <c>store_change</c> or <c>manual</c>.</param>
/// <param name="Id">The trigger's id in <c>trigger_state</c> (<c>after:pkg/task</c>, <c>webhook</c>, <c>file:slot:path</c>).</param>
/// <param name="State">What it is doing, in words (<c>waiting for a change source (not built)</c>).</param>
/// <param name="LastFiredAt">When it last queued a run.</param>
/// <param name="Problem">What is wrong with it, or null.</param>
public sealed record TriggerStatus(TaskRef Task, string Kind, string Id, string State, DateTimeOffset? LastFiredAt, string? Problem);

/// <summary>What one pass of the engine did.</summary>
/// <param name="Queued">Items queued, by task.</param>
/// <param name="Skipped">Events that were seen and not acted on (a filter that did not match, a paused task, a chain too deep).</param>
public sealed record TriggerTickReport(IReadOnlyList<TaskRef> Queued, int Skipped);
