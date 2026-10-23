namespace DbDataKit.Cli;

/// <summary>One verb of a noun: <c>server run</c> is the verb <c>run</c> of the noun <c>server</c>.</summary>
/// <param name="Name">The verb as typed (<c>run</c>).</param>
/// <param name="Summary">One line for the help.</param>
/// <param name="Effect">What the verb may touch; the management API offers it by this.</param>
/// <param name="Usage">The words after the verb in the usage line (<c>[--url &lt;url&gt;]</c>), empty for none.</param>
/// <param name="Run">Runs the verb with the arguments after it and returns the exit code.</param>
public sealed record Verb(string Name, string Summary, EffectClass Effect, string Usage, Func<string[], CliContext, Task<int>> Run);

/// <summary>
/// A group of verbs on one thing, identical in every command line that has it (plan 13, W4). The shared nouns are built here once; a product supplies what is its own
/// (how its server starts, how its configuration is read) as small interfaces and adds its own verbs with <see cref="Noun.With"/>.
/// </summary>
public sealed class Noun(string name, string summary, IReadOnlyList<Verb> verbs)
{
    public string Name { get; } = name;

    public string Summary { get; } = summary;

    public IReadOnlyList<Verb> Verbs { get; } = verbs;

    /// <summary>The same noun with more verbs, for the ones a product adds (<c>cert</c> plus its own, <c>server</c> plus <c>project add</c>).</summary>
    public Noun With(params Verb[] more) => new(Name, Summary, [.. Verbs, .. more]);

    /// <summary>The verbs as command specs (<c>server run</c>), for a product's command table and the help.</summary>
    public IEnumerable<CommandSpec> Specs() => Verbs.Select(v => new CommandSpec($"{Name} {v.Name}", v.Effect, v.Summary, true));
}

/// <summary>Dispatches <c>&lt;noun&gt; &lt;verb&gt; ...</c> and prints the help, the same way in every command line.</summary>
public sealed class NounRouter(string program, IEnumerable<Noun> nouns)
{
    private readonly Noun[] _nouns = [.. nouns];

    public IReadOnlyList<Noun> Nouns => _nouns;

    public async Task<int> RunAsync(string[] args, CliContext context)
    {
        if (args.Length == 0 || args[0] is "help" or "--help" or "-h")
        {
            await context.Out.WriteAsync(Help());
            return args.Length == 0 ? CliExit.Usage : CliExit.Ok;
        }

        var noun = _nouns.FirstOrDefault(n => n.Name == args[0]);
        if (noun is null)
        {
            await context.Error.WriteLineAsync($"{program}: '{args[0]}' is not a command. Run '{program} help' for the list.");
            return CliExit.Usage;
        }

        if (args.Length == 1 || args[1] is "help" or "--help" or "-h")
        {
            await context.Out.WriteAsync(NounHelp(noun));
            return args.Length == 1 ? CliExit.Usage : CliExit.Ok;
        }

        var verb = noun.Verbs.FirstOrDefault(v => v.Name == args[1]);
        if (verb is null)
        {
            await context.Error.WriteLineAsync($"{program} {noun.Name}: '{args[1]}' is not a verb. Run '{program} {noun.Name} help' for the list.");
            return CliExit.Usage;
        }

        return await verb.Run(args[2..], context);
    }

    /// <summary>The list of nouns, one line each. Deterministic, so a snapshot test can pin it.</summary>
    public string Help()
    {
        var width = _nouns.Max(n => n.Name.Length);
        var lines = new List<string> { $"Usage: {program} <noun> <verb> [options]", "", "Commands:" };
        lines.AddRange(_nouns.Select(n => $"  {n.Name.PadRight(width)}  {n.Summary}"));
        lines.AddRange(["", $"Run '{program} <noun> help' for the verbs of one."]);
        return string.Join('\n', lines) + "\n";
    }

    public string NounHelp(Noun noun)
    {
        var lines = new List<string> { $"{program} {noun.Name}: {noun.Summary}", "", "Verbs:" };
        foreach (var verb in noun.Verbs)
        {
            lines.Add($"  {noun.Name} {verb.Name}{(verb.Usage.Length > 0 ? " " + verb.Usage : "")}");
            lines.Add($"      {verb.Summary} [{verb.Effect.Describe()}]");
        }
        return string.Join('\n', lines) + "\n";
    }
}
