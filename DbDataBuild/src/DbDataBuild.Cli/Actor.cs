namespace DbDataBuild.Cli;

/// <summary>
/// Who a command acts for (plan 13, R03). A command run from a terminal acts for the login it connects with, or the operating-system user. A command the Build server runs for a paired console acts for the person who
/// called it, so the server hands the child <see cref="Variable"/> ("Ada via Main console"): the tracking tables then name that person as the one who applied a plan, acknowledged a block or kept a plan, not the service account the
/// server runs as. The server sets the variable for every child, or removes it, so a value in the server's own environment never reaches a command.
/// </summary>
public static class Actor
{
    public const string Variable = "DBDATABUILD_ACTOR";

    /// <summary>The name to record: the actor the server named, else <paramref name="fallback"/>.</summary>
    public static string Of(Func<string, string?> env, string fallback) => env(Variable) is { Length: > 0 } actor ? actor : fallback;
}
