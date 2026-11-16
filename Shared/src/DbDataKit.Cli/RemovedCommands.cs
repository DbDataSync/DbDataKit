namespace DbDataKit.Cli;

/// <summary>
/// The commands of a command line that went with its server (plan 33 SC23: the Build, Sync and Store servers were deleted, so the install is four services). A clean break, never a silent one: a removed command is not
/// "not a command", it answers with the sentence that names where its feature went, and exits with <see cref="CliExit.Usage"/>. A key is a noun (<c>server</c>) or a noun and verb (<c>model put</c>); the longer one wins.
/// </summary>
public sealed class RemovedCommands
{
    private readonly string _program;
    private readonly Dictionary<string, string> _moved;

    /// <param name="program">The program's name, which starts each answer.</param>
    /// <param name="moved">For each removed noun, or noun and verb, the sentence naming where its feature went.</param>
    public RemovedCommands(string program, IReadOnlyDictionary<string, string> moved)
    {
        _program = program;
        _moved = new Dictionary<string, string>(moved, StringComparer.Ordinal);
    }

    /// <summary>The removed nouns and verbs, with their sentences.</summary>
    public IReadOnlyDictionary<string, string> Moved => _moved;

    /// <summary>The answer to <paramref name="args"/> when it names a removed command, else null.</summary>
    public string? AnswerFor(string[] args)
    {
        var words = args.Where(a => !a.StartsWith('-')).Take(2).ToArray();
        if (words.Length == 0) return null;
        if (words.Length == 2 && _moved.TryGetValue($"{words[0]} {words[1]}", out var verb)) return $"'{_program} {words[0]} {words[1]}' is gone: {verb}";
        return _moved.TryGetValue(words[0], out var noun) ? $"'{_program} {words[0]}' is gone: {noun}" : null;
    }

    /// <summary>Writes the answer and returns <see cref="CliExit.Usage"/> when <paramref name="args"/> names a removed command; null when it does not.</summary>
    public async Task<int?> TryAnswerAsync(string[] args, TextWriter error)
    {
        if (AnswerFor(args) is not { } answer) return null;
        await error.WriteLineAsync(answer).ConfigureAwait(false);
        return CliExit.Usage;
    }
}
