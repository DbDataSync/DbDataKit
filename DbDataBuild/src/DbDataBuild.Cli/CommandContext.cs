namespace DbDataBuild.Cli;

/// <summary>
/// What a caller that runs a command in process (the terminal interface) can hand it: a place to report progress as it happens, and a way to ask it to stop between steps.
/// The command's own output is a document delivered at the end; this is the live side. It flows with the command (AsyncLocal), so concurrent callers do not see each other's.
/// </summary>
public sealed record CommandHooks(Action<string>? Progress = null, Func<bool>? StopRequested = null);

public static class CommandContext
{
    private static readonly AsyncLocal<CommandHooks?> Current = new();

    public static CommandHooks? Hooks => Current.Value;

    /// <summary>Runs <paramref name="run"/> with the hooks in force for everything it starts.</summary>
    public static T With<T>(CommandHooks hooks, Func<T> run)
    {
        var before = Current.Value;
        Current.Value = hooks;
        try { return run(); }
        finally { Current.Value = before; }
    }
}
