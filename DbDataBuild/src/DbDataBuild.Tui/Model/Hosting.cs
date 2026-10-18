namespace DbDataBuild.Tui.Model;

public enum OptionKind { Flag, Text, Integer, Path, Choice, List }

/// <summary>What running a command can change, from nothing to the target's tables. Drives the confirmation the TUI asks for.</summary>
public enum Impact { None, RepoFiles, TrackingTables, TargetData, Target }

/// <param name="Name">The option's long name with its dashes (`--allow-risky`).</param>
/// <param name="Default">The default value as text (null when there is none).</param>
/// <param name="Choices">The values the option accepts, when it is a closed set.</param>
public sealed record OptionInfo(string Name, string Description, OptionKind Kind, string? Default, IReadOnlyList<string> Choices);

/// <param name="Repeatable">Takes several values (`models`).</param>
/// <param name="Required">At least one value must be given.</param>
public sealed record ArgumentInfo(string Name, string Description, bool Repeatable, bool Required, IReadOnlyList<string> Choices);

/// <param name="Effect">The effect class as the command prints it ("Target read-only").</param>
/// <param name="Lane">Where the command belongs in the life of a deployment: `inspect`, `deploy`, `refresh`, or empty.</param>
/// <param name="Disposition">What it reads and writes in one code (`P⇒S`: reads the project, writes structure).</param>
/// <param name="WriteFlag">When the command only changes something with a flag: `--write` (render, define), `--apply` (init), or `!--dry-run` (apply, which changes things unless the flag is given). Null: always, if <paramref name="Impact"/> is not None.</param>
public sealed record CommandInfo(string Name, string Purpose, string Effect, Impact Impact, IReadOnlyList<ArgumentInfo> Arguments, IReadOnlyList<OptionInfo> Options, string? WriteFlag = null, string Lane = "", string Disposition = "");

/// <summary>Whether a call changes something, from the command's write flag(s): `--write` changes only with it; `!--dry-run` changes unless it is given; `--a|--b` changes with either.</summary>
public static class WriteRules
{
    /// <param name="repoFiles">Whether writing the project's own files counts (the terminal asks before it; a server for an agent does not).</param>
    public static bool Changes(CommandInfo command, Func<string, bool> given, bool repoFiles)
    {
        if (command.Impact == Impact.None || (command.Impact == Impact.RepoFiles && !repoFiles)) return false;
        var flag = command.WriteFlag;
        if (flag == null) return true;
        if (given("--dry-run") && !given("--ack")) return false;          // a dry run executes nothing
        if (flag.StartsWith('!')) return !given(flag[1..]);
        return flag.Split('|').Any(given);
    }
}

/// <summary>What a command printed. The TUI always asks for `--format json`, so <see cref="Out"/> is one document.</summary>
public sealed record CommandResult(int Exit, string Out, string Err);

/// <summary>What the TUI can hand a running command: where to report progress as it happens, and a question the command asks between steps ("should I stop?").</summary>
public sealed record RunHooks(Action<string>? Progress = null, Func<bool>? StopRequested = null);

/// <summary>
/// The TUI's only way to do anything: the command catalog (read from the real command definitions, so a new option shows up here without work) and running a command
/// in process with arguments. The TUI is a client of the same machine interface a script or an agent uses.
/// </summary>
public interface ICommandHost
{
    IReadOnlyList<CommandInfo> Commands { get; }
    CommandResult Run(IReadOnlyList<string> args, RunHooks? hooks = null);
}
